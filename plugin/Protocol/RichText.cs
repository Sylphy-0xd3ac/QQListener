using System.Text;

namespace QQListener.Protocol;

/// <summary>
/// RichText.elems 的解码：把线上的元素列表变成 <see cref="Segment"/>。
///
/// Elem 子字段号来自 SnowLuma proto-defs element.ts 的 Elem 接口（权威，勿改）。
/// </summary>
public static class RichText
{
    public const int ElemText = 1;
    public const int ElemFace = 2;
    public const int ElemNotOnlineImage = 4;
    public const int ElemTrans = 5;
    public const int ElemCustomFace = 8;      // NT QQ 群图片多走此元素
    public const int ElemGroupFile = 13;
    public const int ElemVideoFile = 19;
    public const int ElemSrcMsg = 45;         // 回复/引用
    public const int ElemCommon = 53;

    /// <summary>RichText.elems 的字段号（已由真实 NTQQ OlPush 帧校准）。</summary>
    public const int RichTextElems = 2;

    // TextElem 子字段：str=1, attr6Buf=3, pbReserve=12
    private const int TextStr = 1;
    private const int TextAttr6 = 3;
    private const int TextPbReserve = 12;

    // @全体成员在 wire 上 uin=0，只能靠文本认。
    private static readonly HashSet<string> AtAllTexts = ["@全体成员", "@all", "@everyone"];

    // ExtBizInfo.pic.bytesPbReserveC2c(11).field30 里带着这条消息自己的 rkey。
    // 少了它 CDN 会以 `invalid rkey` 拒绝下载——图片就是这样"炸"成占位符的。
    private const int ExtBizInfo = 2;
    private const int ExtBizPic = 1;
    private const int PicPbReserveC2c = 11;
    private const int PicReserveRkey = 30;

