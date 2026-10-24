using System.IO.Pipes;

namespace QQListener.Pipes;

/// <summary>
/// 字节流传输，支持以模拟传输验证跨平台协议逻辑。
/// </summary>
public interface IPipeTransport : IDisposable
{
    int Read(Span<byte> buffer);
    void Write(ReadOnlySpan<byte> data);
}

/// <summary>SnowLuma 内核在 QQ 进程里开的两条命名管道。</summary>
public static class PipeNames
{
    public static string Recv(int pid) => $"mojo.{pid}.recv";
    public static string Control(int pid) => $"mojo.{pid}.control";

    /// <summary>给日志/诊断用的完整路径形式。</summary>
    public static string FullRecv(int pid) => $@"\\.\pipe\{Recv(pid)}";
    public static string FullControl(int pid) => $@"\\.\pipe\{Control(pid)}";
}

/// <summary>
/// Windows 命名管道客户端。
///
/// 使用 <c>NamedPipeClientStream</c> 管理流读写与句柄生命周期。
/// </summary>
public sealed class NamedPipeTransport : IPipeTransport
{
    private readonly NamedPipeClientStream _stream;
    private readonly int _readTimeoutMs;
    private readonly CancellationTokenSource? _operationDeadline;

    /// <summary>
    /// <paramref name="readTimeoutMs"/> 默认不超时；为短连接指定时，限制连接后整次
    /// 请求的读写总时长。recv 是长连接，control 则由 QqControlApi 指定有限值。
    /// </summary>
    public NamedPipeTransport(string pipeName, int connectTimeoutMs = 3000, int readTimeoutMs = Timeout.Infinite)
    {
        _readTimeoutMs = readTimeoutMs;
        _stream = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            _stream.Connect(connectTimeoutMs);
            if (readTimeoutMs != Timeout.Infinite) _operationDeadline = new CancellationTokenSource(readTimeoutMs);
        }
        catch
        {
            // Connect 失败时 _stream 已经分配了系统句柄，构造函数不返回意味着
            // 调用方永远拿不到这个实例去 Dispose——自己收拾干净再往外抛。
            _stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// <see cref="PipeStream"/>（<see cref="NamedPipeClientStream"/> 的基类）不支持
    /// <see cref="Stream.ReadTimeout"/>——<see cref="Stream.CanTimeout"/> 恒为
    /// false，直接赋值会抛 <see cref="InvalidOperationException"/>。同步 <c>Read</c>
    /// 一旦对端卡死就没有任何办法叫醒它；唯一能真正设上限的路径是走异步读 +
    /// <see cref="CancellationToken"/>——构造时用了 <see cref="PipeOptions.Asynchronous"/>，
    /// 取消会真正打断底层的 overlapped I/O，不是"发起了就收不回"的假取消。
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        if (_readTimeoutMs == Timeout.Infinite)
            return _stream.Read(buffer);

        var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            int read;
            try
            {
                read = _stream.ReadAsync(pooled.AsMemory(0, buffer.Length), _operationDeadline!.Token)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_operationDeadline!.IsCancellationRequested)
            {
                throw new TimeoutException($"管道读取在 {_readTimeoutMs}ms 内没有返回数据");
            }
            pooled.AsSpan(0, read).CopyTo(buffer);
            return read;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_operationDeadline is not null)
        {
            try
            {
                _stream.WriteAsync(data.ToArray(), _operationDeadline.Token).AsTask().GetAwaiter().GetResult();
                _stream.FlushAsync(_operationDeadline.Token).GetAwaiter().GetResult();
                return;
            }
            catch (OperationCanceledException) when (_operationDeadline.IsCancellationRequested)
            {
                throw new TimeoutException($"管道请求在 {_readTimeoutMs}ms 内未完成");
            }
        }
        _stream.Write(data);
        _stream.Flush();
    }

    public void Dispose()
    {
        _stream.Dispose();
        _operationDeadline?.Dispose();
    }
}
