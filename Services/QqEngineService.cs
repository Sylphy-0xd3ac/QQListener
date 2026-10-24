using Microsoft.Extensions.Hosting;
using QQListener.Core;
using QQListener.Models;
using QQListener.Native;
using QQListener.Oidb;
using QQListener.Pipes;

namespace QQListener.Services;

/// <summary>Serializes engine ownership while both engines share the same message delivery policy.</summary>
public sealed class QqEngineService(
    QqListenerSettings settings, MessageEventPipeline pipeline, ILessonStateProvider lesson,
    TimeProvider time, EngineCommandRouter commands, Func<CoreService>? createSnowCore = null,
    Func<IReadOnlyList<int>>? enumeratePids = null) : BackgroundService, IQqNotificationPoller
{
    private readonly EngineSwitchGuard _switch = new();
    private readonly object _gate = new();
    private Session? _current;
    private ListenerEngine? _selected;
    private DateTimeOffset _retryAfter;
    private int _retryRequested;
    private int _started;

    private sealed class Session : IDisposable
    {
        private sealed record Generation(Guid Id);
        private Generation _generation = new(Guid.NewGuid());
        public Guid Epoch
        {
            get => Volatile.Read(ref _generation).Id;
            set => Volatile.Write(ref _generation, new Generation(value));
        }
        public required ListenerEngine Kind;
        public required NativeQqMessageSource Source;
        public required NativeMessageEnrichment Enrichment;
        public required QqControlApi Api;
        public CoreService? Core;
        public PmhqHost? Host;
        public readonly CancellationTokenSource Lifetime = new();
        public readonly Dictionary<int, string> Accounts = [];
        public Task? ReceiveTask;
        public volatile bool RequiresRestart;
        public volatile bool Stopped;

        public void Dispose()
            => Stop(unloadOwned: false);

        public void Stop(bool unloadOwned)
        {
            Stopped = true;
            Lifetime.Cancel();
            Source.Dispose();
            Host?.Dispose();
            // Engine switching leaves hooks resident until QQ exits; it never unmaps live callbacks.
            Core?.Stop(unloadOwned);
            Core?.Dispose();
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken) =>
        StartOnce(cancellationToken);

    private Task StartOnce(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return Task.CompletedTask;
        settings.PropertyChanged += OnSettingsChanged;
        return base.StartAsync(cancellationToken);
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(QqListenerSettings.IsEnabled))
            Volatile.Read(ref _current)?.Source.ClearSessionState();
    }

    public void RequestRecheck() => Interlocked.Exchange(ref _retryRequested, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Reconcile(); }
                catch (Exception ex)
                {
                    CoreRuntime.Set(CoreRuntimeState.Error, ex is IOException ? ex.Message : "引擎启动失败，请检查配置后重新连接");
                    _retryAfter = time.GetUtcNow().AddSeconds(5);
                }
                await Task.Delay(500, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            settings.PropertyChanged -= OnSettingsChanged;
            StopCurrent(unloadOwnedSnow: true);
            CoreController.ResetToDefault();
            if (CoreRuntime.Get().State != CoreRuntimeState.Error) CoreRuntime.ResetToDefault();
        }
    }

    private void Reconcile()
    {
        if (Interlocked.Exchange(ref _retryRequested, 0) != 0) _retryAfter = default;
        var wanted = settings.Engine;
        if (_selected != wanted)
        {
            var livePids = EnumeratePids();
            if (_selected is not null) _switch.RequireRestart(livePids);
            else if (OperatingSystem.IsWindows())
            {
                if (HookPipeProbe.Exists("pmhq_sign")) _switch.RequireRestart(livePids);
                var bundle = new PmhqBundle(KernelLocator.ResolveNativeDirectory(settings.SettingsDirectory));
                foreach (var pid in livePids)
                {
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(pid);
                        if (bundle.HasResidentHook(pid, process.StartTime.ToUniversalTime().Ticks))
                            _switch.RequireRestart([pid]);
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException
                        or System.ComponentModel.Win32Exception) { }
                }
            }
            StopCurrent();
            _selected = wanted;
            _retryAfter = default;
        }
        var session = Volatile.Read(ref _current);
        if (wanted == ListenerEngine.PMHQ && CoreController.GetState() == CoreState.Detached)
        {
            if (session is not null)
            {
                _switch.RequireRestart(EnumeratePids());
                StopCurrent();
            }
            CoreRuntime.Set(CoreRuntimeState.Detached, "PMHQ 已停止连接；QQ 内的钩子需要重启 QQ 清理");
            return;
        }
        if (session?.RequiresRestart == true || session?.Host?.HasExited == true)
        {
            _switch.RequireRestart(EnumeratePids());
            StopCurrent();
            session = null;
        }
        if (session is not null) return;
        if (time.GetUtcNow() < _retryAfter) return;
        var pids = EnumeratePids();
        if (!_switch.CanStart(pids))
        {
            CoreRuntime.Set(CoreRuntimeState.Waiting, "引擎已更换，请退出并重启 QQ；旧钩子所在进程退出后会自动连接");
            return;
        }
        if (wanted == ListenerEngine.PMHQ && pids.Count != 1)
        {
            CoreRuntime.Set(CoreRuntimeState.Waiting, pids.Count == 0
                ? "PMHQ 实验引擎：请先启动并登录一个 QQ 主进程"
                : "PMHQ 实验引擎暂不支持多个 QQ 主进程");
            return;
        }
        session = CreateSession(wanted);
        lock (_gate) _current = session;
        commands.Bind(session.Epoch, session.Api);
        if (wanted == ListenerEngine.SnowLuma) session.Core!.Start();
        else
        {
            try
            {
                session.Host = new PmhqHost(new PmhqBundle(KernelLocator.ResolveNativeDirectory(settings.SettingsDirectory)));
                session.Host.Start(pids[0]);
                session.ReceiveTask = RunPmhqAsync(session);
            }
            catch { StopCurrent(); throw; }
        }
    }

    private Session CreateSession(ListenerEngine kind)
    {
        Session? session = null;
        var snow = QqControlApi.OverNamedPipes();
        var api = QqControlApi.OverRequests((pid, command, body) =>
        {
            if (session is null || session.Stopped || !commands.IsCurrent(session.Epoch))
                throw new IOException("引擎会话已结束");
            return kind == ListenerEngine.SnowLuma ? snow.SendRaw(pid, command, body)
                : session.Host?.Client?.Send(pid, command, body) ?? throw new IOException("PMHQ 尚未连接");
        });
        var enrichment = new NativeMessageEnrichment(api);
        CoreService? core = kind == ListenerEngine.SnowLuma
            ? createSnowCore?.Invoke() ?? new CoreService(findBinary: () => KernelLocator.FindHookBinary([KernelLocator.ResolveNativeDirectory(settings.SettingsDirectory)])) : null;
        var source = new NativeQqMessageSource(
            () => core is null ? [] : [.. core.ActivePids], name => new NamedPipeTransport(name),
            onCaptureConnected: pid => ReportSnow(CoreRuntimeState.Connected, "SnowLuma 接收管道已连接", pid),
            onCaptureDisconnected: pid => ReportSnow(CoreRuntimeState.Waiting, "SnowLuma 接收管道已断开，等待重连", pid),
            onCaptureError: (pid, _) => ReportSnow(CoreRuntimeState.Error, "SnowLuma 接收失败，等待重连", pid),
            processEvent: evt =>
            {
                if (session is null || session.Stopped || !commands.IsCurrent(session.Epoch)) return null;
                var result = pipeline.Process(evt with { EngineSession = session.Epoch });
                return result.Accepted ? result.Event : null;
            },
            resetState: pipeline.ClearState, getCoreState: CoreController.GetState, enrichment: enrichment,
            getPendingLimit: () => settings.PendingQueueLimit,
            isOnClass: () => settings.DisableDuringClass && lesson.IsOnClass,
            getAfterClassDelaySeconds: () => settings.DisableDuringClass ? settings.AfterClassDelaySeconds : 0,
            excludeSelfMessages: () => settings.ExcludeSelfMessages, timeProvider: time,
            isEnabled: () => settings.IsEnabled,
            onLoginChanged: (pid, login) =>
            {
                if (session is null || session.Stopped) return;
                var identity = login.LoggedIn ? login.Uin : "";
                lock (session.Accounts)
                {
                    if (session.Accounts.TryGetValue(pid, out var old) && old != identity)
                    {
                        enrichment.ForgetPid(pid);
                        session.Source.ClearSessionState();
                        session.Epoch = Guid.NewGuid();
                        commands.Bind(session.Epoch, api);
                    }
                    session.Accounts[pid] = identity;
                }
            });
        return session = new Session { Kind = kind, Source = source, Enrichment = enrichment, Api = api, Core = core };

        void ReportSnow(CoreRuntimeState state, string detail, int pid)
        {
            if (session is not null && !session.Stopped && ReferenceEquals(session, Volatile.Read(ref _current)))
                CoreRuntime.Set(state, detail, pid);
        }
    }

    private async Task RunPmhqAsync(Session session)
    {
        var client = session.Host!.Client!;
        var token = session.Lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                var receive = client.ReceiveAsync(session.Source.Accept, ReportHealth, attempt.Token);
                try
                {
                    while (!receive.IsCompleted)
                    {
                        ReportHealth(await client.GetHealthAsync(attempt.Token));
                        await Task.WhenAny(receive, Task.Delay(1000, attempt.Token));
                    }
                    await receive;
                }
                finally
                {
                    attempt.Cancel();
                    try { await receive; }
                    catch (Exception) when (attempt.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (PmhqAccountChangedException)
            {
                commands.Bind(Guid.Empty, null);
                session.Source.ClearSessionState();
                session.RequiresRestart = true;
                CoreRuntime.Set(CoreRuntimeState.Error, "PMHQ 账号或登录状态变化，请重启 QQ 后继续");
                return;
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested) return;
                CoreRuntime.Set(CoreRuntimeState.Waiting, "PMHQ 本机接收管道未就绪，正在重试；可在调试区重新检查");
            }
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }

        void ReportHealth(Protocol.PmhqHealth health)
        {
            if (session.Stopped) return;
            var initialized = session.Host!.NativeInitialized;
            var ready = health.LoggedIn && initialized && client.IsConnected;
            CoreRuntime.Set(ready ? CoreRuntimeState.Connected : CoreRuntimeState.Waiting,
                ready ? "PMHQ 本机引擎已连接，无需启动凭证"
                    : health.LoggedIn ? initialized ? "PMHQ 内核就绪，等待本机接收管道"
                        : "PMHQ 正在初始化 QQ 内核；暂不发送回复及凭证请求"
                    : "PMHQ 正在等待本机 QQ 登录及消息，无需启动凭证", health.Pid);
        }
    }

    private void StopCurrent(bool unloadOwnedSnow = false)
    {
        Session? old;
        lock (_gate) { old = _current; _current = null; }
        commands.Bind(Guid.Empty, null);
        old?.Stop(unloadOwnedSnow);
        pipeline.ClearState();
    }

    private IReadOnlyList<int> EnumeratePids() => enumeratePids?.Invoke() ??
        (OperatingSystem.IsWindows() ? QqProcessLocator.EnumerateQqPids(WindowsQqProcessSource.Enumerate) : []);

    public async Task<QqNotificationPollResult> PollAsync(bool retryPermission, CancellationToken cancellationToken)
    {
        await StartAsync(CancellationToken.None);
        var session = Volatile.Read(ref _current);
        if (session is null) return new(WindowsNotificationReadStatus.Success, [], CoreRuntime.Get().Detail);
        var result = await session.Source.PollAsync(retryPermission, cancellationToken);
        return ReferenceEquals(session, Volatile.Read(ref _current)) ? result with
        {
            AcceptedEvents = result.AcceptedEvents.Where(message => message.IsPendingDigest
                ? message.PendingMessages.All(item => commands.IsCurrent(item.EngineSession))
                : commands.IsCurrent(message.EngineSession)).ToArray()
        }
            : new(WindowsNotificationReadStatus.Success, [], "引擎已切换");
    }

    public void ResetDetectionState() => pipeline.ClearState();
    public Task WaitForMessagesAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _current)?.Source.WaitForMessagesAsync(cancellationToken) ?? Task.Delay(500, cancellationToken);
}
