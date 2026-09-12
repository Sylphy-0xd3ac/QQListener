using System.Text.RegularExpressions;

namespace QQListener.Protocol;

/// <summary>NotOnlineImage / CustomFace 解出来的图片元素。</summary>
public sealed record ImageElem(
    string OrigUrl = "",
    string BigUrl = "",
    string ThumbUrl = "",
    string Md5 = "",
    ulong FileSize = 0,
    string Name = "");

/// <summary>GroupFileElem 解出来的文件元素。</summary>
public sealed record FileElem(
    string Name = "",
    string Url = "",
    string Md5 = "",
    ulong Size = 0,
    string FileId = "",
    string FileKey = "");

/// <summary>TextElem.pbReserve(12) 解出来的 MentionExtra。</summary>
public sealed record MentionInfo(int Type = 0, ulong Uin = 0, string Uid = "");

/// <summary>SrcMsg（Elem 字段 45）：被引用的那条消息。</summary>
public sealed record SrcMsgElem(
    ulong OrigSeq = 0,
    ulong SenderUin = 0,
    int Time = 0,
    string TroopName = "",
    IReadOnlyList<byte[]>? Elems = null)
{
    public IReadOnlyList<byte[]> Elements => Elems ?? [];
}

/// <summary>
/// 单个消息元素的解码。字段号全部取自 SnowLuma 公开源码
/// <c>packages/proto-defs/src/element.ts</c> 的 Elem 接口——权威，勿改。
/// </summary>
public static partial class Elements
{
    // QQ 图片元素里的 origUrl/bigUrl 常是相对路径，需要补主机名才可下载。
    // 取值规则对齐 SnowLuma packages/protocol/src/msg-push/helpers.ts::makeImageUrl。
    public const string NtMediaHost = "https://multimedia.nt.qq.com.cn";
    public const string LegacyImageHost = "http://gchat.qpic.cn";

    // TextElem.pbReserve(12) 解出的 MentionExtra：type=1 为真实 @，
    // type=2 是回复消息自带的结构性 @（uin=0），后者不是用户内容，需要丢掉。
    public const int MentionTypeUser = 1;
    public const int MentionTypeReply = 2;

    [GeneratedRegex("^[0-9a-fA-F]{32}$")]
    private static partial Regex Md5HexRegex();

    /// <summary>把图片元素里的路径补成可下载 URL；补不出来时返回空串。</summary>
    public static string MakeImageUrl(string origUrl)
    {
        if (string.IsNullOrEmpty(origUrl)) return "";
        if (origUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || origUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return origUrl;

        // 只带查询串没有路径的兼容表情，拼上主机名也是打不开的。
        if (!origUrl.StartsWith('/')) return "";

        return origUrl.Contains("rkey") || origUrl.Contains("fileid")
            ? NtMediaHost + origUrl
            : LegacyImageHost + origUrl;
    }

    /// <summary>老式群图片地址：线上只给了 MD5、没给下载路径时的兜底。</summary>
    public static string ImageUrlFromMd5(string md5Hex) =>
        string.IsNullOrEmpty(md5Hex) || !Md5HexRegex().IsMatch(md5Hex)
            ? ""
            : $"{LegacyImageHost}/gchatpic_new/0/0-0-{md5Hex.ToUpperInvariant()}/0";

    public static string ParseTextElem(ReadOnlySpan<byte> body) =>
        Wire.FieldString(Wire.DecodeFields(body), 1);

    /// <summary>TextElem.pbReserve(12) → MentionExtra{type=3, uin=4, uid=9}。</summary>
    public static MentionInfo ParseMentionExtra(ReadOnlySpan<byte> body)
    {
        var f = Wire.DecodeFields(body);
        return new MentionInfo(
            Wire.ClampToInt32(Wire.FieldInt(f, 3)),
            Wire.FieldInt(f, 4),
            Wire.FieldString(f, 9));
    }

    /// <summary>SrcMsg：origSeqs=1, senderUin=2, time=3, elemsRaw=5, troopName=11。</summary>
    public static SrcMsgElem ParseSrcMsg(ReadOnlySpan<byte> body)
    {
        var f = Wire.DecodeFields(body);
        return new SrcMsgElem(
            Wire.FieldInt(f, 1),
            Wire.FieldInt(f, 2),
            Wire.ClampToInt32(Wire.FieldInt(f, 3)),
            Wire.FieldString(f, 11),
            [.. Wire.Repeated(f, 5).Select(Wire.AsBytes)]);
    }

    public static ImageElem ParseNotOnlineImage(ReadOnlySpan<byte> body)
    {
        var f = Wire.DecodeFields(body);
        return new ImageElem(
            OrigUrl: Wire.FieldString(f, 15),
            BigUrl: Wire.FieldString(f, 14),
            ThumbUrl: Wire.FieldString(f, 12),
            Md5: Hex(Wire.FieldBytes(f, 7)),
            FileSize: Wire.FieldInt(f, 2),
            Name: Wire.FieldString(f, 1));
    }

    /// <summary>NT QQ 群图片多走 CustomFace（Elem 字段 8）。</summary>
    public static ImageElem ParseCustomFace(ReadOnlySpan<byte> body)
    {
        var f = Wire.DecodeFields(body);
        return new ImageElem(
            OrigUrl: Wire.FieldString(f, 16),
            BigUrl: Wire.FieldString(f, 15),
            ThumbUrl: Wire.FieldString(f, 14),
            Md5: Hex(Wire.FieldBytes(f, 13)),
            FileSize: Wire.FieldInt(f, 25),
            Name: Wire.FieldString(f, 2));
    }

    /// <summary>
    /// GroupFileElem（Elem 字段 13）：filename=1, fileSize=2, fileId=3, fileKey=5。
    /// 群文件不在推送里带下载 URL——需用 fileId/fileKey 发 OIDB 请求换地址。
    /// </summary>
    public static FileElem ParseGroupFileElem(ReadOnlySpan<byte> body)
    {
        var f = Wire.DecodeFields(body);
        return new FileElem(
            Name: Wire.FieldString(f, 1),
            Size: Wire.FieldInt(f, 2),
            FileId: Wire.FieldString(f, 3),
            FileKey: Wire.FieldString(f, 5));
    }

    /// <summary>小写十六进制，与 Python 的 <c>bytes.hex()</c> 一致。</summary>
    public static string Hex(byte[] data) =>
        data.Length == 0 ? "" : Convert.ToHexString(data).ToLowerInvariant();
}
