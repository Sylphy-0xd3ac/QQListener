using ClassIsland.Core.Models.Notification;
using QQListener.Models;

namespace QQListener.Services;

internal sealed record QueuedMessageNotification(
    MessageEvent Message, Action<NotificationRequest> ShowNotification, CancellationToken CancellationToken)
{
    public int MessageCount => Message.IsPendingDigest ? Message.PendingMessages.Count : 1;
}

internal sealed class MessageNotificationQueue(int capacity = 100, int batchSize = 20)
{
    private readonly List<QueuedMessageNotification> _messages = [];
    private int _dropped;

    public int MessageCount => _messages.Where(item => !item.CancellationToken.IsCancellationRequested)
        .Sum(item => item.MessageCount);

    public void Enqueue(QueuedMessageNotification item)
    {
        _messages.RemoveAll(message => message.CancellationToken.IsCancellationRequested);
        if (item.CancellationToken.IsCancellationRequested) return;
        _messages.Add(item);
        while (_messages.Count > Math.Max(1, capacity))
        {
            var oldest = _messages.MinBy(message => message.Message.Importance)!;
            _messages.Remove(oldest);
            _dropped += oldest.MessageCount;
        }
    }

    public QueuedMessageNotification? TakeNext()
    {
        _messages.RemoveAll(item => item.CancellationToken.IsCancellationRequested);
        if (_messages.Count == 0) return null;
        var first = _messages.MaxBy(item => item.Message.Importance)!;
        _messages.Remove(first);
        var batch = new List<MessageEvent> { first.Message };
        if (!first.Message.IsPendingDigest)
        {
            foreach (var item in _messages.ToArray())
            {
                if (batch.Count >= Math.Max(1, batchSize)) break;
                if (item.Message.IsPendingDigest || item.Message.Importance != first.Message.Importance
                    || item.ShowNotification != first.ShowNotification
                    || item.CancellationToken != first.CancellationToken
                    || !SameConversation(first.Message, item.Message)
                    || (item.Message.ReceivedAt - first.Message.ReceivedAt).Duration() > TimeSpan.FromSeconds(3))
                    continue;
                batch.Add(item.Message);
                _messages.Remove(item);
            }
        }

        var dropped = _dropped;
        _dropped = 0;
        if (batch.Count == 1 && dropped == 0) return first;
        var messages = batch.SelectMany(message => message.IsPendingDigest
            ? message.PendingMessages : new[] { message }).ToArray();
        return first with { Message = PendingMessageDigest.CreateQueued(messages,
            dropped + batch.Sum(message => message.PendingDropped))! };
    }

    public void Clear()
    {
        _messages.Clear();
        _dropped = 0;
    }

    private static bool SameConversation(MessageEvent first, MessageEvent second)
    {
        static string Identity(MessageEvent message) => !string.IsNullOrWhiteSpace(message.ConversationId)
            ? message.ConversationId : !string.IsNullOrWhiteSpace(message.Conversation)
                ? message.Conversation : message.Sender;
        var identity = Identity(first);
        return identity.Length > 0 && identity == Identity(second)
            && first.Source == second.Source && first.Scene == second.Scene && first.SourcePid == second.SourcePid;
    }
}
