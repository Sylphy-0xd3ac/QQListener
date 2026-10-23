using QQListener.Pipes;

namespace QQListener.Protocol.Tests;

/// <summary>
/// 测试用：按预置 chunk 顺序返回字节，耗尽后返回 0（模拟管道关闭）。
///
/// 只用于测试，因此放在测试项目里而不是产品程序集——之前它跟 <see cref="NamedPipeTransport"/>
/// 放在一起，会被原样打进插件包。
/// </summary>
public sealed class FakeTransport(IEnumerable<byte[]> chunks) : IPipeTransport
{
    private readonly Queue<byte[]> _chunks = new(chunks);
    private byte[]? _current;
    private int _offset;
    private bool _closed;

    public List<byte[]> Writes { get; } = [];

    public int Read(Span<byte> buffer)
    {
        if (_closed) return 0;
        if (_current is null || _offset >= _current.Length)
        {
            if (_chunks.Count == 0) return 0;
            _current = _chunks.Dequeue();
            _offset = 0;
        }

        // chunk 比调用方缓冲区长时，之前的实现会把超出部分直接丢弃而不是留到下次
        // Read——用 _current/_offset 记住消费到哪，跨多次 Read 正确地吐出剩余部分。
        var n = Math.Min(_current.Length - _offset, buffer.Length);
        _current.AsSpan(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        Writes.Add(data.ToArray());
    }

    public void Dispose() => _closed = true;
}
