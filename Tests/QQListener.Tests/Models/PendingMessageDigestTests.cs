using QQListener.Models;
using QQListener.Protocol;
using QQListener.Services;
using Xunit;

namespace QQListener.Tests.Models;

public class PendingMessageDigestTests
{
    private static MessageEvent Message(int i, MessageImportance importance = MessageImportance.Normal) =>
        new("qq", "QQ", $"发送者{i}", $"会话{i}", $"消息{i}", DateTimeOffset.Now,
            importance, [$"消息{i}"], $"key{i}");

    [Fact]
    public void EmptyAndSingleBacklogsDoNotCreateAnUnnecessaryDigest()
    {
        Assert.Null(PendingMessageDigest.Create([]));
        var message = Message(0);
        Assert.Same(message, PendingMessageDigest.Create([message]));
        Assert.True(PendingMessageDigest.Create([message], 2)!.IsPendingDigest);
    }

    [Fact]
    public void DigestPreservesOrderMediaQuotesAndHighestImportance()
    {
        var quote = new Segment { Type = "reply", Extra = new()
        {
            ["sender_name"] = "被引用者",
            ["segments"] = new List<Segment> { new() { Type = "text", Text = "原文" } }
        }};
        var first = Message(0) with { Segments = [new() { Type = "image", Url = "avares://QQListener/icon.png" }] };
        var second = Message(1, MessageImportance.Calling) with { Segments = [quote, new() { Type = "text", Text = "答复" }] };
        var digest = PendingMessageDigest.Create([first, second], 7)!;
        Assert.Equal([first, second], digest.PendingMessages);
        Assert.Equal(MessageImportance.Calling, digest.Importance);
        Assert.Contains("更早的 7 条已丢弃", digest.Sender);
        var blocks = MessageContentPresentation.Create(digest);
        Assert.Equal(2, blocks.Count);
        Assert.Equal("image", Assert.Single(blocks[0].Children!).Kind);
        Assert.Equal("quote", blocks[1].Children![0].Kind);
        Assert.Equal("原文", Assert.Single(blocks[1].Children![0].Children!).Text);
        Assert.False(digest.CanReply);
    }

    [Fact]
    public void DigestUsesItsHostNotificationImportanceInsteadOfAnIndependentLongestDuration()
    {
        var notifier = new ClassIslandMessageNotifier(new QqListenerSettings
        {
            NormalDurationSeconds = 60, ImportantDurationSeconds = 10
        });
        var digest = PendingMessageDigest.Create([Message(0), Message(1, MessageImportance.Important)])!;
        Assert.Equal(TimeSpan.FromSeconds(10), notifier.GetDuration(digest));
        Assert.Equal(notifier.GetDuration(digest), notifier.BuildRequest(digest).OverlayContent!.Duration);
    }

    [Fact]
    public void SpeechReadsOnlyFirstThreeAndReportsTotalsAndOverflow()
    {
        var digest = PendingMessageDigest.Create(Enumerable.Range(0, 6).Select(i => Message(i)).ToArray(), 4)!;
        var request = new ClassIslandMessageNotifier(new QqListenerSettings()).BuildRequest(digest);
        var speech = request.OverlayContent!.SpeechContent;
        Assert.StartsWith("暂停期间收到6条消息。", speech);
        Assert.Contains("消息2", speech);
        Assert.DoesNotContain("消息3", speech);
        Assert.Contains("另有3条未念", speech);
        Assert.Contains("更早的4条已超出上限", speech);
    }
}
