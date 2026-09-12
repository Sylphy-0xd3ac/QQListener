using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQListener.Models;

namespace QQListener.Services;

[NotificationProviderInfo("a6f94b7a-d398-41d4-b3c7-208fb9d9ad7b", "QQListener", "\uE02B", "监听 QQ 通知并转发为 ClassIsland 提醒。")]
public class QqNotificationProvider : NotificationProviderBase, IHostedService
{
    private readonly QqListenerSettings _settings;
    private readonly QqListenerWorker _worker;
    private readonly IQqNotificationPoller _poller;
    private readonly ClassIslandMessageNotifier _notifier;
    private readonly QqListenerDiagnosticsState _diagnostics;
    private readonly ILogger<QqNotificationProvider> _logger;
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public QqNotificationProvider(
        QqListenerSettings settings,
        QqListenerWorker worker,
        IQqNotificationPoller poller,
        ClassIslandMessageNotifier notifier,
        QqListenerDiagnosticsState diagnostics,
        ILogger<QqNotificationProvider> logger)
    {
        _settings = settings;
        _worker = worker;
        _poller = poller;
        _notifier = notifier;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    public void ResetDetectionState()
    {
        _poller.ResetDetectionState();
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_listenerTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _listenerCts?.Dispose();
            _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listenerTask = _worker.RunAsync(ShowQqNotificationAsync, _listenerCts.Token);
        }

        _logger.LogInformation("QQListener 监听已启动（消息到达即提醒，去重冷却 {Cooldown}s）",
            _settings.CooldownSeconds);
        _diagnostics.MarkStarted(_settings.IsEnabled);
        return Task.CompletedTask;
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        Task? listenerTask;
        CancellationTokenSource? listenerCts;
        lock (_lifecycleLock)
        {
            listenerTask = _listenerTask;
            listenerCts = _listenerCts;
        }

        if (listenerTask is null || listenerCts is null) return;

        _logger.LogInformation("QQListener 监听正在停止...");
        await listenerCts.CancelAsync();
        try
        {
            await listenerTask.WaitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 之前清理块只在 WaitAsync 成功之后才执行——一旦它因为超时/取消抛出，
            // _listenerTask/_listenerCts 会原样留着指向一个已经放弃等待的旧任务，
            // 下一次 StartAsync 看到 "_listenerTask is {IsCompleted:false}" 就会
            // 直接放弃启动，监听从此永远不会重启，而且完全静默。这里仍然继续走到
            // 下面的清理块——跟 CoreService.Stop() 超时后刻意保留 _thread 不同：
            // 那边两个并发 reconcile 线程会直接竞争同一份原生句柄，这边旧的
            // RunAsync 循环即便真的还没退出，也只是在同一个 _worker 实例上跟
            // 新循环共用同一把 _pollDispatchGate/_stateLock，顶多短暂重复轮询，
            // 不是资源层面的竞态；放弃恢复能力换一个理论上很小的重复窗口不划算。
            _logger.LogWarning(ex, "QQListener 监听未能在预期时间内确认停止");
            _diagnostics.RecordError(ex, "停止监听");
        }

        lock (_lifecycleLock)
        {
            if (ReferenceEquals(_listenerTask, listenerTask))
            {
                _listenerTask = null;
                _listenerCts = null;
                listenerCts.Dispose();
            }
        }

        _logger.LogInformation("QQListener 监听已停止。");
        _diagnostics.MarkStopped();
    }

    public async Task PollOnceManuallyAsync()
    {
        // 之前用 CancellationToken.None：UI 上"立即轮询"按钮可能因为一次卡住的
        // 轮询（比如 control 管道那头没回应）而无限期阻塞在 _pollDispatchGate
        // 后面。给一个宽松的上限，超时了至少能返回而不是让按钮一直转圈。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _worker.PollOnceAndDispatchAsync(retryPermission: true, ShowQqNotificationAsync, timeout.Token);
    }

    public Task ReplayLatestAsync(CancellationToken cancellationToken = default)
    {
        return _worker.ReplayLatestAsync(
            (message, showIfCurrent, token) => _notifier.ShowAsync(
                message,
                request => showIfCurrent(() => ShowNotification(request)),
                token),
            cancellationToken);
    }

    /// <summary>
    /// 用一条假消息走完整的提醒链路，确认提醒 UI 的实际样子。
    ///
    /// 走的是和真实消息完全相同的路径（<see cref="ClassIslandMessageNotifier"/> →
    /// <c>ShowNotification</c>），所以看到的就是插件真正的提醒外观，不是另画的预览。
    /// </summary>
    public Task ShowTestMessageAsync(
        MessageImportance importance = MessageImportance.Important,
        CancellationToken cancellationToken = default,
        MessagePreviewKind kind = MessagePreviewKind.Text)
    {
        var message = MessagePreviewSamples.Create(kind, importance);

        _logger.LogInformation("显示测试提醒（重要性={Importance}）", importance);
        return _notifier.ShowAsync(message, ShowNotification, cancellationToken);
    }

    private Task ShowQqNotificationAsync(MessageEvent message, CancellationToken cancellationToken)
    {
        return _notifier.ShowAsync(message, ShowNotification, cancellationToken);
    }
}
