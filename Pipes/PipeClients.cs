using QQListener.Protocol;

namespace QQListener.Pipes;

/// <summary>登录态推送（op=7）。</summary>
public sealed record LoginState(bool LoggedIn, string Uin);

/// <summary>control 管道的一次请求应答。</summary>
public sealed record SendReply(uint RequestId, int Error, string Message, byte[] Body);

/// <summary>
/// recv 管道会话：持续读帧，把 op=6 映射成逻辑包回调出去。
/// </summary>
public sealed class RecvClient(IPipeTransport transport) : IDisposable
{
    private const int ReadBufferSize = 65536;
    private volatile bool _stopped;

    public void Stop()
    {
        _stopped = true;
        transport.Dispose();
    }

    /// <summary>阻塞读，直到管道关闭或 <see cref="Stop"/>。放在后台线程里跑。</summary>
    public void Run(Action<RecvPacket> onPacket, Action<LoginState>? onLogin = null)
    {
        var reader = new FrameReader();
        var buffer = new byte[ReadBufferSize];

        while (!_stopped)
        {
            var read = transport.Read(buffer);
            if (read <= 0) return;   // 管道关了

            foreach (var frame in reader.Push(buffer.AsSpan(0, read)))
            {
                if (frame.Op == (int)PipeOp.LoginState)
                {
                    if (onLogin is not null)
                    {
                        var loggedIn = (frame.Flags & Hqp1.FlagLoggedIn) != 0 || frame.Status != 0;
                        var uin = frame.Msg.Length > 0 ? frame.Msg : frame.Value0.ToString();
                        onLogin(new LoginState(loggedIn, uin));
                    }
                    continue;
                }

                if (Hqp1.PacketFromFrame(frame) is { } packet)
                    onPacket(packet);
            }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// control 管道的单请求客户端。
///
/// 每次请求临时连接，避免与持续运行的 recv 管道互相影响。
/// 协议是 hello → sendRequest → sendAck → sendReply。
/// </summary>
public sealed class ControlClient(IPipeTransport transport) : IDisposable
{
    private const int ReadBufferSize = 65536;
    private readonly FrameReader _reader = new();
    private readonly List<Hqp1Frame> _pending = [];
    private bool _closed;

    public SendReply Send(string cmd, byte[] body, uint requestId = 1)
    {
        WaitForHello();

        transport.Write(Hqp1.EncodeFrame(
            PipeOp.SendRequest,
            requestId: requestId,
            flags: Hqp1.FlagWantReply,
            cmd: cmd,
            body: body));

        var ackReceived = false;
        while (true)
        {
            foreach (var frame in ReadFrames())
            {
                if (frame.RequestId != requestId) continue;

                if (frame.Op == (int)PipeOp.Error)
                    throw new IOException(frame.Msg.Length > 0
                        ? frame.Msg
                        : $"control request failed: {frame.Status}");

                if (frame.Op == (int)PipeOp.SendAck)
                {
                    ackReceived = true;
                    continue;
                }

                if (frame.Op == (int)PipeOp.SendReply)
                {
                    if (!ackReceived)
                        throw new IOException("control reply arrived before ack");
                    return new SendReply(requestId, frame.Status, frame.Msg, frame.Body);
                }
            }
        }
    }

    /// <summary>内核连上后先发 hello；不等它就发请求会被丢掉。</summary>
    private void WaitForHello()
    {
        while (true)
        {
            var frames = ReadFrames();
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i].Op != (int)PipeOp.Hello) continue;
                // hello 之后的帧留着，下一轮 ReadFrames 先吃它们
                _pending.AddRange(frames.Skip(i + 1));
                return;
            }
        }
    }

    private IReadOnlyList<Hqp1Frame> ReadFrames()
    {
        if (_pending.Count > 0)
        {
            var buffered = _pending.ToArray();
            _pending.Clear();
            return buffered;
        }

        var buffer = new byte[ReadBufferSize];
        while (true)
        {
            var read = transport.Read(buffer);
            if (read <= 0) throw new IOException("control pipe closed");

            var frames = _reader.Push(buffer.AsSpan(0, read));
            if (frames.Count > 0) return frames;
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        transport.Dispose();
    }
}
