using System.Globalization;
using QQListener.Models;
using QQListener.Services;
using Xunit;

namespace QQListener.Tests.Services;

public sealed class MessageRuleEvaluatorTests
{
    [Fact]
    public void FixedPriorityIsBlacklistThenSourceThenTimeThenImportance()
    {
        var settings = new QqListenerSettings
        {
            BlacklistText = "secret-blacklist",
            AllowedSourcesText = "allowed.source",
            RuleTimeWindowEnabled = true,
            RuleTimeWindowStartText = "08:00",
            RuleTimeWindowEndText = "18:00",
            CallingEnabled = true,
            CallingKeyword = "call"
        };
        var evaluator = new MessageRuleEvaluator(settings);

        Assert.Equal("命中黑名单规则", evaluator.Evaluate(Event("other", "secret-blacklist call", "12:00")).Reason);
        Assert.Equal("来源不在允许列表", evaluator.Evaluate(Event("other", "call", "12:00")).Reason);
        Assert.Equal("不在允许时间段", evaluator.Evaluate(Event("allowed.source", "call", "20:00")).Reason);
        Assert.Equal(MessageImportance.Calling, evaluator.Evaluate(Event("allowed.source", "call", "12:00")).Event.Importance);
    }

    [Fact]
    public void EmptyAllowedSourcesAllowsEverySource()
    {
        var result = new MessageRuleEvaluator(new QqListenerSettings()).Evaluate(Event("unknown.source", "normal", "12:00"));

        Assert.False(result.IsIgnored);
        Assert.Equal(MessageImportance.Normal, result.Event.Importance);
    }

    [Theory]
    [InlineData("QQ.Toast", "qq.toast", false)]
    [InlineData("qq.toast.extra", "qq.toast", true)]
    public void AllowedSourcesUsesExactCaseInsensitiveMatching(string source, string allowedSources, bool ignored)
    {
        var settings = new QqListenerSettings { AllowedSourcesText = allowedSources };
        var result = new MessageRuleEvaluator(settings).Evaluate(Event(source, "normal", "12:00"));

        Assert.Equal(ignored, result.IsIgnored);
        Assert.Equal(ignored ? "来源不在允许列表" : "未命中特殊规则", result.Reason);
    }

    [Theory]
    [InlineData("08:00", true)]
    [InlineData("17:59", true)]
    [InlineData("18:00", false)]
    public void DayWindowUsesInclusiveStartExclusiveEnd(string clock, bool accepted)
    {
        Assert.Equal(accepted, !EvaluateAt("08:00", "18:00", clock).IsIgnored);
    }

    [Theory]
    [InlineData("22:00", true)]
    [InlineData("06:59", true)]
    [InlineData("07:00", false)]
    [InlineData("12:00", false)]
    public void OvernightWindowWrapsMidnight(string clock, bool accepted)
    {
        Assert.Equal(accepted, !EvaluateAt("22:00", "07:00", clock).IsIgnored);
    }

    [Theory]
    [InlineData("00:00")]
    [InlineData("12:00")]
    [InlineData("23:59")]
    public void EqualTimeBoundsAllowTheWholeDay(string clock)
    {
        Assert.False(EvaluateAt("08:00", "08:00", clock).IsIgnored);
    }

    [Theory]
    [InlineData("", "18:00")]
    [InlineData("2", "18:00")]
    [InlineData("25:99", "18:00")]
    [InlineData("08:00", "")]
    [InlineData("08:00", "2")]
    [InlineData("08:00", "25:99")]
    public void InvalidTimeBoundsAreTreatedAsAllDayWithoutThrowing(string start, string end)
    {
        var settings = new QqListenerSettings
        {
            RuleTimeWindowEnabled = true,
            RuleTimeWindowStartText = start,
            RuleTimeWindowEndText = end
        };

        var result = new MessageRuleEvaluator(settings).Evaluate(Event("qq.toast", "normal", "12:00"));

        Assert.False(result.IsIgnored);
        Assert.Equal(MessageImportance.Normal, result.Event.Importance);
    }

    [Fact]
    public void OnClassPreservesClassificationForDeferredDelivery()
    {
        var settings = new QqListenerSettings { DisableDuringClass = true, CallingKeyword = "call" };
        var result = new MessageRuleEvaluator(settings)
            .Evaluate(Event("qq.native", "call", "12:00"));
        Assert.False(result.IsIgnored);
        Assert.Equal(MessageImportance.Calling, result.Event.Importance);
    }

