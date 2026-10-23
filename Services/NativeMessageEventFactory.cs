using QQListener.Models;
using QQListener.Protocol;

namespace QQListener.Services;

/// <summary>
/// 把协议层解出来的 <see cref="CapturedMessage"/> 转成插件的 <see cref="MessageEvent"/>。
///
/// 只填字段，不做渲染——富媒体/引用的展示结构完全交给
/// <see cref="MessageContentPresentation"/> 去处理 <see cref="MessageEvent.Segments"/>。
/// </summary>
public static class NativeMessageEventFactory
{
    private const string SourceId = "qq.native";
    private const string SourceDisplayName = "QQ";

    public static MessageEvent From(CapturedMessage message)
    {
        var senderDisplay = DisplayName(message);
        // 群聊显示"群名 / 发送者"；私聊本来就是一对一，会话名跟发送者相同——
        // MessageEvent.DisplaySender 会把相同的两段自动去重成一行。
        var conversation = message.IsGroup ? message.PeerName : senderDisplay;
        var body = MessageText.Body(message);

        return new MessageEvent(
            SourceId,
            SourceDisplayName,
            senderDisplay,
            conversation,
            body,
            // 转成本地时区：MessageRuleEvaluator 的时间窗判定直接读 ReceivedAt.TimeOfDay
            // （DateTimeOffset 的这个属性取的是它自己偏移量下的挂钟时刻，不会另外做
            // 时区换算）——用户在设置里填的 "08:00" 是本地时间，UTC 时间戳留着不转
            // 会让实际生效的时间窗跟用户配置的相差一个时区（东八区下差 8 小时）。
            // DateTimeOffset 的相等/比较是按 UTC 时刻算的，不受这里选哪个偏移量表示
            // 影响，所以这个改动不影响别处依赖时间先后顺序的逻辑。
            message.Timestamp != 0
                ? DateTimeOffset.FromUnixTimeSeconds(message.Timestamp).ToLocalTime()
                : DateTimeOffset.Now,
            MessageImportance.Normal,
            [body],
            DeduplicationKey(message, body))
        {
            Segments = message.Segments,
            SenderId = message.SenderId,
            ConversationId = message.PeerId,
            Scene = message.Scene,
            SourcePid = message.SourcePid,
            PeerUid = message.PeerUid,
            RawSeq = (long)message.RawSeq,
            MentionsSelf = MentionsSelf(message),
        };
    }

    /// <summary>
    /// "有人 @ 我" 规则之前是拿正文去搜字面量 "有人@我"——那是 Windows 通知中心
    /// 弹窗的提示语，原生捕获路径的正文来自协议解析，根本不会出现这段中文，
    /// 规则在新数据源下恒不命中。改成检查结构化的 @ 片段是否指向当前账号自己。
    /// </summary>
    private static bool MentionsSelf(CapturedMessage message)
    {
        if (message.SelfUin == 0) return false;
        var selfId = message.SelfUin.ToString();
        return message.Segments.Any(s => s.Type == "at" && s.TargetId == selfId);
    }

    /// <summary>备注 &gt; 群昵称 &gt; 昵称 &gt; 推送自带名 &gt; 裸 QQ 号。</summary>
    private static string DisplayName(CapturedMessage message)
    {
        if (message.SenderRemark.Length > 0) return message.SenderRemark;
        if (message.IsGroup && message.SenderGroupCard.Length > 0) return message.SenderGroupCard;
        if (message.SenderNickname.Length > 0) return message.SenderNickname;
        if (message.SenderName.Length > 0) return message.SenderName;
        return message.SenderId;
    }

    // RawSeq 非 0 时它本身已经唯一标识一条消息，不需要也不该再往键里掺别的字段——
    // 掺了反而会让"同一条消息被重复投递、但两次时间戳有任何出入"漏判成不重复。
    // 只有协议没给到真实序列号、RawSeq 老实留 0 时（见 SsoDecode）才用 Timestamp +
    // 正文兜底：光靠 "会话+发送者+0" 会把同一个人连续发的好几条零序列号消息全部
    // 去重成一条；Timestamp 来自消息本体（ContentHead），同一条消息真被重复投递时
    // 两次的值不会变，不会破坏这个场景本身要抓的重复。
    private static string DeduplicationKey(CapturedMessage message, string body) =>
        message.RawSeq != 0
            ? $"{SourceId}:{message.Scene}:{message.PeerId}:{message.SenderId}:{message.RawSeq}"
            : $"{SourceId}:{message.Scene}:{message.PeerId}:{message.SenderId}:0:{message.Timestamp}:{body}";
}
