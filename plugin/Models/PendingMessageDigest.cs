namespace QQListener.Models;

/// <summary>与原版 build_digest_payload 一致：恢复时只显示一个摘要，单条直接显示原消息。</summary>
public static class PendingMessageDigest
{
    public static MessageEvent? Create(IReadOnlyList<MessageEvent> messages, int dropped = 0)
    {
        if (messages.Count == 0) return null;
        dropped = Math.Max(0, dropped);
        if (messages.Count == 1 && dropped == 0) return messages[0];
        var items = messages.ToArray();
        var title = $"暂停期间的 {items.Length} 条消息"
                    + (dropped > 0 ? $"（更早的 {dropped} 条已丢弃）" : "");
        return new MessageEvent("qq.pending", "QQ", title, "",
            string.Join(Environment.NewLine, items.Select(x => x.DisplayBody)),
            items[^1].ReceivedAt, items.Max(x => x.Importance),
            items.SelectMany(x => x.RawText).ToArray(), $"pending:{Guid.NewGuid():N}")
        {
            PendingMessages = items,
            PendingDropped = dropped
        };
    }
}
