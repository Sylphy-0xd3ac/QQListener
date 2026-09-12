using QQListener.Pipes;

namespace QQListener.Protocol.Tests;

/// <summary>
/// 管道会话。对应 Python 侧 test_hook_client.py / test_control_client.py。
/// 用假传输喂字节，不碰真管道——这几条在 macOS 上也要能跑。
/// </summary>
public class RecvClientTests
{
    [Fact]
    public void RecvPacketsAreHandedOverAndOtherOpsIgnored()
    {
        var transport = new FakeTransport([
            Pb.Cat(
                Hqp1.EncodeFrame(PipeOp.Hello),
                Hqp1.EncodeFrame(PipeOp.RecvPacket,
                    status: 0, value0: 9, cmd: "trpc.msg.olpush.OlPushService.MsgPush",
                    msg: "10001", body: [0xAA])),
        ]);

        var packets = new List<RecvPacket>();
        new RecvClient(transport).Run(packets.Add);

        var packet = Assert.Single(packets);
        Assert.Equal(9UL, packet.Seq);
        Assert.Equal("trpc.msg.olpush.OlPushService.MsgPush", packet.Cmd);
        Assert.Equal("10001", packet.Uin);
        Assert.Equal(new byte[] { 0xAA }, packet.Body);
    }

    [Fact]
    public void FramesSplitAcrossReadsAreReassembled()
    {
        var frame = Hqp1.EncodeFrame(PipeOp.RecvPacket, value0: 1, cmd: "c", body: [1, 2, 3]);
        var transport = new FakeTransport([frame[..20], frame[20..]]);

        var packets = new List<RecvPacket>();
        new RecvClient(transport).Run(packets.Add);

        Assert.Single(packets);
    }

    [Fact]
    public void LoginStateIsReportedSeparately()
    {
        var transport = new FakeTransport([
            Hqp1.EncodeFrame(PipeOp.LoginState, flags: Hqp1.FlagLoggedIn, msg: "10001"),
        ]);

        var states = new List<LoginState>();
        new RecvClient(transport).Run(_ => Assert.Fail("登录态不该走消息回调"), states.Add);

        var state = Assert.Single(states);
        Assert.True(state.LoggedIn);
        Assert.Equal("10001", state.Uin);
    }
}

public class ControlClientTests
{
    [Fact]
    public void RequestWaitsForHelloThenAckThenReply()
    {
        var transport = new FakeTransport([
            Pb.Cat(
                Hqp1.EncodeFrame(PipeOp.Hello),
                Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
                Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1, body: [0x42])),
        ]);

        var reply = new ControlClient(transport).Send("Some.Cmd", [0x01]);

        Assert.Equal(0, reply.Error);
        Assert.Equal(new byte[] { 0x42 }, reply.Body);

        // 请求必须带 wantReply，否则内核不会回包。
        var sent = Assert.Single(new FrameReader().Push(Pb.Cat([.. transport.Writes])));
        Assert.Equal((int)PipeOp.SendRequest, sent.Op);
        Assert.Equal("Some.Cmd", sent.Cmd);
        Assert.Equal(Hqp1.FlagWantReply, sent.Flags & Hqp1.FlagWantReply);
    }

    [Fact]
    public void ErrorFrameBecomesAnException()
    {
        var transport = new FakeTransport([
            Pb.Cat(
                Hqp1.EncodeFrame(PipeOp.Hello),
                Hqp1.EncodeFrame(PipeOp.Error, requestId: 1, status: 3, msg: "not logged in")),
        ]);

        var error = Assert.Throws<IOException>(
            () => new ControlClient(transport).Send("Some.Cmd", []));
        Assert.Contains("not logged in", error.Message);
    }

    [Fact]
    public void FramesForOtherRequestsAreIgnored()
    {
        var transport = new FakeTransport([
            Pb.Cat(
                Hqp1.EncodeFrame(PipeOp.Hello),
                Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 99, body: [0xFF]),
                Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 7),
                Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 7, body: [0x07])),
        ]);

        var reply = new ControlClient(transport).Send("Some.Cmd", [], requestId: 7);

        Assert.Equal(new byte[] { 0x07 }, reply.Body);
    }

    [Fact]
    public void ClosedPipeFailsInsteadOfHanging()
    {
        var transport = new FakeTransport([]);

        Assert.Throws<IOException>(() => new ControlClient(transport).Send("Some.Cmd", []));
    }
}

public class PipeNameTests
{
    [Fact]
    public void PipeNamesMatchTheKernelsNaming()
    {
        Assert.Equal("mojo.4242.recv", PipeNames.Recv(4242));
        Assert.Equal("mojo.4242.control", PipeNames.Control(4242));
        Assert.Equal(@"\\.\pipe\mojo.4242.recv", PipeNames.FullRecv(4242));
    }
}
