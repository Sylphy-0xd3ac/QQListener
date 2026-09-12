using System.Collections.Concurrent;
using QQListener.Core;
using QQListener.Models;
using QQListener.Native;
using QQListener.Pipes;
using QQListener.Protocol;

namespace QQListener.Services;

/// <summary>
/// 原生捕获数据源：接管 <see cref="IQqNotificationPoller"/> 的位置。
///
/// <see cref="Services.QqListenerWorker"/> 上游完全不变——仍然是周期性
/// <see cref="PollAsync"/>，取走这段时间内到达的消息；只是消息来源从 Windows
/// 通知中心的弹窗文本，换成了 SnowLuma 注入 + HQP1 管道解出来的
/// <see cref="Protocol.CapturedMessage"/>。
///
/// 每个当前"有钩子在跑"的 QQ 进程各开一条后台线程读它的 recv 管道；谁来判断
/// "有没有钩子"由 <c>getActivePids</c> 决定（通常是 <see cref="CoreService.ActivePids"/>），
/// 本类自己不管注入/卸载，只管把管道里出来的消息接住转成 <see cref="MessageEvent"/>。
/// </summary>
public sealed class NativeQqMessageSource : IQqNotificationPoller, IDisposable
{
    private readonly Func<IReadOnlyList<int>> _getActivePids;
    private readonly Func<string, IPipeTransport> _connectTransport;
    private readonly Action<int>? _onCaptureConnected;
    private readonly Action<int>? _onCaptureDisconnected;
    private readonly Action<int, Exception>? _onCaptureError;

    private readonly ConcurrentQueue<MessageEvent> _queue = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, RecvCapture> _running = [];
    private volatile bool _disposed;

    private readonly Func<MessageEvent, MessageEvent?>? _processEvent;
    private readonly Action? _resetState;
    private readonly Func<CoreState>? _getCoreState;
    private readonly NativeMessageEnrichment? _enrichment;
    private readonly Func<int>? _getPendingLimit;
    private readonly PendingQueue<MessageEvent> _pendingQueue = new();
    private bool _wasPaused;

    /// <summary>消息落进 <see cref="_queue"/> 时完成一次，用来即时唤醒轮询方。
    /// 每次唤醒后换一个新的实例，等价于一个 auto-reset 事件。</summary>
    private TaskCompletionSource<bool> _arrival = CreateArrivalSignal();

    public NativeQqMessageSource(
        Func<IReadOnlyList<int>> getActivePids,
        Func<string, IPipeTransport> connectTransport,
        Action<int>? onCaptureConnected = null,
        Action<int>? onCaptureDisconnected = null,
        Action<int, Exception>? onCaptureError = null,
        Func<MessageEvent, MessageEvent?>? processEvent = null,
        Action? resetState = null,
        Func<CoreState>? getCoreState = null,
        NativeMessageEnrichment? enrichment = null,
        Func<int>? getPendingLimit = null)
    {
        _getActivePids = getActivePids;
        _connectTransport = connectTransport;
        _onCaptureConnected = onCaptureConnected;
        _onCaptureDisconnected = onCaptureDisconnected;
        _onCaptureError = onCaptureError;
        _processEvent = processEvent;
        _resetState = resetState;
        _getCoreState = getCoreState;
        _enrichment = enrichment;
        _getPendingLimit = getPendingLimit;
    }

    /// <summary>真实接线：走命名管道，把连接状态变化同步进 <see cref="CoreRuntime"/>，
    /// 把规则评估/去重接进 <paramref name="pipeline"/>，把暂停/恢复接进 <see cref="CoreController"/>，
    /// 把发送者备注、图片 rkey、文件下载地址接进 <paramref name="enrichment"/>。</summary>
    public static NativeQqMessageSource ForCoreService(
        CoreService coreService,
        MessageEventPipeline pipeline,
        NativeMessageEnrichment enrichment,
        QqListenerSettings settings) => new(
        () => [.. coreService.ActivePids],
        name => new NamedPipeTransport(name),
        onCaptureConnected: pid =>
            CoreRuntime.Set(CoreRuntimeState.Connected, "已连接到 QQ 接收管道", pid),
        onCaptureDisconnected: pid =>
            CoreRuntime.Set(CoreRuntimeState.Waiting, "接收管道已断开，等待重新连接", pid),
        onCaptureError: (pid, ex) =>
            CoreRuntime.Set(CoreRuntimeState.Error, $"接收管道异常：{ex.Message}", pid),
        processEvent: evt =>
        {
            var result = pipeline.Process(evt);
            return result.Accepted ? result.Event : null;
        },
        resetState: pipeline.ClearState,
        getCoreState: CoreController.GetState,
        enrichment: enrichment,
        getPendingLimit: () => settings.PendingQueueLimit);

    public Task<QqNotificationPollResult> PollAsync(bool retryPermission, CancellationToken cancellationToken)
    {
        Reconcile();

        cancellationToken.ThrowIfCancellationRequested();
        var state = _getCoreState?.Invoke() ?? CoreState.Running;
        if (state == CoreState.Detached)
        {
            _pendingQueue.Clear();
            while (_queue.TryDequeue(out _)) { }
            _wasPaused = false;
            return Task.FromResult(new QqNotificationPollResult(WindowsNotificationReadStatus.Success, [], ""));
        }
        var paused = state == CoreState.Paused;
        var events = new List<MessageEvent>();

        // 恢复前已到达但尚未轮询的消息，也并入这次摘要，避免恢复时连弹多个窗口。
        while (_queue.TryDequeue(out var evt))
        {
            if (paused || _wasPaused) _pendingQueue.Push(evt, _getPendingLimit?.Invoke());
            else events.Add(evt);
        }
        if (_wasPaused && !paused)
        {
            var (backlog, dropped) = _pendingQueue.Drain();
            if (PendingMessageDigest.Create(backlog, dropped) is { } digest) events.Add(digest);
        }
        _wasPaused = paused;
        return Task.FromResult(new QqNotificationPollResult(WindowsNotificationReadStatus.Success, events, ""));
    }

