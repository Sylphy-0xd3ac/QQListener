using QQListener.Native;
using QQListener.Pipes;

namespace QQListener.Protocol.Tests;

/// <summary>对应 Python 侧 test_capture.py 里 RecvCapture 的两个行为测试。</summary>
public class RecvCaptureTests
{
    [Fact]
    public void LoginStateSuppliesSelfAccountWhenThePacketOmitsIt()
    {
        var response = Pb.Cat(Pb.VarintField(1, 10000), Pb.LenField(8, Pb.VarintField(1, 123456)));
        var message = Pb.Cat(Pb.LenField(1, response), Pb.LenField(2, Pb.VarintField(1, 82)),
            Pb.LenField(3, Pb.LenField(1, Pb.Rich(Pb.TextElem("自己发的")))));
        var transport = new FakeTransport([
            Hqp1.EncodeFrame(PipeOp.LoginState, flags: Hqp1.FlagLoggedIn, msg: "10000"),
            Hqp1.EncodeFrame(PipeOp.RecvPacket, cmd: "trpc.msg.olpush.OlPushService.MsgPush",
                body: Pb.LenField(1, message))
        ]);
        var got = new List<CapturedMessage>();
        new RecvCapture(1, got.Add, _ => transport).Run();
        Assert.True(Assert.Single(got).IsSelf);
    }

    [Fact]
    public void NonMessageCmdsDoNotProduceAMessage()
    {
        // 非消息 cmd 不该产出；真消息路径由 SsoDecodeTests 的 fixture 覆盖。
        var transport = new FakeTransport([
            Hqp1.EncodeFrame(PipeOp.RecvPacket, value0: 1, cmd: "not.a.message", msg: "10001", body: []),
        ]);
        var got = new List<CapturedMessage>();

        new RecvCapture(1, got.Add, _ => transport).Run();

        Assert.Empty(got);
    }

    [Fact]
    public void ReportsPipeConnectionLifecycle()
    {
        var transport = new FakeTransport([Hqp1.EncodeFrame(PipeOp.Hello)]);
        var lifecycle = new List<string>();

        new RecvCapture(1, _ => { }, _ => transport,
            onConnected: () => lifecycle.Add("connected"),
            onDisconnected: () => lifecycle.Add("disconnected")).Run();

        Assert.Equal(["connected", "disconnected"], lifecycle);
    }

    /// <summary>模拟一条真实命名管道：<c>Read</c> 一直阻塞，直到 <c>Dispose</c>——
    /// 这时再抛异常，就跟 Windows 命名管道被关掉时的行为一样。</summary>
    private sealed class BlockingTransport : IPipeTransport
    {
        private readonly ManualResetEventSlim _closed = new(false);

        public int Read(Span<byte> buffer)
        {
            _closed.Wait();
            throw new ObjectDisposedException(nameof(BlockingTransport));
        }

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Dispose() => _closed.Set();
    }

