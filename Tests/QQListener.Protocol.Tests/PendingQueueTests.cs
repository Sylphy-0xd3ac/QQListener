using QQListener.Core;

namespace QQListener.Protocol.Tests;

/// <summary>积压队列的顺序、容量和清理行为；摘要合并由通知层测试验证。</summary>
public class PendingQueueTests
{
    [Fact]
    public void PushAndDrainPreservesOrder()
    {
        var queue = new PendingQueue<string>();
        queue.Push("第一条");
        queue.Push("第二条");
        Assert.Equal(2, queue.Count);

        var (items, dropped) = queue.Drain();

        Assert.Equal(["第一条", "第二条"], items);
        Assert.Equal(0, dropped);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void QueueIsBoundedAndDropsTheOldest()
    {
        var queue = new PendingQueue<string>();
        for (var i = 0; i < 5; i++) queue.Push($"第{i}条", maxItems: 3);

        var (items, dropped) = queue.Drain();

        Assert.Equal(["第2条", "第3条", "第4条"], items);
        Assert.Equal(2, dropped);
    }

    [Fact]
    public void NullPayloadDoesNotEnqueueButIsCountedAsDropped()
    {
        // 之前直接静默忽略，调用方完全没法从任何地方看出发生过这件事。
        var queue = new PendingQueue<string?>();
        queue.Push(null);
        Assert.Equal(0, queue.Count);

        var (items, dropped) = queue.Drain();
        Assert.Empty(items);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void ClearResetsBothItemsAndDropCount()
    {
        var queue = new PendingQueue<string>();
        for (var i = 0; i < 4; i++) queue.Push(i.ToString(), maxItems: 2);
        queue.Clear();

        var (items, dropped) = queue.Drain();
        Assert.Empty(items);
        Assert.Equal(0, dropped);
    }
}