    [Theory]
    // 群号、QQ 号：整串相同才算命中，前缀相同不算。
    [InlineData("100200300", true)]
    [InlineData("10020030", false)]
    [InlineData("42", true)]
    // 群名、昵称：包含即可，用户不必抄全名。
    [InlineData("班群", true)]
    [InlineData("Ali", true)]
    [InlineData("隔壁班", false)]
    public void ConversationBlockListMatchesIdsExactlyAndNamesLoosely(string rule, bool ignored)
    {
        var settings = new QqListenerSettings { ConversationBlockListText = rule };

        var result = new MessageRuleEvaluator(settings).Evaluate(Conversation());

        Assert.Equal(ignored, result.IsIgnored);
        if (ignored) Assert.Equal("命中会话黑名单", result.Reason);
    }

    [Fact]
    public void EmptyConversationAllowListAllowsEveryConversation()
    {
        var result = new MessageRuleEvaluator(new QqListenerSettings()).Evaluate(Conversation());

        Assert.False(result.IsIgnored);
    }

    [Theory]
    [InlineData("100200300", false)]
    [InlineData("999", true)]
    public void ConversationAllowListDropsEverythingOutsideTheList(string rule, bool ignored)
    {
        var settings = new QqListenerSettings { ConversationAllowListText = rule };

        var result = new MessageRuleEvaluator(settings).Evaluate(Conversation());

        Assert.Equal(ignored, result.IsIgnored);
        if (ignored) Assert.Equal("会话不在白名单", result.Reason);
    }

    [Fact]
    public void ConversationBlockListWinsOverTheAllowList()
    {
        var settings = new QqListenerSettings
        {
            ConversationAllowListText = "100200300",
            ConversationBlockListText = "100200300"
        };

        Assert.Equal("命中会话黑名单", new MessageRuleEvaluator(settings).Evaluate(Conversation()).Reason);
    }

    /// <summary>
    /// 发送者显示名走的是备注 › 群名片 › 昵称，只有全都缺失时才退化成裸 QQ 号——
    /// 所以按 QQ 号填的重要人物必须比对 SenderId，光比对 Sender 等于永远不命中。
    /// </summary>
    [Fact]
    public void ImportantPersonsMatchTheSenderUinEvenWhenADisplayNameIsPresent()
    {
        var settings = new QqListenerSettings { ImportantPersonsText = "42", ImportantKeywordsText = "" };

        var result = new MessageRuleEvaluator(settings).Evaluate(Conversation());

        Assert.Equal(MessageImportance.Important, result.Event.Importance);
        Assert.Equal("命中重要人物规则", result.Reason);
    }

    /// <summary>群号 100200300 不该被规则 "1002003" 顺带匹配上。</summary>
    [Fact]
    public void ImportantPersonsDoNotMatchAUinPrefix()
    {
        var settings = new QqListenerSettings { ImportantPersonsText = "4", ImportantKeywordsText = "" };

        Assert.Equal(
            MessageImportance.Normal,
            new MessageRuleEvaluator(settings).Evaluate(Conversation()).Event.Importance);
    }

    /// <summary>带上会话／发送者 ID 的事件，用来验证按群号与 QQ 号过滤。</summary>
    private static MessageEvent Conversation() => Event("qq.native", "normal", "12:00") with
    {
        SenderId = "42",
        ConversationId = "100200300"
    };

    private static MessageEvent Event(string source, string body, string clock)
    {
        var time = TimeOnly.ParseExact(clock, "HH:mm", CultureInfo.InvariantCulture);
        return new MessageEvent(
            source, "QQ", "Alice", "班群", body,
            new DateTimeOffset(2026, 7, 26, time.Hour, time.Minute, 0, TimeSpan.FromHours(8)),
            MessageImportance.Normal, ["Alice", body], $"{source}:{clock}:{body}");
    }

    private static MessageRuleEvaluationResult EvaluateAt(string start, string end, string clock)
    {
        var settings = new QqListenerSettings
        {
            RuleTimeWindowEnabled = true,
            RuleTimeWindowStartText = start,
            RuleTimeWindowEndText = end
        };
        settings.Normalize();
        return new MessageRuleEvaluator(settings).Evaluate(Event("qq.toast", "normal", clock));
    }
}
