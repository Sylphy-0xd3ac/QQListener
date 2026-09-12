using System.Buffers.Binary;
using System.Text;

namespace QQListener.Protocol;

/// <summary>HQP1 管道操作码。取自 SnowLuma 公开源码 packages/bridge/src/qq-hook-client.ts。</summary>
public enum PipeOp
{
    Hello = 1,
    SendRequest = 2,
    SendAck = 3,
    SendReply = 4,
    Error = 5,
    RecvPacket = 6,
    LoginState = 7
}

/// <summary>一帧 HQP1。</summary>
public sealed record Hqp1Frame(
    int Op,
    uint RequestId,
    int Status,
    uint Flags,
    ulong Value0,
    string Cmd,
    string Msg,
    byte[] Body);

/// <summary>op=6 映射出来的逻辑包：一条 QQ 私有 SSO 报文。</summary>
public sealed record RecvPacket(ulong Seq, int Error, string Cmd, string Uin, byte[] Body);

/// <summary>
/// HQP1 帧协议。
///
/// 帧 = 40 字节头 + cmd + msg + body，全部小端：
/// <code>
///   0   u32  magic 0x31504851 ("QHP1")
///   4   u16  version = 1
///   6   u16  op
///   8   u32  requestId
///   12  i32  status
///   16  u32  flags
///   20  u32  cmdLen
///   24  u32  msgLen
///   28  u32  bodyLen
///   32  u64  value0
/// </code>
/// </summary>
public static class Hqp1
{
    public const uint Magic = 0x31504851;
    public const ushort Version = 1;
    public const int HeaderSize = 40;

    public const uint FlagWantReply = 1 << 0;
    public const uint FlagLoggedIn = 1 << 2;

    public static byte[] EncodeFrame(
        PipeOp op,
        uint requestId = 0,
        int status = 0,
        uint flags = 0,
        ulong value0 = 0,
        string cmd = "",
        string msg = "",
        byte[]? body = null)
    {
        var cmdBytes = Encoding.UTF8.GetBytes(cmd);
        var msgBytes = Encoding.UTF8.GetBytes(msg);
        body ??= [];

        var frame = new byte[HeaderSize + cmdBytes.Length + msgBytes.Length + body.Length];
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], (ushort)op);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], requestId);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], status);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)cmdBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)msgBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)body.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], value0);

        var offset = HeaderSize;
        cmdBytes.CopyTo(frame, offset); offset += cmdBytes.Length;
        msgBytes.CopyTo(frame, offset); offset += msgBytes.Length;
        body.CopyTo(frame, offset);
        return frame;
    }

    /// <summary>op=6（recvPacket）→ 逻辑包；其他 op 返回 null。</summary>
    public static RecvPacket? PacketFromFrame(Hqp1Frame frame) =>
        frame.Op != (int)PipeOp.RecvPacket
            ? null
            : new RecvPacket(frame.Value0, frame.Status, frame.Cmd, frame.Msg, frame.Body);
}

/// <summary>
/// 流式拆帧：管道给的是字节流，一次 read 可能拿到半帧或多帧。
/// </summary>
public sealed class FrameReader
{
    /// <summary>
    /// 单个长度字段（cmd/msg/body）的上限。真实协议里 cmd/msg 都是短字符串，
    /// body 通常也就到 KB 级——64 MiB 已经远超正常用量，只是用来堵住两类问题：
    /// 一是 uint32 强转 int 时，超过 0x80000000 的值会变成负数，让"帧还没收全"
    /// 的判断被绕过，最终在 Slice 里炸出一个跟协议错误无关的 ArgumentOutOfRangeException；
    /// 二是即便长度字段是合法的巨大正数，也会让 _buffer 无限期攒下去等一个永远
    /// 凑不齐的帧，内存无界增长到 OOM。三个字段都在这个上限内时，三者相加
    /// 也不可能溢出 int，因此不用再额外校验 total。
    /// </summary>
    private const uint MaxFieldLength = 64 * 1024 * 1024;

    private readonly List<byte> _buffer = [];
    private bool _faulted;

    /// <summary>
    /// 抛出异常后 <see cref="_buffer"/> 原样保留坏字节，同一个 reader 再 Push 只会
    /// 在同一处再抛一次——<see cref="Pipes.ControlClient"/> 复用同一个 reader
    /// （<see cref="Pipes.RecvClient"/> 每个会话都新建一个，不受影响），一旦解析
    /// 出错就该整个报废，而不是让调用方误以为再试一次还有意义。
    /// </summary>
    public IReadOnlyList<Hqp1Frame> Push(ReadOnlySpan<byte> chunk)
    {
        if (_faulted)
            throw new InvalidOperationException("此 FrameReader 已经因为帧解析错误报废，不能继续使用");

        try
        {
            return PushCore(chunk);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private IReadOnlyList<Hqp1Frame> PushCore(ReadOnlySpan<byte> chunk)
    {
        _buffer.AddRange(chunk);
        var frames = new List<Hqp1Frame>();

        while (_buffer.Count >= Hqp1.HeaderSize)
        {
            var header = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer);

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[0..]);
            var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            if (magic != Hqp1.Magic || version != Hqp1.Version)
                throw new InvalidDataException(
                    $"bad frame header magic=0x{magic:x} version={version}");

            var op = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            var requestId = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var status = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var cmdLenRaw = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var msgLenRaw = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var bodyLenRaw = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
            var value0 = BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);

            if (cmdLenRaw > MaxFieldLength || msgLenRaw > MaxFieldLength || bodyLenRaw > MaxFieldLength)
                throw new InvalidDataException(
                    $"frame length field too large: cmdLen={cmdLenRaw} msgLen={msgLenRaw} bodyLen={bodyLenRaw}");

            var cmdLen = (int)cmdLenRaw;
            var msgLen = (int)msgLenRaw;
            var bodyLen = (int)bodyLenRaw;
            var total = Hqp1.HeaderSize + cmdLen + msgLen + bodyLen;

            if (_buffer.Count < total)
                break;  // 帧还没收全，等下一块

            var offset = Hqp1.HeaderSize;
            var cmd = Encoding.UTF8.GetString(header.Slice(offset, cmdLen));
            offset += cmdLen;
            var msg = Encoding.UTF8.GetString(header.Slice(offset, msgLen));
            offset += msgLen;
            var body = header.Slice(offset, bodyLen).ToArray();

            _buffer.RemoveRange(0, total);
            frames.Add(new Hqp1Frame(op, requestId, status, flags, value0, cmd, msg, body));
        }

        // recv 是长连接，_buffer 的底层数组只增不减；收过一个接近 64 MiB 上限的帧
        // 之后，这块内存会被这个 reader 一直占着。缓冲区真正见底时顺手释放掉。
        if (_buffer.Count == 0 && _buffer.Capacity > 0)
            _buffer.Capacity = 0;

        return frames;
    }
}
