namespace QQListener.Protocol;

/// <summary>
/// 一条消息里的一段。
///
/// 类型用字符串而不是枚举，
/// 是为了让未知元素也能原样带过去（渲染成 <c>[type]</c>），不至于整条消息丢掉。
/// </summary>
public sealed class Segment
{
    public required string Type { get; init; }
    public string Text { get; init; } = "";
    public string Url { get; set; } = "";
    public string Name { get; init; } = "";
    public string Md5 { get; init; } = "";

    /// <summary>@ 的目标：QQ 号，或 "all" 表示全体成员。</summary>
    public string TargetId { get; init; } = "";

    /// <summary>类型相关的附加字段（尺寸、file_id、被引用消息的段等）。</summary>
    public Dictionary<string, object?> Extra { get; init; } = [];

    public T? Get<T>(string key, T? fallback = default) =>
        Extra.TryGetValue(key, out var v) && v is T typed ? typed : fallback;
}

/// <summary>被引用的那条消息（来自 SrcMsg）。</summary>
public sealed class QuotedMessage
{
    public string SenderId { get; init; } = "";
    public string SenderName { get; set; } = "";
    public int Time { get; init; }
    public ulong Seq { get; init; }
    public IReadOnlyList<Segment> Segments { get; init; } = [];
}

/// <summary>一条原生捕获到的消息。</summary>
public sealed class CapturedMessage
{
    public required string Scene { get; init; }          // "group" / "c2c"
    public string PeerId { get; init; } = "";
    public string PeerName { get; init; } = "";
    public string PeerUid { get; init; } = "";
    public string SenderId { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string SenderNickname { get; set; } = "";
    public string SenderRemark { get; set; } = "";
    public string SenderGroupCard { get; init; } = "";
    public string AccountUid { get; set; } = "";
    public ulong RawSeq { get; init; }
    public int Timestamp { get; init; }
    public int SourcePid { get; set; }
    /// <summary>当前登录账号自己的数字 QQ 号；判定"有人 @ 我"要拿它跟 Segment("at") 的
    /// TargetId 比对，0 表示没能解析出来。</summary>
    public ulong SelfUin { get; init; }
    public IReadOnlyList<Segment> Segments { get; init; } = [];

    public bool IsGroup => Scene == "group";
    public bool IsSelf => SelfUin != 0 && SenderId == SelfUin.ToString();
}

public static class MessageText
{
    /// <summary>把若干段渲染成一行文本（播报与去重用）。</summary>
    public static string Render(IEnumerable<Segment> segments)
    {
        var parts = new List<string>();
        foreach (var seg in segments)
        {
            parts.Add(seg.Type switch
            {
                "text" => seg.Text,
                // 线上的 @ 文本自带 "@" 和尾随空格，别渲染成 [@@李四 ]
                "at" => AtLabel(seg.Text),
                "image" => "[图片]",
                "record" => "[语音]",
                "video" => "[视频]",
                "file" => $"[文件] {seg.Name}".TrimEnd(),
                "reply" => "[回复]",
                _ => $"[{seg.Type}]"
            });
        }
        return string.Concat(parts);
    }

    private static string AtLabel(string text)
    {
        var name = text.Trim().TrimStart('@');
        return name.Length > 0 ? $"[@{name}]" : "[@]";
    }

    /// <summary>本条消息正文：引用块单独渲染，不混进正文。</summary>
    public static string Body(CapturedMessage msg) =>
        Render(msg.Segments.Where(s => s.Type != "reply"));

    /// <summary>取出被引用的消息；没有引用返回 null。</summary>
    public static QuotedMessage? Quoted(CapturedMessage msg)
    {
        foreach (var seg in msg.Segments)
        {
            if (seg.Type != "reply") continue;
            return new QuotedMessage
            {
                SenderId = seg.Get<string>("sender_id") ?? "",
                SenderName = seg.Get<string>("sender_name") ?? "",
                Time = seg.Get<int>("time"),
                Seq = seg.Get<ulong>("seq"),
                Segments = seg.Get<List<Segment>>("segments") ?? []
            };
        }
        return null;
    }
}
