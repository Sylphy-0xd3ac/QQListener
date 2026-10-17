using System.Collections.Concurrent;
using QQListener.Core;
using QQListener.Models;
using QQListener.Pipes;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

/// <summary>
/// <see cref="NativeQqMessageSource"/> 的编排逻辑：按活跃 pid 起停捕获线程、
/// 把解出来的消息喂进 <see cref="IQqNotificationPoller.PollAsync"/> 的取件口。
///
/// 捕获跑在真的后台线程上，断言用有界轮询等待——这是这类线程化生产者测试
/// 该有的样子，不是偷懒。
/// </summary>
public class NativeQqMessageSourceTests
{
    private const string PushCmd = "trpc.msg.olpush.OlPushService.MsgPush";

    private static byte[] GroupPushFrame(ulong seq, string text, ulong sender = 1001)
    {
        var group = Pb.Cat(Pb.VarintField(1, 123456), Pb.LenField(7, "高三2班"));
        var responseHead = Pb.Cat(Pb.VarintField(1, sender), Pb.LenField(8, group));
        var contentHead = Pb.Cat(Pb.VarintField(1, 82), Pb.VarintField(5, seq));
        var body = Pb.LenField(1, Pb.Rich(Pb.TextElem(text)));
        var message = Pb.Cat(Pb.LenField(1, responseHead), Pb.LenField(2, contentHead), Pb.LenField(3, body));
        var pushMsg = Pb.LenField(1, message);

        return Hqp1.EncodeFrame(PipeOp.RecvPacket, value0: 1, cmd: PushCmd, msg: "10000", body: pushMsg);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public async Task PollWithNoActivePidsSucceedsWithNoEvents()
    {
        var source = new NativeQqMessageSource(() => [], _ => throw new InvalidOperationException());

        var result = await source.PollAsync(retryPermission: false, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.AcceptedEvents);
    }

    [Fact]
    public async Task DecodedPushMessagesSurfaceAsMessageEventsOnNextPoll()
    {
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "明天带作业")]);
        using var source = new NativeQqMessageSource(() => [7], _ => transport);

        // 第一次 Poll 触发 Reconcile 起线程；消息到达是异步的，用第二次 Poll 有界重试着取。
        await source.PollAsync(false, CancellationToken.None);

        List<MessageEvent> events = [];
        Assert.True(WaitUntil(() =>
        {
            var result = source.PollAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            events.AddRange(result.AcceptedEvents);
            return events.Count > 0;
        }));

        var evt = Assert.Single(events);
        Assert.Equal("高三2班", evt.Conversation);
        Assert.Equal("明天带作业", evt.Body);
    }

    [Fact]
    public void ReconcileConnectsEachNewlyActivePidExactlyOnce()
    {
        var connects = new ConcurrentQueue<string>();
        IPipeTransport Connect(string name)
        {
            connects.Enqueue(name);
            return new FakeTransport([Hqp1.EncodeFrame(PipeOp.Hello)]); // 立刻"挂断"，线程很快自己退出
        }

        IReadOnlyList<int> active = [11, 22];
        using var source = new NativeQqMessageSource(() => active, Connect);

        source.Reconcile();
        source.Reconcile(); // 同一批 pid 再来一次，不应该重复连接仍在跑的那些

        Assert.True(WaitUntil(() => connects.Count >= 2));
        Assert.Contains("mojo.11.recv", connects);
        Assert.Contains("mojo.22.recv", connects);
    }

    [Fact]
    public void CaptureConnectAndDisconnectCallbacksFireWithThePid()
    {
        var connected = new ConcurrentBag<int>();
        var disconnected = new ConcurrentBag<int>();
        var transport = new FakeTransport([Hqp1.EncodeFrame(PipeOp.Hello)]);

        using var source = new NativeQqMessageSource(
            () => [33], _ => transport,
            onCaptureConnected: connected.Add,
            onCaptureDisconnected: disconnected.Add);

        source.Reconcile();

        Assert.True(WaitUntil(() => connected.Contains(33) && disconnected.Contains(33)));
    }

    [Fact]
    public async Task ProcessEventCanRejectAMessageBeforeItReachesThePoll()
    {
        // processEvent 就是规则管线/去重接进来的地方——之前这里完全没接，
        // 黑名单/重要人物/去重在原生捕获路径下形同虚设。
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "被规则挡住的消息")]);
        using var source = new NativeQqMessageSource(
            () => [7], _ => transport, processEvent: _ => null);

        await source.PollAsync(false, CancellationToken.None);

        Thread.Sleep(100); // 给捕获线程时间跑到 onMessage
        var result = await source.PollAsync(false, CancellationToken.None);

        Assert.Empty(result.AcceptedEvents);
    }

    [Fact]
    public async Task ProcessEventCanRewriteTheAcceptedMessage()
    {
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "原文")]);
        using var source = new NativeQqMessageSource(
            () => [7], _ => transport,
            processEvent: evt => evt with { Body = "改写后" });

        await source.PollAsync(false, CancellationToken.None);

        List<MessageEvent> events = [];
        Assert.True(WaitUntil(() =>
        {
            var result = source.PollAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            events.AddRange(result.AcceptedEvents);
            return events.Count > 0;
        }));

        Assert.Equal("改写后", Assert.Single(events).Body);
    }

    [Fact]
    public async Task PausedStateQueuesMessagesInsteadOfSurfacingThem()
    {
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "暂停期间到达")]);
        var paused = true;
        using var source = new NativeQqMessageSource(
            () => [7], _ => transport, getCoreState: () => paused ? CoreState.Paused : CoreState.Running);

        await source.PollAsync(false, CancellationToken.None);
        Thread.Sleep(100); // 给捕获线程时间把消息塞进内部队列

        var whilePaused = await source.PollAsync(false, CancellationToken.None);
        Assert.Empty(whilePaused.AcceptedEvents);
    }

    [Fact]
    public async Task ResumingFromPauseFlushesTheBacklogInOrder()
    {
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "暂停期间到达")]);
        var paused = true;
        using var source = new NativeQqMessageSource(
            () => [7], _ => transport, getCoreState: () => paused ? CoreState.Paused : CoreState.Running);

        await source.PollAsync(false, CancellationToken.None); // 触发 Reconcile 起线程

        // 消息到达是异步的；暂停期间 PollAsync 永远返回空结果，没有直接可观察的
        // 信号能确认"消息已经落进积压队列"，用一个固定的轮询窗口保证捕获线程
        // 有足够时间处理完，而不是只调用一次就假设它已经到达。
        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            await source.PollAsync(false, CancellationToken.None);
            Thread.Sleep(20);
        }

        paused = false;
        var afterResume = await source.PollAsync(false, CancellationToken.None);

        Assert.Equal("暂停期间到达", Assert.Single(afterResume.AcceptedEvents).Body);
    }

    /// <summary>
    /// 积压上限之前硬编码在 PendingQueue 的默认参数里（50 条），设置页没有入口、
    /// 调用点也不传值——用户填什么都没用。这里验证上限确实是从外面读进来的。
    /// </summary>
    [Fact]
    public async Task PendingBacklogHonoursTheConfiguredLimitAndDropsTheOldest()
    {
        var transport = new FakeTransport(
        [
            GroupPushFrame(seq: 1, text: "第一条"),
            GroupPushFrame(seq: 2, text: "第二条"),
            GroupPushFrame(seq: 3, text: "第三条"),
        ]);
        var paused = true;
        using var source = new NativeQqMessageSource(
            () => [7], _ => transport,
            getCoreState: () => paused ? CoreState.Paused : CoreState.Running,
            getPendingLimit: () => 2);

        await source.PollAsync(false, CancellationToken.None); // 触发 Reconcile 起线程

        var deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            await source.PollAsync(false, CancellationToken.None);
            Thread.Sleep(20);
        }

        paused = false;
        var afterResume = await source.PollAsync(false, CancellationToken.None);

        var digest = Assert.Single(afterResume.AcceptedEvents);
        Assert.Equal(["第二条", "第三条"], digest.PendingMessages.Select(x => x.Body));
        Assert.Equal(1, digest.PendingDropped);
        Assert.Contains("更早的 1 条已丢弃", digest.Sender);
    }

    /// <summary>
    /// 推送式取件：消息落地就把等待方叫醒，而不是让它按固定间隔回来问。
    /// </summary>
    [Fact]
    public async Task WaitForMessagesCompletesAsSoonAsAMessageLands()
    {
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "到了")]);
        using var source = new NativeQqMessageSource(() => [7], _ => transport);

        var waiting = source.WaitForMessagesAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted); // 还没起捕获线程，队列是空的

        source.Reconcile();

        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>队列里已经有货时不该再等下一条——否则先到的那条会被压到下一次唤醒之后。</summary>
    [Fact]
    public async Task WaitForMessagesReturnsImmediatelyWhileTheQueueStillHasItems()
    {
        var transport = new FakeTransport([GroupPushFrame(seq: 1, text: "到了")]);
        using var source = new NativeQqMessageSource(() => [7], _ => transport);

        source.Reconcile();
        await source.WaitForMessagesAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(source.WaitForMessagesAsync(CancellationToken.None).IsCompleted);
    }

    private sealed class FeedTransport : IPipeTransport
    {
        private readonly BlockingCollection<byte[]> _chunks = new();
        public int ReadCalls;
        public void Push(byte[] frame) => _chunks.Add(frame);
        public int Read(Span<byte> buffer)
        {
            Interlocked.Increment(ref ReadCalls);
            if (!_chunks.TryTake(out var frame, Timeout.Infinite)) return 0;
            frame.AsSpan().CopyTo(buffer);
            return frame.Length;
        }
        public void Write(ReadOnlySpan<byte> data) { }
        public void Dispose() => _chunks.CompleteAdding();
    }

    private static async Task Deliver(NativeQqMessageSource source, FeedTransport transport, ulong seq, string text)
    {
        var arrival = source.WaitForMessagesAsync(CancellationToken.None);
        transport.Push(GroupPushFrame(seq, text));
        await arrival.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MessagesDuringAfterClassDelayJoinTheSameDigest()
    {
        var clock = new Clock();
        var onClass = true;
        var transport = new FeedTransport();
        using var source = new NativeQqMessageSource(() => [7], _ => transport,
            isOnClass: () => onClass, getAfterClassDelaySeconds: () => 60, timeProvider: clock);
        await source.PollAsync(false, CancellationToken.None);
        await Deliver(source, transport, 1, "上课收到");
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        onClass = false;
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        clock.Advance(30);
        await Deliver(source, transport, 2, "拖堂期间收到");
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        clock.Advance(30);
        var digest = Assert.Single((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        Assert.Equal(["上课收到", "拖堂期间收到"], digest.PendingMessages.Select(m => m.Body));
    }

    [Fact]
    public async Task MessagesReceivedBeforePauseStayBeforeMessagesReceivedDuringPause()
    {
        var paused = false;
        var transport = new FeedTransport();
        using var source = new NativeQqMessageSource(() => [7], _ => transport,
            getCoreState: () => paused ? CoreState.Paused : CoreState.Running);
        source.Reconcile();
        await Deliver(source, transport, 1, "暂停前");
        paused = true;
        // The first message is still in the ready queue. A third read proves both
        // capture callbacks completed, without consuming that queue.
        transport.Push(GroupPushFrame(2, "暂停中"));
        Assert.True(WaitUntil(() => Volatile.Read(ref transport.ReadCalls) >= 3));
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        paused = false;
        var digest = Assert.Single((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        Assert.Equal(["暂停前", "暂停中"], digest.PendingMessages.Select(m => m.Body));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    [Fact]
    public async Task ClassBacklogWaitsThroughDelayAndTheNextClassRestartsTheDelay()
    {
        var clock = new Clock();
        var onClass = true;
        var state = CoreState.Running;
        var completed = new TaskCompletionSource();
        var transport = new FakeTransport([GroupPushFrame(1, "作业"), GroupPushFrame(2, "通知")]);
        IReadOnlyList<int> active = [7];
        using var source = new NativeQqMessageSource(() => active, _ => transport,
            onCaptureDisconnected: _ => completed.TrySetResult(), getCoreState: () => state,
            isOnClass: () => onClass, getAfterClassDelaySeconds: () => 60, timeProvider: clock);
        await source.PollAsync(false, CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        active = [];
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        onClass = false;
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        clock.Advance(59);
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        onClass = true;
        await source.PollAsync(false, CancellationToken.None);
        clock.Advance(120);
        onClass = false;
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        clock.Advance(60);
        state = CoreState.Paused;
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        state = CoreState.Running;
        var digest = Assert.Single((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
        Assert.Equal(["作业", "通知"], digest.PendingMessages.Select(m => m.Body));
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
    }

    [Fact]
    public async Task DetachClearsClassBacklogAndPendingDelay()
    {
        var onClass = true;
        var state = CoreState.Running;
        var completed = new TaskCompletionSource();
        var transport = new FakeTransport([GroupPushFrame(1, "积压")]);
        IReadOnlyList<int> active = [7];
        using var source = new NativeQqMessageSource(() => active, _ => transport,
            onCaptureDisconnected: _ => completed.TrySetResult(), getCoreState: () => state,
            isOnClass: () => onClass, getAfterClassDelaySeconds: () => 60);
        await source.PollAsync(false, CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        active = [];
        state = CoreState.Detached;
        await source.PollAsync(false, CancellationToken.None);
        onClass = false;
        state = CoreState.Running;
        Assert.Empty((await source.PollAsync(false, CancellationToken.None)).AcceptedEvents);
    }

    [Theory]
    [InlineData(true, 0)] [InlineData(false, 1)]
    public async Task SelfEchoIsExcludedByDefaultAndCanBeEnabled(bool exclude, int expected)
    {
        var completed = new TaskCompletionSource();
        var transport = new FakeTransport([GroupPushFrame(1, "自己发的", sender: 10000)]);
        IReadOnlyList<int> active = [7];
        using var source = new NativeQqMessageSource(() => active, _ => transport,
            onCaptureDisconnected: _ => completed.TrySetResult(), excludeSelfMessages: () => exclude);
        source.Reconcile();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        active = [];
        var result = await source.PollAsync(false, CancellationToken.None);
        Assert.Equal(expected, result.AcceptedEvents.Count);
    }

    [Fact]
    public void ResetDetectionStateInvokesTheSuppliedResetCallback()
    {
        var resetCalled = false;
        var source = new NativeQqMessageSource(() => [], _ => throw new InvalidOperationException(),
            resetState: () => resetCalled = true);

        source.ResetDetectionState();

        Assert.True(resetCalled);
    }

    [Fact]
    public void DisposeStopsRunningCapturesWithoutCrashing()
    {
        var transport = new BlockingForeverTransport();
        var source = new NativeQqMessageSource(() => [1], _ => transport);

        source.Reconcile();
        Thread.Sleep(50); // 让捕获线程真正卡进阻塞的 Read

        source.Dispose(); // 不应该抛异常，也不应该挂起
    }

    /// <summary>永远阻塞直到 Dispose，再抛异常——模拟一条真实命名管道被关掉。</summary>
    private sealed class BlockingForeverTransport : IPipeTransport
    {
        private readonly ManualResetEventSlim _closed = new(false);

        public int Read(Span<byte> buffer)
        {
            _closed.Wait();
            throw new ObjectDisposedException(nameof(BlockingForeverTransport));
        }

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Dispose() => _closed.Set();
    }
}
