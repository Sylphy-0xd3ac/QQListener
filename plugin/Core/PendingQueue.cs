namespace QQListener.Core;

/// <summary>
/// 暂停期间的消息积压队列。
///
/// <see cref="CoreState.Paused"/> 时钩子还在 QQ 里、管道也还通，捕获照常进行——
/// 只是解析好的通知不弹出来，先压在这里；恢复监听时一次性倒出来，由上层合成
/// 摘要窗口。入队的应当是**已经解析完的**内容（含图片预取、文件地址、rkey 签名）：
/// 这些凭证都是短效的，拖到恢复时再解就已经过期了。
///
/// 队列有上限，满了丢最旧的——积压不该把内存吃穿。对照 Python 版
/// <c>src/core/pending_queue.py</c>。
/// </summary>
public sealed class PendingQueue<T>(int defaultMaxItems = 50)
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();
    private int _dropped;

    public void Push(T payload, int? maxItems = null)
    {
        if (payload is null)
        {
            // is null 对值类型恒为 false，这里主要保护引用类型误传 null 的情况；
            // 之前直接静默忽略，调用方完全没法从 Drop 计数里看出发生过这件事——
            // 至少算作一次丢弃，跟"超过上限被挤掉"用同一个计数器体现出来。
            lock (_gate) _dropped++;
            return;
        }
        var limit = Math.Max(1, maxItems ?? defaultMaxItems);
        lock (_gate)
        {
            _items.Enqueue(payload);
            while (_items.Count > limit)
            {
                _items.Dequeue();
                _dropped++;
            }
        }
    }

    /// <summary>取出全部积压，返回 (消息, 因超出上限被丢弃的条数)。</summary>
    public (IReadOnlyList<T> Items, int Dropped) Drain()
    {
        lock (_gate)
        {
            var items = _items.ToArray();
            var dropped = _dropped;
            _items.Clear();
            _dropped = 0;
            return (items, dropped);
        }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _dropped = 0;
        }
    }
}
