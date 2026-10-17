using QQListener.Models;
using System.Globalization;

namespace QQListener.Services;

public class MessageRuleEvaluator(
    QqListenerSettings settings)
{
    public MessageRuleEvaluationResult Evaluate(MessageEvent messageEvent)
    {
        var fullText = BuildSearchText(messageEvent);

        // Class-time suppression belongs to the delivery queue, so matching messages survive.
        var blacklist = settings.Blacklist.FirstOrDefault(x => Contains(fullText, x));
        if (!string.IsNullOrWhiteSpace(blacklist))
        {
            return Ignore(messageEvent, "命中黑名单规则");
        }

        if (settings.ConversationBlockList.Any(x => MatchesConversation(messageEvent, x)))
        {
            return Ignore(messageEvent, "命中会话黑名单");
        }

        var conversationAllowList = settings.ConversationAllowList;
        if (conversationAllowList.Count > 0
            && !conversationAllowList.Any(x => MatchesConversation(messageEvent, x)))
        {
            return Ignore(messageEvent, "会话不在白名单");
        }

        var allowedSources = settings.AllowedSources;
        if (allowedSources.Count > 0
            && !allowedSources.Contains(messageEvent.Source, StringComparer.OrdinalIgnoreCase))
        {
            return Ignore(messageEvent, "来源不在允许列表");
        }

        if (settings.RuleTimeWindowEnabled && !IsInsideAllowedWindow(messageEvent.ReceivedAt.TimeOfDay))
        {
            return Ignore(messageEvent, "不在允许时间段");
        }

        if (settings.CallingEnabled
            && !string.IsNullOrWhiteSpace(settings.CallingKeyword)
            && Contains(fullText, settings.CallingKeyword))
        {
            return new MessageRuleEvaluationResult(
                messageEvent with { Importance = MessageImportance.Calling },
                false,
                "命中呼叫关键词");
        }

        // 发送者显示名走的是"备注 > 群名片 > 昵称"，只有全都缺失时才退化成裸 QQ 号，
        // 所以光比对 Sender 会让用户按 QQ 号填的重要人物几乎永远不命中。
        var importantPerson = settings.ImportantPersons.FirstOrDefault(
            x => Contains(messageEvent.Sender, x) || IdEquals(messageEvent.SenderId, x));
        if (!string.IsNullOrWhiteSpace(importantPerson))
        {
            return new MessageRuleEvaluationResult(
                messageEvent with { Importance = MessageImportance.Important },
                false,
                "命中重要人物规则");
        }

        var importantKeyword = settings.ImportantKeywords.FirstOrDefault(x => Contains(fullText, x));
        if (!string.IsNullOrWhiteSpace(importantKeyword))
        {
            return new MessageRuleEvaluationResult(
                messageEvent with { Importance = MessageImportance.Important },
                false,
                "命中重要关键词");
        }

        // messageEvent.MentionsSelf 是结构化判定（原生捕获路径专用，见
        // NativeMessageEventFactory）；"有人@我" 是旧 toast 路径遗留的弱兜底——
        // 那是 Windows 通知中心弹窗的提示语，原生正文里不会出现这段中文。
        if (settings.SomeoneAtMe && (messageEvent.MentionsSelf || Contains(fullText, "有人@我")))
        {
            return new MessageRuleEvaluationResult(
                messageEvent with { Importance = MessageImportance.Important },
                false,
                "检测到有人@我");
        }

        return new MessageRuleEvaluationResult(
            messageEvent with { Importance = MessageImportance.Normal },
            false,
            "未命中特殊规则");
    }

    private static string BuildSearchText(MessageEvent messageEvent)
    {
        // 注意：不要把 SourceDisplayName（如 "QQ"）拼进搜索文本，否则用户把 "QQ" 加进
        // 黑名单/重要关键词时，每条消息都会被强行命中。
        return string.Join(Environment.NewLine, new[]
            {
                messageEvent.Sender,
                messageEvent.Conversation,
                messageEvent.Body,
                messageEvent.FullText
            }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>
    /// 一条会话过滤规则能写群号、QQ 号，也能写群名/昵称的一段文字。数字 ID 要求整串
    /// 相等——群号 "12345" 不该顺带匹配上 "123456789"；名称走包含匹配，用户不必抄全名。
    /// </summary>
    private static bool MatchesConversation(MessageEvent messageEvent, string rule) =>
        IdEquals(messageEvent.ConversationId, rule)
        || IdEquals(messageEvent.SenderId, rule)
        || Contains(messageEvent.Conversation, rule)
        || Contains(messageEvent.Sender, rule);

    private static bool IdEquals(string id, string rule) =>
        !string.IsNullOrWhiteSpace(id)
        && string.Equals(id, rule.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string keyword)
    {
        return !string.IsNullOrWhiteSpace(value)
               && !string.IsNullOrWhiteSpace(keyword)
               && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsInsideAllowedWindow(TimeSpan time)
    {
        if (!TimeOnly.TryParseExact(
                settings.RuleTimeWindowStartText,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start)
            || !TimeOnly.TryParseExact(
                settings.RuleTimeWindowEndText,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            return true;
        }

        var startTime = start.ToTimeSpan();
        var endTime = end.ToTimeSpan();

        if (startTime == endTime) return true;
        return startTime < endTime
            ? time >= startTime && time < endTime
            : time >= startTime || time < endTime;
    }

    private static MessageRuleEvaluationResult Ignore(MessageEvent messageEvent, string reason) => new(
        messageEvent with { Importance = MessageImportance.Ignored },
        true,
        reason);
}