    [Fact]
    public void StoppingACaptureWhileReadIsBlockedDoesNotCrashItsThread()
    {
        // 回归测试：Stop() 靠 dispose 传输打断阻塞中的 Read，这会让 Read 抛异常。
        // 不接住它，异常会顶着裸 Thread 冒出去，变成整个宿主进程的未处理异常崩溃。
        var transport = new BlockingTransport();
        var capture = new RecvCapture(1, _ => { }, _ => transport);
        Exception? escaped = null;
        var threadDone = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try { capture.Run(); }
            catch (Exception ex) { escaped = ex; }
            finally { threadDone.Set(); }
        })
        { IsBackground = true };
        thread.Start();

        Thread.Sleep(50); // 给线程时间真正卡进阻塞的 Read
        capture.Stop();

        Assert.True(threadDone.Wait(TimeSpan.FromSeconds(2)), "捕获线程应该在 Stop() 后退出");
        Assert.Null(escaped);
    }

    /// <summary>连接阶段就失败（比如注入完成但 recv 管道还没建好）不该杀死进程，
    /// 这条路径以前在 try 块之外，Stop() 触发路径的修复完全没覆盖到它。</summary>
    private sealed class ThrowingTransportFactory
    {
        public IPipeTransport Connect(string _) => throw new TimeoutException("recv 管道还没建好");
    }

    [Fact]
    public void ConnectFailureDoesNotEscapeRun()
    {
        var factory = new ThrowingTransportFactory();
        var disconnected = false;

        var exception = Record.Exception(() =>
            new RecvCapture(1, _ => { }, factory.Connect, onDisconnected: () => disconnected = true).Run());

        Assert.Null(exception);
        // 连接从没成功过，走到 finally 时报"已断开"是合理的：
        // 上层用它来判断"这次捕获尝试已经结束，可以再排一次"。
        Assert.True(disconnected);
    }

    /// <summary>正常运行期间遇到协议解析异常（不是 Stop() 触发的）同样不该杀死进程——
    /// 这是"Pipes and capture concurrency bug hunt"揪出来的那个更大的漏洞：老版本的
    /// `catch (Exception) when (_stopRequested)` 只堵住了主动停止这一条路径。</summary>
    private sealed class SingleBadFrameTransport : IPipeTransport
    {
        private bool _served;

        public int Read(Span<byte> buffer)
        {
            if (_served) return 0; // 之后表现为管道关闭，避免测试死循环
            _served = true;
            // 拿一个合法帧，破坏 version 字段——FrameReader 会在帧头校验时
            // 抛 InvalidDataException，模拟管道数据错位/损坏。
            var frame = Hqp1.EncodeFrame(PipeOp.Hello);
            frame[4] = 0xFF;
            frame[5] = 0xFF;
            frame.CopyTo(buffer);
            return frame.Length;
        }

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void ProtocolErrorDuringNormalOperationDoesNotEscapeRun()
    {
        var transport = new SingleBadFrameTransport();
        Exception? reported = null;

        var exception = Record.Exception(() =>
            new RecvCapture(1, _ => { }, _ => transport, onError: ex => reported = ex).Run());

        Assert.Null(exception);
        Assert.NotNull(reported);
    }

    /// <summary>一个包解析失败（帧本身合法，但内容解不出来）不该打断整条 recv 会话——
    /// 之前这类异常会一路冒出 client.Run，被外层 catch 当成"连接失败"处理，导致
    /// 一次不必要的断线重连，重连后大概率还会重新收到同一条坏消息。</summary>
    [Fact]
    public void PacketLevelParseErrorDoesNotEscapeRunAndLaterGoodPacketsStillArrive()
    {
        const string pushCmd = "trpc.msg.olpush.OlPushService.MsgPush";

        // 帧本身合法（能正确切出一个 RecvPacket），但 body 里的字段号是 0——
        // Wire.DecodeFields 会在这里直接抛出，模拟"包能收到但内容解不出来"。
        var badPacket = Hqp1.EncodeFrame(
            PipeOp.RecvPacket, value0: 1, status: 0, cmd: pushCmd, msg: "10000", body: [0x00]);

        var responseHead = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, Pb.VarintField(1, 123456)));
        var contentHead = Pb.Cat(Pb.VarintField(1, 82), Pb.VarintField(5, 1));
        var pushBody = Pb.LenField(1, Pb.Rich(Pb.TextElem("收到")));
        var message = Pb.Cat(Pb.LenField(1, responseHead), Pb.LenField(2, contentHead), Pb.LenField(3, pushBody));
        var goodPacket = Hqp1.EncodeFrame(
            PipeOp.RecvPacket, value0: 2, status: 0, cmd: pushCmd, msg: "10000", body: Pb.LenField(1, message));

        var transport = new FakeTransport([badPacket, goodPacket]);
        var received = new List<CapturedMessage>();
        Exception? reported = null;

        var exception = Record.Exception(() =>
            new RecvCapture(1, received.Add, _ => transport, onError: ex => reported = ex).Run());

        Assert.Null(exception);
        Assert.NotNull(reported);
        var msg = Assert.Single(received);
        Assert.Equal("收到", MessageText.Body(msg));
    }
}