    /// <summary>原生捕获没有"通知访问权限"这回事；但仍然要清空规则管线的去重/冷却状态，
    /// 语义跟 toast 数据源的 <see cref="QqNotificationPoller.ResetDetectionState"/> 一致。</summary>
    public void ResetDetectionState()
    {
        _resetState?.Invoke();
    }

    /// <summary>按 <c>getActivePids</c> 当前的结果，起停后台捕获线程；公开以便测试直接驱动。</summary>
    public void Reconcile()
    {
        if (_disposed) return;
        var active = new HashSet<int>(_getActivePids());

        List<(int Pid, RecvCapture Capture)> toStop;
        List<int> toStart;
        lock (_gate)
        {
            toStop = [.. _running.Where(kv => !active.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value))];
            toStart = [.. active.Where(pid => !_running.ContainsKey(pid))];
        }

        foreach (var (pid, capture) in toStop)
        {
            capture.Stop();
            RemoveIfCurrent(pid, capture);
            // pid 真的不再活跃了（进程退出，或者被系统回收给别的进程）：忘掉它的
            // rkey 缓存，避免新进程沿用旧账号（甚至完全无关的进程）的密钥。
            _enrichment?.ForgetPid(pid);
        }

        foreach (var pid in toStart) StartCapture(pid);
    }

    private void StartCapture(int pid)
    {
        RecvCapture? capture = null;
        capture = new RecvCapture(
            pid,
            ProcessMessage,
            _connectTransport,
            onConnected: () => _onCaptureConnected?.Invoke(pid),
            onDisconnected: () =>
            {
                _onCaptureDisconnected?.Invoke(pid);
                RemoveIfCurrent(pid, capture!);
            },
            onError: ex => _onCaptureError?.Invoke(pid, ex));

        lock (_gate) _running[pid] = capture;
        new Thread(capture.Run) { Name = $"QQListener-Recv-{pid}", IsBackground = true }.Start();
    }

    /// <summary>
    /// 单条消息落地前的完整处理链：备注解析 → 转成 MessageEvent → 规则评估/去重 →
    /// （通过的话）图片 rkey / 文件地址解析 → 入队。
    ///
    /// 备注解析必须排在规则评估之前——"重要人物"规则是拿 Sender 的显示名去匹配，
    /// 备注没解析出来之前 Sender 可能还是群名片/昵称，跟用户在重要人物列表里
    /// 填的备注对不上。rkey/文件地址解析放在规则评估通过之后，被过滤掉的消息
    /// 不用白跑一次 control 管道往返。
    ///
    /// 这几步都是同步调用（会阻塞这个 pid 专属的捕获线程），换来的是不需要处理
    /// 额外的异步竞态/乱序；每一步内部都有超时和失败降级，见 NativeMessageEnrichment。
    /// </summary>
    private void ProcessMessage(CapturedMessage message)
    {
        _enrichment?.EnrichSenderProfile(message);

        var evt = NativeMessageEventFactory.From(message);
        var accepted = _processEvent is null ? evt : _processEvent(evt);
        if (accepted is null) return;

        // Segments 是 CapturedMessage 和 MessageEvent 共享的同一份列表引用
        // （NativeMessageEventFactory.From 没有拷贝），这里补的 Url 对 accepted 同样生效。
        _enrichment?.EnrichSegments(message);
        _queue.Enqueue(accepted);
        // 先入队再叫醒：被唤醒的一方一定能看到这条消息。
        Interlocked.Exchange(ref _arrival, CreateArrivalSignal()).TrySetResult(true);
    }

    /// <inheritdoc />
    public Task WaitForMessagesAsync(CancellationToken cancellationToken)
    {
        // 顺序不能反：必须先取信号再查队列。反过来的话，"查到队列是空的"和
        // "读到信号"这两步之间捕获线程正好入队并换掉信号，我们就会去等一个
        // 永远不会被点亮的新信号——那条已经躺在队列里的消息谁也叫不醒。
        var arrival = Volatile.Read(ref _arrival).Task;
        // 队列里已经有货就别等了——不然刚入队、还没人来取的那条消息会被压到
        // 下一次唤醒之后。
        if (!_queue.IsEmpty) return Task.CompletedTask;
        return arrival.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CreateArrivalSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 只删除属于"这个 capture 自己"的注册项。
    ///
    /// 旧 capture 停止时线程不会立刻退出（阻塞中的 Read 需要时间解开），如果这段
    /// 时间里 pid 又变回活跃、新 capture 已经顶上并注册，旧 capture 收尾时不加区分
    /// 地按 pid 删除，会把刚注册的新 capture 从 _running 里删掉——下一轮 Reconcile
    /// 看它"不存在"又会再起一个，变成两个 reader 同时读同一条管道。
    /// </summary>
    private void RemoveIfCurrent(int pid, RecvCapture capture)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(pid, out var current) && ReferenceEquals(current, capture))
                _running.Remove(pid);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        List<RecvCapture> captures;
        lock (_gate)
        {
            captures = [.. _running.Values];
            _running.Clear();
        }
        foreach (var capture in captures) capture.Stop();
        _pendingQueue.Clear();
        while (_queue.TryDequeue(out _)) { }
    }
}
