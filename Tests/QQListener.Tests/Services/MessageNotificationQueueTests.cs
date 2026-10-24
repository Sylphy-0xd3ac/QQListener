using ClassIsland.Core.Models.Notification;
using QQListener.Models;
using QQListener.Services;
using Xunit;

namespace QQListener.Tests.Services;

public sealed class MessageNotificationQueueTests
{
    private static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
    private static readonly Action<NotificationRequest> Show = _ => { };

    private static MessageEvent Message(string key, string conversation = "group",
        MessageImportance importance = MessageImportance.Normal) =>
        new("qq.native", "QQ", "老师", conversation, key, ReceivedAt, importance, [key], key)
        {
            ConversationId = conversation, Scene = "group", SourcePid = 7,
            SenderId = "1001", RawSeq = 42,
            Segments = [new() { Type = "text", Text = key }]
        };

    private static QueuedMessageNotification Item(MessageEvent message, CancellationToken token = default) =>
        new(message, Show, token);

    [Fact]
    public void PreviewCannotCarryAnUnvalidatedLiveMessageIntoItsDigest()
    {
        var queue = new MessageNotificationQueue();
        queue.Enqueue(Item(Message("preview")) with { IsPreview = true });
        queue.Enqueue(Item(Message("live")));
        Assert.Equal("preview", queue.TakeNext()!.Message.Body);
        var live = queue.TakeNext()!;
        Assert.False(live.IsPreview);
        Assert.Equal("live", live.Message.Body);
    }

    [Fact]
    public void EngineCleanupPreservesOnlyExplicitPreviewItems()
    {
        var queue = new MessageNotificationQueue();
        queue.Enqueue(Item(Message("live")));
        queue.Enqueue(Item(Message("preview")) with { IsPreview = true });
        queue.ClearEngineNotifications();
        Assert.Equal(1, queue.MessageCount);
        Assert.True(queue.TakeNext()!.IsPreview);
        Assert.Null(queue.TakeNext());
    }

    [Fact]
    public void HighestPriorityComesFirstAndEqualPrioritiesKeepArrivalOrder()
    {
        var queue = new MessageNotificationQueue();
        queue.Enqueue(Item(Message("first", "a")));
        queue.Enqueue(Item(Message("second", "b")));
        queue.Enqueue(Item(Message("important", "c", MessageImportance.Important)));
        queue.Enqueue(Item(Message("calling", "d", MessageImportance.Calling)));

        Assert.Equal("calling", queue.TakeNext()!.Message.Body);
        Assert.Equal("important", queue.TakeNext()!.Message.Body);
        Assert.Equal("first", queue.TakeNext()!.Message.Body);
        Assert.Equal("second", queue.TakeNext()!.Message.Body);
        Assert.Null(queue.TakeNext());
    }

    [Fact]
    public void SameConversationBurstIsMergedInOrderWithOriginalMessagesAndReplyTarget()
    {
        var queue = new MessageNotificationQueue();
        var first = Message("first", "10001");
        var second = Message("second", "10001") with { RawSeq = 43 };
        queue.Enqueue(Item(first));
        queue.Enqueue(Item(second));

        var digest = queue.TakeNext()!.Message;

        Assert.Equal([first, second], digest.PendingMessages);
        Assert.Equal("qq.queued", digest.Source);
        Assert.Contains("等待期间的 2 条消息", digest.Sender);
        Assert.StartsWith("等待期间收到2条消息。", digest.SpeechText);
        Assert.Equal(second.Segments, digest.PendingMessages[^1].Segments);
        Assert.True(digest.PendingMessages[^1].CanReply);
        Assert.Null(queue.TakeNext());
    }

    [Fact]
    public void BatchSizeIsBoundedAndLaterMessagesRemainQueued()
    {
        var queue = new MessageNotificationQueue(batchSize: 2);
        foreach (var key in new[] { "first", "second", "third" }) queue.Enqueue(Item(Message(key)));

        Assert.Equal(2, queue.TakeNext()!.Message.PendingMessages.Count);
        Assert.Equal(1, queue.MessageCount);
        Assert.Equal("third", queue.TakeNext()!.Message.Body);
    }

    [Fact]
    public void MessagesOutsideTheBurstWindowOrFromDifferentAccountsAreNotMerged()
    {
        var queue = new MessageNotificationQueue();
        queue.Enqueue(Item(Message("first")));
        queue.Enqueue(Item(Message("late") with { ReceivedAt = ReceivedAt.AddSeconds(4) }));
        queue.Enqueue(Item(Message("account") with { SourcePid = 8 }));

        Assert.False(queue.TakeNext()!.Message.IsPendingDigest);
        Assert.False(queue.TakeNext()!.Message.IsPendingDigest);
        Assert.False(queue.TakeNext()!.Message.IsPendingDigest);
    }

    [Fact]
    public void CancellationRemovesWaitingMessagesWithoutDiscardingOtherMessages()
    {
        var queue = new MessageNotificationQueue();
        using var cancellation = new CancellationTokenSource();
        queue.Enqueue(Item(Message("canceled"), cancellation.Token));
        queue.Enqueue(Item(Message("kept")));
        cancellation.Cancel();

        Assert.Equal(1, queue.MessageCount);
        Assert.Equal("kept", queue.TakeNext()!.Message.Body);
        Assert.Null(queue.TakeNext());
    }

    [Fact]
    public void IndependentCallbacksAndCancellationLifetimesAreNeverMerged()
    {
        var queue = new MessageNotificationQueue();
        using var cancellation = new CancellationTokenSource();
        queue.Enqueue(Item(Message("first")));
        queue.Enqueue(Item(Message("other token"), cancellation.Token));
        queue.Enqueue(new QueuedMessageNotification(Message("other callback"), request => request.Cancel(), default));

        Assert.False(queue.TakeNext()!.Message.IsPendingDigest);
        Assert.False(queue.TakeNext()!.Message.IsPendingDigest);
        Assert.False(queue.TakeNext()!.Message.IsPendingDigest);
    }

    [Fact]
    public void OverflowKeepsImportantMessagesAndReportsTheDiscardedCount()
    {
        var queue = new MessageNotificationQueue(capacity: 2);
        queue.Enqueue(Item(Message("important", "a", MessageImportance.Important)));
        queue.Enqueue(Item(Message("old", "b")));
        queue.Enqueue(Item(Message("new", "c")));

        var next = queue.TakeNext()!.Message;
        Assert.Equal("important", Assert.Single(next.PendingMessages).Body);
        Assert.Equal(1, next.PendingDropped);
        Assert.Contains("更早的 1 条已丢弃", next.Sender);
        Assert.Equal("new", queue.TakeNext()!.Message.Body);
    }

    [Fact]
    public void ExistingPauseDigestIsPreservedAndClearResetsTheQueue()
    {
        var queue = new MessageNotificationQueue();
        var digest = PendingMessageDigest.Create([Message("first"), Message("second")])!;
        queue.Enqueue(Item(digest));
        Assert.Equal(2, queue.MessageCount);
        Assert.Same(digest, queue.TakeNext()!.Message);
        queue.Enqueue(Item(Message("discard")));
        queue.Clear();
        Assert.Equal(0, queue.MessageCount);
        Assert.Null(queue.TakeNext());
    }
}
