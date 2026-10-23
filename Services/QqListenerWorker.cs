using System.ComponentModel;
using QQListener.Models;

namespace QQListener.Services;

public sealed class QqListenerWorker
{
    private readonly QqListenerSettings _settings;
    private readonly IQqNotificationPoller _poller;
    private readonly LatestMessageState _latestMessage;
    private readonly IQqListenerDelay _delay;
    private readonly QqListenerDiagnosticsState _diagnostics;
    private readonly MessageAutomationEventHub _automationEventHub;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _pollDispatchGate = new(1, 1);
    private long _generation;
    private CancellationTokenSource _generationCancellation = new();
    private TaskCompletionSource<bool> _settingsChanged = CreateSignal();

    public QqListenerWorker(
        QqListenerSettings settings,
        IQqNotificationPoller poller,
        LatestMessageState latestMessage,
        IQqListenerDelay delay,
        QqListenerDiagnosticsState diagnostics,
        MessageAutomationEventHub automationEventHub)
    {
        _settings = settings;
        _poller = poller;
        _latestMessage = latestMessage;
        _delay = delay;
        _diagnostics = diagnostics;
        _automationEventHub = automationEventHub;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    public async Task RunAsync(
        Func<MessageEvent, CancellationToken, Task> dispatchAsync,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_settings.IsEnabled)
                {
                    consecutiveFailures = 0;
                    await WaitForDelayOrSettingsChangeAsync(HeartbeatInterval, cancellationToken);
                    continue;
                }

                var result = await PollOnceAndDispatchAsync(
                    retryPermission: false,
                    dispatchAsync,
                    cancellationToken);

                if (!_settings.IsEnabled)
                {
                    consecutiveFailures = 0;
                    continue;
                }

                if (result.IsSuccess)
                {
                    consecutiveFailures = 0;
                    await WaitForNextMessageAsync(cancellationToken);
                }
                else
                {
                    consecutiveFailures++;
                    await WaitForDelayOrSettingsChangeAsync(
                        GetFailureDelay(consecutiveFailures), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 轮询/派发链路上任何没预料到的异常（窗口构造失败、组件绑定抛出、
                // 规则评估里的意外空引用……）之前会让这个 while 循环连同它所在的
                // Task 一起永久退出——监听从此静默失效，而且这个 Task 是 fire-and-
                // forget 起的（QqNotificationProvider），异常要等宿主关闭时才会
                // 被重新观察到，届时诊断页早就应该显示"运行中"却一直显示"运行中"
                // 没有任何变化。记下来、按连续失败次数退避后继续下一轮，不让一次
                // 意外的异常杀死整条监听链路。
                consecutiveFailures++;
                _diagnostics.RecordError(ex, "监听循环");
                try
                {
                    await WaitForDelayOrSettingsChangeAsync(GetFailureDelay(consecutiveFailures), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    public async Task<QqNotificationPollResult> PollOnceAndDispatchAsync(
        bool retryPermission,
        Func<MessageEvent, CancellationToken, Task> dispatchAsync,
        CancellationToken cancellationToken)
    {
        await _pollDispatchGate.WaitAsync(cancellationToken);
        try
        {
            return await PollOnceAndDispatchCoreAsync(
                retryPermission,
                dispatchAsync,
                cancellationToken);
        }
        finally
        {
            _pollDispatchGate.Release();
        }
    }

    private async Task<QqNotificationPollResult> PollOnceAndDispatchCoreAsync(
        bool retryPermission,
        Func<MessageEvent, CancellationToken, Task> dispatchAsync,
        CancellationToken cancellationToken)
    {
        long generation;
        CancellationToken generationToken;
        lock (_stateLock)
        {
            if (!_settings.IsEnabled)
            {
                return DisabledResult();
            }

            generation = _generation;
            generationToken = _generationCancellation.Token;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationToken);
        QqNotificationPollResult result;
        try
        {
            result = await _poller.PollAsync(retryPermission, linkedCancellation.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            return ChangedGenerationResult();
        }

        if (!result.IsSuccess)
        {
            return result;
        }

        try
        {
            foreach (var message in result.AcceptedEvents)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                bool stillCurrent;
                lock (_stateLock)
                {
                    stillCurrent = generation == _generation
                                   && _settings.IsEnabled
                                   && !linkedCancellation.IsCancellationRequested;
                }
                if (!stillCurrent) return ChangedGenerationResult();

                // Update 会同步触发 PropertyChanged，走到 Avalonia 绑定/组件代码——
                // 不能在持有 _stateLock 时调用外部代码，那是一个现成的死锁触发点
                // （尤其如果某个订阅者反过来同步等 UI 线程，而 UI 线程正想拿这把锁）。
                _latestMessage.Update(message);

                if (!IsGenerationCurrent(generation, generationToken))
                {
                    return ChangedGenerationResult();
                }

                linkedCancellation.Token.ThrowIfCancellationRequested();
                _automationEventHub.Publish(message);

                if (!IsGenerationCurrent(generation, generationToken))
                {
                    return ChangedGenerationResult();
                }

                linkedCancellation.Token.ThrowIfCancellationRequested();
                await dispatchAsync(message, linkedCancellation.Token);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && generationToken.IsCancellationRequested)
        {
            return ChangedGenerationResult();
        }

        return result;
    }

    public async Task ReplayLatestAsync(
        Func<MessageEvent, Action<Action>, CancellationToken, Task> dispatchAsync,
        CancellationToken cancellationToken)
    {
        long generation;
        CancellationToken generationToken;
        lock (_stateLock)
        {
            if (!_settings.IsEnabled) return;

            generation = _generation;
            generationToken = _generationCancellation.Token;
        }

        if (!_latestMessage.TryCreateReplayEvent(out var message)) return;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            generationToken);

        bool CanShow()
        {
            if (linkedCancellation.IsCancellationRequested) return false;

            lock (_stateLock)
            {
                return generation == _generation
                       && _settings.IsEnabled
                       && !linkedCancellation.IsCancellationRequested;
            }
        }

        void ShowIfCurrent(Action show)
        {
            if (linkedCancellation.IsCancellationRequested) return;

            lock (_stateLock)
            {
                if (generation == _generation
                    && _settings.IsEnabled
                    && !linkedCancellation.IsCancellationRequested)
                {
                    show();
                }
            }
        }

        if (!CanShow()) return;

        try
        {
            await dispatchAsync(message, ShowIfCurrent, linkedCancellation.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested
                  && generationToken.IsCancellationRequested)
        {
        }
    }

    public void ResetDetectionState()
    {
        _poller.ResetDetectionState();
    }

    private bool IsGenerationCurrent(long generation, CancellationToken generationToken)
    {
        lock (_stateLock)
        {
            return generation == _generation
                   && _settings.IsEnabled
                   && !generationToken.IsCancellationRequested;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QqListenerSettings.IsEnabled)) return;

        CancellationTokenSource previousGenerationCancellation;
        TaskCompletionSource<bool> previousSignal;
        bool enabled;
        lock (_stateLock)
        {
            _generation++;
            previousGenerationCancellation = _generationCancellation;
            _generationCancellation = new CancellationTokenSource();
            previousSignal = Interlocked.Exchange(ref _settingsChanged, CreateSignal());
            enabled = _settings.IsEnabled;
            _diagnostics.MarkEnabledChanged(enabled);
        }

        // ResetDetectionState/Clear 都可能触发外部回调（Clear 会同步发 PropertyChanged，
        // 走到 Avalonia 绑定/组件代码）——不能在持有 _stateLock 时调用，原因跟上面
        // PollOnceAndDispatchCoreAsync 里 Update 的那处一致。
        if (enabled)
            _poller.ResetDetectionState();
        else
            _latestMessage.Clear();

        previousGenerationCancellation.Cancel();
        previousGenerationCancellation.Dispose();
        previousSignal.TrySetResult(true);
    }

    private async Task WaitForDelayOrSettingsChangeAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        var signal = Volatile.Read(ref _settingsChanged).Task;
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = _delay.DelayAsync(delay, delayCancellation.Token);
        var completed = await Task.WhenAny(delayTask, signal);
        if (completed == delayTask)
        {
            await delayTask;
            return;
        }

        await delayCancellation.CancelAsync();
        try
        {
            await delayTask;
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 消息到达时会被 <see cref="IQqNotificationPoller.WaitForMessagesAsync"/> 立刻叫醒，
    /// 这个间隔只是兜底心跳：数据源的 PollAsync 里还顺带做别的周期性工作（原生捕获
    /// 要按当前活跃的 QQ 进程起停捕获线程、要在暂停/恢复之间搬运积压队列），一直挂着
    /// 不醒这些就停摆了。所以它不是"扫描间隔"，用户也不需要调。
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 等下一条消息：谁先来就跟谁走——数据源的到达信号、兜底心跳、或者监听开关被改动。
    /// </summary>
    private async Task WaitForNextMessageAsync(CancellationToken cancellationToken)
    {
        var signal = Volatile.Read(ref _settingsChanged).Task;
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = _delay.DelayAsync(HeartbeatInterval, waitCancellation.Token);
        var arrival = _poller.WaitForMessagesAsync(waitCancellation.Token);

        var completed = await Task.WhenAny(heartbeat, signal, arrival);
        await waitCancellation.CancelAsync();

        // 心跳自己走完的话要把它 await 出来，好让外层 cancellationToken 的取消
        // 照常冒泡；另外两条路走完时，剩下那些还挂着的等待会被上面的 Cancel 掀掉，
        // 抛出来的取消异常必须有人接住，否则会变成 UnobservedTaskException。
        if (completed == heartbeat) await heartbeat;
        else await ObserveAsync(heartbeat);
        await ObserveAsync(arrival);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static TimeSpan GetFailureDelay(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(30, 1 << exponent));
    }

    private static QqNotificationPollResult DisabledResult() => new(
        WindowsNotificationReadStatus.Success,
        [],
        "监听已关闭");

    private static QqNotificationPollResult ChangedGenerationResult() => new(
        WindowsNotificationReadStatus.Success,
        [],
        "监听状态已改变，已丢弃在途结果");

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
