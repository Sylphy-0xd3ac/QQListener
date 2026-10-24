using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using QQListener.Protocol;

namespace QQListener.Core;

/// <summary>Owns a per-QQ pipe; no Rust launcher, manager, HTTP service or startup token.</summary>
public sealed class PmhqLocalClient : IPmhqClient
{
    private readonly int _pid;
    private readonly Func<bool> _ready;
    private readonly Func<NamedPipeServerStream> _create;
    private readonly Func<NamedPipeServerStream, int> _peerPid;
    private readonly Func<string>? _currentAccount;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<byte[]>> _pending = new();
    private NamedPipeServerStream? _waiting;
    private NamedPipeServerStream? _pipe;
    private NamedPipeServerStream? _acceptedPipe;
    private PmhqHealth _health;
    private long _nextId;
    private int _receiving;
    private int _disposed;
    public bool IsConnected => Volatile.Read(ref _disposed) == 0
        && Volatile.Read(ref _acceptedPipe) is { } pipe && ReferenceEquals(pipe, Volatile.Read(ref _pipe));

    public PmhqLocalClient(int pid, Func<bool> ready, Func<NamedPipeServerStream>? create = null,
        Func<NamedPipeServerStream, int>? peerPid = null, Func<string>? currentAccount = null)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        _pid = pid;
        _ready = ready;
        _currentAccount = currentAccount;
        _health = new(pid, "", "") { IsLocalPipe = true };
        _create = create ?? (() => new NamedPipeServerStream("pmhq_cmd_" + pid, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536));
        _peerPid = peerPid ?? (pipe => OperatingSystem.IsWindows()
            && GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) ? checked((int)client) : 0);
        // Create before injection: the DLL can connect immediately from its hook worker.
        _waiting = _create();
    }

    public Task<PmhqHealth> GetHealthAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _lifetime.Token.ThrowIfCancellationRequested();
        return Task.FromResult(ObserveAccount());
    }

    private PmhqHealth ObserveAccount()
    {
        if (_currentAccount is null) return Volatile.Read(ref _health);
        var account = _currentAccount();
        if (!ulong.TryParse(account, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            account = "";
        lock (_gate)
        {
            var health = _health;
            if (health.Uin.Length != 0 && health.Uin != account) throw new PmhqAccountChangedException();
            return _health = health with { Uin = account };
        }
    }

    public byte[] Send(int pid, string command, byte[] body) => SendAsync(pid, command, body).GetAwaiter().GetResult();

    internal async Task<byte[]> SendAsync(int pid, string command, byte[] body)
    {
        var pipe = Volatile.Read(ref _pipe) ?? throw new IOException("PMHQ 本机管道已断开");
        if (pid != _pid || !IsConnected || !ObserveAccount().LoggedIn || !_ready())
            throw new IOException("PMHQ 本机 QQ 会话尚未就绪");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        var id = checked((ulong)Interlocked.Increment(ref _nextId));
        var request = PmhqNativeProtocol.Request(id, command, body);
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await _write.WaitAsync(token);
            try
            {
                if (!IsConnected || !ReferenceEquals(pipe, Volatile.Read(ref _pipe)))
                    throw new IOException("PMHQ 请求所属的管道已断开，未向新连接重发");
                if (!ObserveAccount().LoggedIn) throw new IOException("PMHQ 对应的 QQ 尚未登录");
                try
                {
                    await pipe.WriteAsync(request, token);
                    await pipe.FlushAsync(token);
                }
                catch
                {
                    // A partial JSON line cannot be reused by a later request.
                    Interlocked.CompareExchange(ref _acceptedPipe, null, pipe);
                    pipe.Dispose();
                    throw;
                }
            }
            finally { _write.Release(); }
            // Correlation IDs are unique for this instance. Never replay a send after a disconnect.
            return await completion.Task.WaitAsync(token);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task ReceiveAsync(Action<CapturedMessage> receive, Action<PmhqHealth> onHealth, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _receiving, 1, 0) != 0)
            throw new InvalidOperationException("PMHQ 已有接收连接");
        try { await ReceiveConnectionAsync(receive, onHealth, token); }
        finally { Volatile.Write(ref _receiving, 0); }
    }

    private async Task ReceiveConnectionAsync(Action<CapturedMessage> receive, Action<PmhqHealth> onHealth, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        NamedPipeServerStream pipe;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            pipe = _waiting ?? _create();
            _waiting = null;
        }
        var messages = Channel.CreateBounded<CapturedMessage>(new BoundedChannelOptions(500)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });
        Task? delivery = null;
        Task? reader = null;
        try
        {
            lock (_gate) _pipe = pipe;
            await pipe.WaitForConnectionAsync(linked.Token);
            if (_peerPid(pipe) != _pid) throw new IOException("PMHQ 管道连接者不是所选 QQ 进程");
            Volatile.Write(ref _acceptedPipe, pipe);
            reader = ReadAsync(pipe, messages.Writer, onHealth, linked.Token);
            // Enrichment can issue requests. It must never block the same loop reading their replies.
            delivery = Task.Run(async () =>
            {
                await foreach (var message in messages.Reader.ReadAllAsync(linked.Token)) receive(message);
            }, linked.Token);
            await await Task.WhenAny(reader, delivery);
        }
        finally
        {
            Interlocked.CompareExchange(ref _acceptedPipe, null, pipe);
            linked.Cancel();
            pipe.Dispose();
            Interlocked.CompareExchange(ref _pipe, null, pipe);
            messages.Writer.TryComplete();
            foreach (var pending in _pending.Values) pending.TrySetException(new IOException("PMHQ 管道已断开，请等待新消息后重试"));
            if (reader is not null) try { await reader; } catch (Exception) when (linked.IsCancellationRequested) { }
            if (delivery is not null) try { await delivery; } catch (Exception) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task ReadAsync(Stream pipe, ChannelWriter<CapturedMessage> messages,
        Action<PmhqHealth> onHealth, CancellationToken token)
    {
        var buffer = new byte[65536];
        using var line = new MemoryStream();
        int read;
        while ((read = await pipe.ReadAsync(buffer, token)) != 0)
        {
            var offset = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n') continue;
                Append(buffer.AsSpan(offset, i - offset));
                if (line.Length != 0) Handle(PmhqNativeProtocol.Parse(line.ToArray()));
                line.SetLength(0);
                offset = i + 1;
            }
            Append(buffer.AsSpan(offset, read - offset));
        }
        if (line.Length != 0) throw new IOException("PMHQ 管道返回了不完整的数据帧");

        void Append(ReadOnlySpan<byte> bytes)
        {
            if (line.Length + bytes.Length > PmhqProtocol.MaxJsonBytes) throw new IOException("PMHQ 管道数据超过大小限制");
            line.Write(bytes);
        }

        void Handle(PmhqNativeProtocol.Frame frame)
        {
            if (frame.Type == "resp")
            {
                if (_pending.TryRemove(frame.Id, out var pending))
                {
                    if (frame.Code == 0) pending.TrySetResult(frame.Body);
                    else pending.TrySetException(new IOException($"QQ 拒绝 PMHQ 请求（{frame.Code}）"));
                }
                return;
            }
            if (frame.Type is not ("recv" or "send")) return;
            var health = ObserveAccount();
            if (frame.Uin.Length != 0)
            {
                if (health.Uin.Length != 0 && health.Uin != frame.Uin) throw new PmhqAccountChangedException();
                health = health with { Uin = frame.Uin };
                Volatile.Write(ref _health, health);
                onHealth(health);
            }
            if (!health.LoggedIn || frame.Type != "recv" || frame.Echo.Length != 0 || frame.Body.Length == 0) return;
            var message = SsoDecode.DecodeMessagePush(new RecvPacket(0, 0, frame.Command, health.Uin, frame.Body));
            if (message is null) return;
            message.SourcePid = _pid;
            if (!message.IsGroup && message.AccountUid.Length != 0)
                Volatile.Write(ref _health, health with { Uid = message.AccountUid });
            messages.TryWrite(message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        lock (_gate) { _waiting?.Dispose(); _waiting = null; _pipe?.Dispose(); }
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("PMHQ 会话已结束"));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