    /// <summary>把 rkey 拼到下载地址上；已经带 rkey 或缺任一方时原样返回。</summary>
    public static string AppendRkey(string url, string rkey)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(rkey) || url.Contains("rkey="))
            return url;

        var token = rkey;
        foreach (var prefix in new[] { "&rkey=", "?rkey=" })
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                token = token[prefix.Length..];
                break;
            }
        }
        if (token.Length == 0) return url;

        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}rkey={Uri.EscapeDataString(token)}";
    }

    /// <summary>解析一段 RichText 的 elems。</summary>
    public static List<Segment> ParseElems(ReadOnlySpan<byte> richTextBody, bool isGroup = false)
    {
        var fields = Wire.DecodeFields(richTextBody);
        var raw = Wire.Repeated(fields, RichTextElems).Select(Wire.AsBytes).ToList();
        return ConvertElems(raw, isGroup);
    }

    /// <summary>MessageBody → 段列表（richText 字段 1 + msgContent 字段 2）。</summary>
    public static List<Segment> ParseMessageBody(ReadOnlySpan<byte> body, bool isGroup)
    {
        var fields = Wire.DecodeFields(body);
        var segments = new List<Segment>();

        var richText = Wire.FieldBytes(fields, 1);
        if (richText.Length > 0)
            segments.AddRange(ParseElems(richText, isGroup));

        var msgContent = Wire.FieldBytes(fields, 2);
        if (msgContent.Length > 0 && ParseC2cFile(msgContent) is { } file)
            segments.Add(file);

        return segments;
    }

    // 引用嵌套的深度上限，跟 MessageContentPresentation.Build 的引用层数上限保持一致——
    // 那边挡的是"解析完了但渲染层数太深"，这里挡的是更根本的问题：ConvertElems 和
    // ReplySegment 相互递归且原本没有深度限制，一条精心构造/损坏的消息（SrcMsg(45)
    // 里嵌 SrcMsg(45)，嵌到底）会一路递归到栈溢出——StackOverflowException 在 .NET
    // 里不可捕获，会直接杀死整个宿主进程，不是"这条消息处理失败"那么简单。
    private const int MaxReplyDepth = 8;

    internal static List<Segment> ConvertElems(IReadOnlyList<byte[]> rawElems, bool isGroup) =>
        ConvertElems(rawElems, isGroup, depth: 0);

    private static List<Segment> ConvertElems(IReadOnlyList<byte[]> rawElems, bool isGroup, int depth)
    {
        var segments = new List<Segment>();
        var sawReply = false;
        var dropNextBlankText = false;

        foreach (var raw in rawElems)
        {
            var fields = Wire.DecodeFields(raw);

            if (fields.ContainsKey(ElemSrcMsg))
            {
                sawReply = true;
                if (ReplySegment(Wire.AsBytes(fields[ElemSrcMsg][0]), isGroup, depth) is { } reply)
                    segments.Add(reply);
                continue;
            }

            if (fields.ContainsKey(ElemText))
            {
                var (seg, isReplyMention) =
                    TextSegment(Wire.AsBytes(fields[ElemText][0]), sawReply);

                if (isReplyMention)
                {
                    dropNextBlankText = true;
                    continue;
                }
                if (dropNextBlankText)
                {
                    dropNextBlankText = false;
                    if (seg is null || (seg.Type == "text" && seg.Text.Trim().Length == 0))
                        continue;
                }
                if (seg is not null) segments.Add(seg);
                continue;
            }

            if (ElemToSegment(raw, isGroup) is { } other)
                segments.Add(other);
        }

        return DropLegacyImageSiblings(segments);
    }

    /// <summary>
    /// 把 TextElem 变成 text/at 段。
    ///
    /// 第二个返回值为 true 表示这是回复消息自带的结构性 @（不是用户内容），
    /// 调用方应连同紧随其后的空白文本一起丢掉。
    /// </summary>
    internal static (Segment? Segment, bool IsReplyMention) TextSegment(
        ReadOnlySpan<byte> textBody, bool sawReply)
    {
        var t = Wire.DecodeFields(textBody);
        var text = Wire.FieldString(t, TextStr);
        var attr6 = Wire.FieldBytes(t, TextAttr6);
        var reserve = Wire.FieldBytes(t, TextPbReserve);
        MentionInfo? mention = reserve.Length > 0 ? Elements.ParseMentionExtra(reserve) : null;

        if (sawReply && mention is { Type: Elements.MentionTypeReply, Uin: 0 })
            return (null, true);

        var hasAttr6 = attr6.Length > 11;
        var hasMention = mention is not null
                         && mention.Type is Elements.MentionTypeUser or Elements.MentionTypeReply;

        if (hasAttr6 || hasMention)
        {
            // attr6Buf 的 [7..11] 是大端 uin
            var uin = hasAttr6
                ? ((ulong)attr6[7] << 24) | ((ulong)attr6[8] << 16)
                  | ((ulong)attr6[9] << 8) | attr6[10]
                : 0UL;
            if (uin == 0 && mention is not null) uin = mention.Uin;

            var target = uin != 0
                ? uin.ToString()
                : mention?.Uid ?? "";
            if (target.Length == 0 && AtAllTexts.Contains(text.Trim()))
                target = "all";

            return (new Segment { Type = "at", Text = text, TargetId = target }, false);
        }

        return (text.Length > 0 ? new Segment { Type = "text", Text = text } : null, false);
    }

    /// <summary>SrcMsg(45) → reply 段；被引用消息的元素一并解析进 Extra["segments"]。</summary>
    internal static Segment? ReplySegment(ReadOnlySpan<byte> srcBody, bool isGroup) =>
        ReplySegment(srcBody, isGroup, depth: 0);

    private static Segment? ReplySegment(ReadOnlySpan<byte> srcBody, bool isGroup, int depth)
    {
        var src = Elements.ParseSrcMsg(srcBody);
        if (src.OrigSeq == 0 && src.Elements.Count == 0 && src.SenderUin == 0)
            return null;

        // 超过深度上限就不再往下解析被引用内容本身，只保留这一层的元信息——
        // "深层引用链丢了内容"是可以接受的降级，比无限递归到栈溢出体面得多。
        var quoted = src.Elements.Count > 0 && depth < MaxReplyDepth
            ? ConvertElems(src.Elements, isGroup, depth + 1)
            : [];
        var senderId = src.SenderUin != 0 ? src.SenderUin.ToString() : "";

        return new Segment
        {
            Type = "reply",
            TargetId = senderId,
            Extra =
            {
                ["seq"] = src.OrigSeq,
                ["time"] = src.Time,
                ["sender_id"] = senderId,
                ["troop_name"] = src.TroopName,
                // 群名片是推送自带的，不用额外查询就能显示；没有群名片（比如私聊引用）
                // 时留空，交给渲染层按 sender_id 走异步资料解析再补上。
                ["sender_name"] = src.TroopName,
                ["segments"] = quoted
            }
        };
    }

    /// <summary>非 text/srcMsg 元素 → Segment。</summary>
    internal static Segment? ElemToSegment(ReadOnlySpan<byte> elemBody, bool isGroup)
    {
        var f = Wire.DecodeFields(elemBody);

        if (f.ContainsKey(ElemNotOnlineImage))
            return ImageSegment(Elements.ParseNotOnlineImage(Wire.AsBytes(f[ElemNotOnlineImage][0])));

        if (f.ContainsKey(ElemTrans))
            return ParseGroupFileTransElem(Wire.AsBytes(f[ElemTrans][0]));

        if (f.ContainsKey(ElemCustomFace))
            return ImageSegment(Elements.ParseCustomFace(Wire.AsBytes(f[ElemCustomFace][0])));

        if (f.ContainsKey(ElemGroupFile))
        {
            var fe = Elements.ParseGroupFileElem(Wire.AsBytes(f[ElemGroupFile][0]));
            return new Segment
            {
                Type = "file",
                Name = fe.Name,
                Url = fe.Url,
                Md5 = fe.Md5,
                Extra =
                {
                    ["bus_id"] = 102UL,
                    ["size"] = fe.Size,
                    ["file_id"] = fe.FileId,
                    ["file_key"] = fe.FileKey
                }
            };
        }

        if (f.ContainsKey(ElemVideoFile))
        {
            var video = Wire.DecodeFields(Wire.AsBytes(f[ElemVideoFile][0]));
            return new Segment
            {
                Type = "video",
                Name = Wire.FieldString(video, 3),
                Md5 = Elements.Hex(Wire.FieldBytes(video, 2)),
                Extra =
                {
                    ["size"] = Wire.FieldInt(video, 6),
                    ["file_id"] = Wire.FieldString(video, 1),
                    ["duration"] = Wire.FieldInt(video, 5)
                }
            };
        }

        if (f.ContainsKey(ElemCommon))
            return ParseNtCommonElem(Wire.AsBytes(f[ElemCommon][0]), isGroup);

        return null;
    }

    private static Segment ImageSegment(ImageElem img)
    {
        var url = Elements.MakeImageUrl(img.OrigUrl);
        if (url.Length == 0) url = Elements.MakeImageUrl(img.BigUrl);
        if (url.Length == 0) url = Elements.MakeImageUrl(img.ThumbUrl);
        if (url.Length == 0) url = Elements.ImageUrlFromMd5(img.Md5);

        var seg = new Segment { Type = "image", Url = url, Md5 = img.Md5, Name = img.Name };
        if (img.FileSize != 0) seg.Extra["size"] = img.FileSize;
        return seg;
    }

    /// <summary>MsgInfo.extBizInfo.pic.bytesPbReserveC2c.field30 → `&amp;rkey=...`。</summary>
    private static string PicRkey(Dictionary<int, List<WireValue>> msgInfo)
    {
        var extBiz = Wire.FieldBytes(msgInfo, ExtBizInfo);
        if (extBiz.Length == 0) return "";

        var pic = Wire.FieldBytes(Wire.DecodeFields(extBiz), ExtBizPic);
        if (pic.Length == 0) return "";

        var reserve = Wire.FieldBytes(Wire.DecodeFields(pic), PicPbReserveC2c);
        if (reserve.Length == 0) return "";

        return Wire.FieldString(Wire.DecodeFields(reserve), PicReserveRkey);
    }

    /// <summary>
    /// 解析 NTQQ CommonElem(service=48) 的图片槽位。
    ///
    /// 只认 businessType 10/20（私聊/群聊图片）——下面这串 index/fileInfo/picture
    /// 字段结构是图片 MsgInfo 专用的，11/21（短视频）、12/22（语音）用的是完全不同的
    /// payload 结构，这里还没有实现，会被上层当成未知元素、消息正文渲染成空而不是
    /// "[视频]"/"[语音]"。不要在这已知缺口之外，让业务类型判断本身自相矛盾——曾经
    /// 这里的第一层校验错误地把 11/21/12/22 也当作"通过"，可它们从来走不到后面
    /// 任何一步图片解析逻辑，纯粹是误导读者以为已经支持的死代码。
    /// </summary>
    internal static Segment? ParseNtCommonElem(ReadOnlySpan<byte> body, bool isGroup)
    {
        var common = Wire.DecodeFields(body);
        var serviceType = Wire.FieldInt(common, 1);
        var businessType = Wire.FieldInt(common, 3);
        var payload = Wire.FieldBytes(common, 2);

        // 第一层判断跟 SnowLuma 源码保持一致的 OR 结构（svc==48 单独也能通过，
        // 不要求 businessType 一定是图片）；第二层才真正收紧到目前唯一实现了的
        // 图片分支。之前第一层里混进了 11/21/12/22，但它们无论如何都会被第二层
        // 拦下，写在那里纯粹是误导。
        if (payload.Length == 0 || !(serviceType == 48 || businessType is 10 or 20))
            return null;
        if (businessType is not (10 or 20))
            return null;

        var msgInfo = Wire.DecodeFields(payload);
        if (Wire.First(msgInfo, 1) is not { } bodyValue) return null;

        var infoBody = Wire.DecodeFields(Wire.AsBytes(bodyValue));
        var indexData = Wire.FieldBytes(infoBody, 1);
        if (indexData.Length == 0) return null;

        var index = Wire.DecodeFields(indexData);
        var fileInfoData = Wire.FieldBytes(index, 1);
        if (fileInfoData.Length == 0) return null;

        var fileInfo = Wire.DecodeFields(fileInfoData);
        var fileUuid = Wire.FieldString(index, 2);
        var fileName = Wire.FieldString(fileInfo, 4);

        var url = "";
        var pictureData = Wire.FieldBytes(infoBody, 2);
        if (pictureData.Length > 0)
        {
            var picture = Wire.DecodeFields(pictureData);
            var path = Wire.FieldString(picture, 1);
            var domain = Wire.FieldString(picture, 3);
            if (domain.Length == 0) domain = "multimedia.nt.qq.com.cn";

            // 这几段字符串来自消息本身（发消息的一方能影响其取值），最终会被
            // MessageAttachmentDownloader 拿去发真实请求——domain 之前只挡了包含
            // '/' 的情况，@、?、#、\ 都能放过去；path/extension 完全没挡控制字符。
            // 用 Uri.TryCreate 校验完整 URL 分支，domain 收紧成纯主机名字符集，
            // path/extension 都拒绝控制字符。
            if (IsHttpUrl(path))
                url = path;
            else if (path.StartsWith('/') && IsValidHostname(domain) && !HasControlChar(path))
                url = $"https://{domain}{path}";

            var extensionData = Wire.FieldBytes(picture, 2);
            if (url.Length > 0 && extensionData.Length > 0)
            {
                var extension = Wire.FieldString(Wire.DecodeFields(extensionData), 1);
                if (!HasControlChar(extension))
                    url += extension;
            }
        }

        if (url.Length == 0 && fileUuid.Length > 0)
        {
            var appId = isGroup ? 1407 : 1406;
            url = $"https://multimedia.nt.qq.com.cn/download?appid={appId}"
                  + $"&fileid={Uri.EscapeDataString(fileUuid)}";
        }

        url = AppendRkey(url, PicRkey(msgInfo));

        return new Segment
        {
            Type = "image",
            Url = url,
            Name = fileName.Length > 0 ? fileName : fileUuid,
            Md5 = Wire.FieldString(fileInfo, 2),
            Extra =
            {
                ["size"] = Wire.FieldInt(fileInfo, 1),
                ["width"] = Wire.FieldInt(fileInfo, 6),
                ["height"] = Wire.FieldInt(fileInfo, 7),
                ["file_id"] = fileUuid,
                ["sha1"] = Wire.FieldString(fileInfo, 3),
                ["nt"] = true
            }
        };
    }

    /// <summary>解析 NTQQ 群文件常见的 TransElem(type=24) 结构。</summary>
    internal static Segment? ParseGroupFileTransElem(ReadOnlySpan<byte> body)
    {
        var trans = Wire.DecodeFields(body);
        if (Wire.FieldInt(trans, 1) != 24) return null;

        var value = Wire.FieldBytes(trans, 2);
        if (value.Length < 3) return null;

        // 头一个字节是标记，紧跟两字节大端长度
        var payloadSize = (value[1] << 8) | value[2];
        if (payloadSize <= 0 || value.Length < 3 + payloadSize) return null;

        var extra = Wire.DecodeFields(value.AsSpan(3, payloadSize));
        var innerData = Wire.FieldBytes(extra, 7);
        var inner = innerData.Length > 0 ? Wire.DecodeFields(innerData) : [];
        var infoData = Wire.FieldBytes(inner, 2);
        var info = infoData.Length > 0 ? Wire.DecodeFields(infoData) : [];

        var fileId = Wire.FieldString(info, 2);
        if (fileId.Length == 0) return null;

        var fileName = Wire.FieldString(info, 4);
        if (fileName.Length == 0) fileName = Wire.FieldString(extra, 2);

        var busId = Wire.FieldInt(info, 1);
        return new Segment
        {
            Type = "file",
            Name = fileName,
            Md5 = Elements.Hex(Wire.FieldBytes(info, 8)),
            Extra =
            {
                ["bus_id"] = busId != 0 ? busId : 102UL,
                ["size"] = Wire.FieldInt(info, 3),
                ["file_id"] = fileId,
                ["sha1"] = Elements.Hex(Wire.FieldBytes(info, 6))
            }
        };
    }

    private static Segment? ParseC2cFile(ReadOnlySpan<byte> msgContent)
    {
        var fileExtra = Wire.DecodeFields(msgContent);
        var fileData = Wire.FieldBytes(fileExtra, 1);
        if (fileData.Length == 0) return null;

        var fileInfo = Wire.DecodeFields(fileData);
        var fileId = Wire.FieldString(fileInfo, 3);
        if (fileId.Length == 0) return null;

        return new Segment
        {
            Type = "file",
            Name = Wire.FieldString(fileInfo, 5),
            Extra =
            {
                ["size"] = Wire.FieldInt(fileInfo, 6),
                ["file_id"] = fileId,
                ["file_hash"] = Wire.FieldString(fileInfo, 57)
            }
        };
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool IsValidHostname(string domain) =>
        domain.Length > 0 && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    private static bool HasControlChar(string value) => value.Any(char.IsControl);

    private static bool IsNtImage(Segment seg) =>
        seg.Type == "image"
        && (seg.Get<bool>("nt") || seg.Url.Contains("multimedia.nt.qq.com.cn"));

    /// <summary>
    /// NT 图片会附带一份给老客户端看的 CustomFace/NotOnlineImage 兄弟元素。
    ///
    /// QQ 只显示一张，这里同样只留 NT 那张：同 MD5 的旧元素、以及压根没有可用
    /// 地址的旧元素都丢掉。
    /// </summary>
    internal static List<Segment> DropLegacyImageSiblings(List<Segment> segments)
    {
        var ntMd5 = segments
            .Where(s => IsNtImage(s) && s.Md5.Length > 0)
            .Select(s => s.Md5.ToLowerInvariant())
            .ToHashSet();

        var kept = new List<Segment>();
        foreach (var seg in segments)
        {
            if (seg.Type != "image" || IsNtImage(seg))
            {
                kept.Add(seg);
                continue;
            }
            if (seg.Url.Length == 0) continue;
            if (seg.Md5.Length > 0 && ntMd5.Contains(seg.Md5.ToLowerInvariant())) continue;
            kept.Add(seg);
        }
        return kept;
    }
}
