using QQListener.Protocol;

namespace QQListener.Models;

public sealed record MessageEvent(
    string Source,
    string SourceDisplayName,
    string Sender,
    string Conversation,
    string Body,
    DateTimeOffset ReceivedAt,
    MessageImportance Importance,
    IReadOnlyList<string> RawText,
    string DeduplicationKey)
{
    // Toast 输入仍可只提供 Body；富消息内容独立保留，供界面逐段显示。
    public IReadOnlyList<Segment> Segments { get; init; } = [];
    public string SenderId { get; init; } = "";
    public string ConversationId { get; init; } = "";

    // 以下四项只有原生捕获来源（NativeMessageEventFactory）会填；toast 来源留空/为 0，
    // 表示"这条消息没法回复"——SourcePid == 0 就是判断能不能回复的唯一依据。
    /// <summary>"group" 或 "c2c"，构造回复请求时决定走群聊路由还是私聊路由。</summary>
    public string Scene { get; init; } = "";
    public int SourcePid { get; init; }
    public string PeerUid { get; init; } = "";
    public long RawSeq { get; init; }

    /// <summary>这条消息里有没有 @ 到当前登录账号自己——只有原生捕获来源会填这个；
    /// 旧的 toast 来源没有结构化的消息片段，没法判断，留 false。</summary>
    public bool MentionsSelf { get; init; }

    /// <summary>
    /// 能不能对这条消息发起回复——只有原生捕获、且对端能用一个数字账号路由才行。
    ///
    /// 私聊消息偶尔只有 uid（QQ 没能解出对方的数字账号）没有 uin：SsoDecode 那边
    /// 退化用 uid 字符串顶上 ConversationId，但 MessageSender 硬性要求
    /// peerUin &gt; 0，只靠 uid 是发不出去的。这里收紧成必须能解析成正整数，
    /// 别让 UI 显示一个点了必然失败的"回复"入口。
    /// </summary>
    public bool CanReply => SourcePid != 0 && Scene.Length > 0
        && long.TryParse(ConversationId, out var peerUin) && peerUin > 0;

    /// <summary>暂停恢复后的摘要保留每条原消息，供富消息展示和明确回复目标。</summary>
    public IReadOnlyList<MessageEvent> PendingMessages { get; init; } = [];
    public int PendingDropped { get; init; }
    public bool IsPendingDigest => PendingMessages.Count > 0;
    public string SpeechText => IsPendingDigest
        ? $"暂停期间收到{PendingMessages.Count}条消息。"
          + string.Join("。", PendingMessages.Take(3).Select(x => x.DisplayBody)) + "。"
          + (PendingMessages.Count > 3 ? $"另有{PendingMessages.Count - 3}条未念。" : "")
          + (PendingDropped > 0 ? $"更早的{PendingDropped}条已超出上限。" : "")
        : DisplayBody;

    public bool IsIgnored => Importance == MessageImportance.Ignored;

    public bool IsImportant => Importance is MessageImportance.Important or MessageImportance.Calling;

    public bool IsCalling => Importance == MessageImportance.Calling;

    public string FullText => string.Join(Environment.NewLine, RawText);

    public string DisplayTitle => IsCalling
        ? $"{SourceDisplayName} 呼叫"
        : IsImportant
            ? $"重要 {SourceDisplayName} 消息"
            : $"{SourceDisplayName} 消息";

    public string DisplaySender
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Conversation)) return Sender;
            if (string.IsNullOrWhiteSpace(Sender)) return Conversation;
            // 解析器在某些两行 Toast 上会把同一段文本同时当作 Conversation 和 Sender，
            // 这里直接去重，避免显示成 "X / X"。
            return string.Equals(Conversation, Sender, StringComparison.Ordinal)
                ? Sender
                : $"{Conversation} / {Sender}";
        }
    }

    public string DisplayBody
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Body)) return DisplaySender;
            return string.IsNullOrWhiteSpace(DisplaySender)
                ? Body
                : $"{DisplaySender}{Environment.NewLine}{Body}";
        }
    }
}
