using System.Buffers.Binary;
using QQListener.Protocol;

namespace QQListener.Protocol.Tests;

/// <summary>移植自 Python 版 tests/native/test_hqp1.py。</summary>
public class Hqp1Tests
{
    [Fact]
    public void EncodeFrame_HeaderLayout()
    {
        var data = Hqp1.EncodeFrame(
            PipeOp.RecvPacket, status: 3, flags: 5, value0: 42,
            cmd: "ab", msg: "c", body: "xy"u8.ToArray());

        Assert.Equal(Hqp1.HeaderSize + 2 + 1 + 2, data.Length);
        Assert.Equal(Hqp1.Magic, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0)));
        Assert.Equal(Hqp1.Version, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)));
        Assert.Equal((ushort)PipeOp.RecvPacket, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)));
        Assert.Equal(5U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)));  // cmdLen
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)));  // msgLen
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28)));  // bodyLen
        Assert.Equal(42UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(32)));
        Assert.Equal("abcxy"u8.ToArray(), data[Hqp1.HeaderSize..]);
    }

    [Fact]
    public void FrameReader_BuffersAcrossChunks()
    {
        var frame = Hqp1.EncodeFrame(
            PipeOp.RecvPacket, value0: 7, cmd: "cmd", msg: "123", body: [0x01, 0x02]);

        var reader = new FrameReader();
        // 分两半喂入，验证跨块缓冲
        Assert.Empty(reader.Push(frame.AsSpan(0, 10)));

        var frames = reader.Push(frame.AsSpan(10));
        var f = Assert.Single(frames);
        Assert.Equal((int)PipeOp.RecvPacket, f.Op);
        Assert.Equal(7UL, f.Value0);
        Assert.Equal("cmd", f.Cmd);
        Assert.Equal("123", f.Msg);
        Assert.Equal<byte[]>([0x01, 0x02], f.Body);
    }

    [Fact]
    public void FrameReader_YieldsMultipleFramesFromOneChunk()
    {
        var a = Hqp1.EncodeFrame(PipeOp.RecvPacket, value0: 1, cmd: "a");
        var b = Hqp1.EncodeFrame(PipeOp.LoginState, value0: 2, msg: "10001");

        var frames = new FrameReader().Push([.. a, .. b]);

        Assert.Equal(2, frames.Count);
        Assert.Equal(1UL, frames[0].Value0);
        Assert.Equal("10001", frames[1].Msg);
    }

    [Fact]
    public void FrameReader_BadMagicThrows()
    {
        var reader = new FrameReader();
        var bad = new byte[Hqp1.HeaderSize];  // 全 0，magic 不对
        var ex = Assert.Throws<InvalidDataException>(() => reader.Push(bad));
        Assert.Contains("magic", ex.Message);
    }

    /// <summary>回归测试：bodyLen 是 0xFFFFFFFF 时，(int) 强转会变成 -1，
    /// 让"帧还没收全"的检查被绕过，最终在 Slice 里抛出一个跟协议错误无关的
    /// ArgumentOutOfRangeException。应该在校验阶段就识别成 InvalidDataException。</summary>
    [Fact]
    public void FrameReader_HugeBodyLengthThrowsInvalidDataInsteadOfCorrupting()
    {
        var reader = new FrameReader();
        var frame = Hqp1.EncodeFrame(PipeOp.Hello);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(28), 0xFFFFFFFF); // bodyLen

        var ex = Assert.Throws<InvalidDataException>(() => reader.Push(frame));
        Assert.Contains("too large", ex.Message);
    }

    /// <summary>合法但巨大的长度（远超真实协议用量）同样要被拒绝，
    /// 否则会让 _buffer 无限期攒下去等一个永远凑不齐的帧，内存无界增长。</summary>
    [Fact]
    public void FrameReader_LargePositiveBodyLengthIsAlsoRejected()
    {
        var reader = new FrameReader();
        var frame = Hqp1.EncodeFrame(PipeOp.Hello);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(28), 100 * 1024 * 1024); // 100 MiB

        Assert.Throws<InvalidDataException>(() => reader.Push(frame));
    }

    [Fact]
    public void FrameReader_IsPermanentlyFaultedAfterAnException()
    {
        // ControlClient 复用同一个 FrameReader 实例；一次解析错误之后继续 Push 只会
        // 在同一处再抛一次坏数据的异常，之前会一直重复原样保留的坏字节。这里锁定
        // "报废之后明确拒绝继续使用"这个更清楚的失败模式。
        var reader = new FrameReader();
        var badFrame = Hqp1.EncodeFrame(PipeOp.Hello);
        badFrame[4] = 0xFF; // 破坏 version 字段
        Assert.Throws<InvalidDataException>(() => reader.Push(badFrame));

        Assert.Throws<InvalidOperationException>(() => reader.Push(Hqp1.EncodeFrame(PipeOp.Hello)));
    }

    [Fact]
    public void PacketFromFrame_MapsOp6()
    {
        var frame = new Hqp1Frame(
            Op: (int)PipeOp.RecvPacket, RequestId: 0, Status: -3, Flags: 0,
            Value0: 99, Cmd: "trpc.x", Msg: "10001", Body: [0xAA]);

        var packet = Hqp1.PacketFromFrame(frame);

        Assert.NotNull(packet);
        Assert.Equal(99UL, packet!.Seq);
        Assert.Equal(-3, packet.Error);
        Assert.Equal("trpc.x", packet.Cmd);
        Assert.Equal("10001", packet.Uin);   // uin 走 msg 字段
        Assert.Equal<byte[]>([0xAA], packet.Body);
    }

    [Fact]
    public void PacketFromFrame_IgnoresOtherOps()
    {
        var frame = new Hqp1Frame((int)PipeOp.Hello, 0, 0, 0, 0, "", "", []);
        Assert.Null(Hqp1.PacketFromFrame(frame));
    }
}
