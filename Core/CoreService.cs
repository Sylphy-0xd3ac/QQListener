using System.Runtime.InteropServices;
using QQListener.Native;

namespace QQListener.Core;

/// <summary>
/// 核心装载生命周期服务。
///
/// 状态机（<see cref="CoreController"/>）仍保持纯状态；本服务在后台订阅状态变化，
/// 执行进程发现、幂等注入与卸载。所有外部依赖都能注入，支持离线确定性测试。
/// </summary>
public sealed class CoreService : IDisposable
{
    private const int ReconcileIntervalMs = 2000;

    private readonly Func<IReadOnlyList<int>> _enumeratePids;
    private readonly Func<string?> _findBinary;
    private readonly Func<int, string, MapHandle> _inject;
    private readonly Action<int, MapHandle> _unload;
    private readonly Func<int, bool> _hookActive;
    private readonly Func<int, bool> _recheckMainProcess;
    private readonly TimeSpan _stopTimeout;
    private readonly bool _supported;

    private readonly object _gate = new();
    private readonly object _lifecycleGate = new();
    private readonly Dictionary<int, MapHandle> _handles = [];
    private readonly HashSet<int> _externalPids = [];
    // 远程调用超时后 ManualMapInjector 会故意保留目标内存不释放（回收会导致确定性崩溃），
    // 这个 pid 本次会话就不再重试注入了，避免每 2 秒往同一个卡住的进程里再泄漏一份镜像。
    private readonly HashSet<int> _stuckPids = [];

    private readonly ManualResetEventSlim _wake = new(true);
    private volatile bool _stopping;
    private Thread? _thread;

    public CoreService(
        Func<IReadOnlyList<int>>? enumeratePids = null,
        Func<string?>? findBinary = null,
        Func<int, string, MapHandle>? injectFunc = null,
        Action<int, MapHandle>? unloadFunc = null,
        Func<int, bool>? hookActive = null,
        Func<int, bool>? recheckMainProcess = null,
        TimeSpan? stopTimeout = null,
        bool? supported = null)
    {
        _enumeratePids = enumeratePids ?? DefaultEnumeratePids;
        _findBinary = findBinary ?? (() => KernelLocator.FindHookBinary());
        _inject = injectFunc ?? DefaultInject;
        _unload = unloadFunc ?? DefaultUnload;
        _hookActive = hookActive ?? HookPipeAvailable;
        _recheckMainProcess = recheckMainProcess ?? DefaultRecheckMainProcess;
        _stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(5);
        _supported = supported ?? OperatingSystem.IsWindows();
    }

    /// <summary>释放等待句柄。只在确认后台线程已经不在跑（<see cref="Stop"/> 干净停止过、
    /// 从没启动过，或者 <see cref="Start"/> 从未成功）时才真正释放——万一 <see cref="Stop"/>
    /// 曾经超时收尾、线程可能还在用 <c>_wake</c>，宁可留着不释放，也不要在它脚下抽走。</summary>
    public void Dispose()
    {
        if (_thread is { IsAlive: true }) return;
        _wake.Dispose();
    }

    public IReadOnlyDictionary<int, MapHandle> Handles
    {
        get { lock (_gate) return new Dictionary<int, MapHandle>(_handles); }
    }

    /// <summary>当前"钩子在跑、可以连接接收管道"的全部 pid——自己注入的加上复用的外部钩子。
    /// 供 <c>NativeQqMessageSource</c> 决定该往哪些进程开捕获线程，本类不管那一层。</summary>
    public IReadOnlyCollection<int> ActivePids
    {
        get { lock (_gate) return new HashSet<int>([.. _handles.Keys, .. _externalPids]); }
    }

    public void Start()
    {
        lock (_lifecycleGate) StartCore();
    }

    private void StartCore()
    {
        if (!_supported)
        {
            CoreRuntime.Set(CoreRuntimeState.Unsupported, "当前平台不支持核心注入");
            return;
        }
        if (_thread is { IsAlive: true })
        {
            // _stopping 为真说明上一次 Stop() 曾经 5 秒超时收尾、线程还没死透——
            // 不能在它之上再叠一个新线程（两个 reconcile 线程会并发踩同一个
            // _handles），但也不能像以前那样完全静默地什么都不做：那样旧线程
            // 真正退出之后，永远不会再有人重新启动它，核心从此没有任何提示地失效。
            if (_stopping)
                CoreRuntime.Set(CoreRuntimeState.Error, "上一次停止尚未完成，本次启动请求已跳过，请稍后重试");
            return;
        }

        _stopping = false;
        _wake.Set();
        CoreController.AddListener(OnStateChanged);
        _thread = new Thread(Run) { Name = "QQListener-CoreService", IsBackground = true };
        _thread.Start();
    }

    /// <summary>停止后台线程；<paramref name="unloadOwned"/> 决定退出时是否把自己注入的核心卸载掉。</summary>
    public void Stop(bool unloadOwned = true)
    {
        CoreController.RemoveListener(OnStateChanged);
        _stopping = true;
        _wake.Set();

        var thread = _thread;
        if (thread is { IsAlive: true } && !thread.Join(_stopTimeout))
        {
            // 线程仍然可能卡在一次耗时的远程调用里（WindowsProcess.RemoteCall 最长
            // 会等 30 秒）。不清空 _thread：Start() 的重入检查靠它判断"是不是已经
            // 有一个线程在跑"，清空了反而会让人误以为可以放心再开一个。但也不能
            // 完全沉默——之前这里直接 return，核心从此静默失效，没有任何地方
            // 报出来过。跳过的 unloadOwned 卸载会让下次启动把这个 pid 当成外部钩子
            // （见 Reconcile 里 _hookActive 的判定），不会再主动尝试卸载，这里把这个
            // 后果也写进详情，免得用户以为"卸载"按钮下次还会正常工作。
            CoreRuntime.Set(CoreRuntimeState.Error,
                $"核心生命周期线程未能在 {_stopTimeout.TotalSeconds:0.#} 秒内停止，可能仍在执行较慢的远程调用；" +
                "本次不会卸载残留的核心，下次启动会把它当作外部钩子接管，如需彻底清理请重启 QQ");
            return;
        }

        _thread = null;
        if (!unloadOwned) return;
        try { Reconcile(CoreState.Detached); }
        catch { /* 退出时卸载失败不阻塞关闭 */ }
    }

    private void OnStateChanged(CoreState _) => _wake.Set();

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                Reconcile();
            }
            catch (Exception exc)
            {
                CoreRuntime.Set(CoreRuntimeState.Error, $"{exc.GetType().Name}: {exc.Message}");
            }
            _wake.Wait(ReconcileIntervalMs);
            _wake.Reset();
        }
    }

    /// <summary>把目标状态与当前 QQ 进程同步；公开以便离线确定性测试。</summary>
    public void Reconcile(CoreState? state = null)
    {
        if (!_supported) return;
        var targetState = state ?? CoreController.GetState();

        // 枚举异常直接向上传递，且在成功拿到新快照前不改任何现有句柄。
        var currentPids = new HashSet<int>(_enumeratePids());
        lock (_gate)
        {
            foreach (var pid in _handles.Keys.Where(p => !currentPids.Contains(p)).ToList())
                _handles.Remove(pid);
            _externalPids.IntersectWith(currentPids);
            _stuckPids.IntersectWith(currentPids); // 进程真的退出了，"不再重试"的记号也该跟着清掉
        }

        if (targetState == CoreState.Detached)
        {
            UnloadAll(currentPids);
            lock (_gate) { _externalPids.Clear(); _stuckPids.Clear(); }
            // UnloadAll 对卸载失败的 pid 已经单独报过 Error 并把句柄留着等下一轮重试；
            // 只有真的一个不剩才能说"核心已卸载"，否则会把刚报的 Error 立刻覆盖掉，
            // 等于告诉用户一切正常，而实际上还有进程没卸干净。
            bool fullyUnloaded;
            lock (_gate) fullyUnloaded = _handles.Count == 0;
            if (fullyUnloaded)
                CoreRuntime.Set(CoreRuntimeState.Detached, "核心已卸载");
            return;
        }
        // Pause suppresses delivery, not capture. New/restarted QQ processes still need a hook.

        if (currentPids.Count == 0)
        {
            CoreRuntime.Set(CoreRuntimeState.NoQq, "未找到正在运行的 QQ 主进程");
            return;
        }

        List<int> candidates;
        lock (_gate)
            candidates = currentPids
                .Where(pid => !_handles.ContainsKey(pid) && !_externalPids.Contains(pid)
                    && !_stuckPids.Contains(pid))
                .Order()
                .ToList();

        if (candidates.Count == 0)
        {
            ReportIdleState(currentPids, "核心已映射，等待接收管道", targetState);
            return;
        }

        var needsInjection = new List<int>();
        foreach (var pid in candidates)
        {
            if (_hookActive(pid))
                lock (_gate) _externalPids.Add(pid);
            else
                needsInjection.Add(pid);
        }
        if (needsInjection.Count == 0)
        {
            ReportIdleState(currentPids, "已发现核心，等待接收管道", targetState);
            return;
        }

        var dllPath = _findBinary() ?? throw new FileNotFoundException(KernelLocator.MissingBinaryHelp());
        foreach (var pid in needsInjection)
        {
            // enumeratePids 到这里之间已经过了一轮 hookActive 探测，足够旧进程退出、
            // pid 被系统回收给别的进程。紧邻 OpenProcess 之前再核实一次，把 TOCTOU
            // 窗口收到最窄——ManualMapInjector 内部把读盘解析 PE 的结果缓存住了，
            // 这里到真正 OpenProcess 之间不会再夹着那部分耗时。
            if (!_recheckMainProcess(pid)) continue;

            try
            {
                var handle = _inject(pid, dllPath);
                lock (_gate) _handles[pid] = handle;
                // 注入返回到这里之间，NativeQqMessageSource 可能已经从 ActivePids
                // 看到新句柄、连上 recv 管道并把状态推进到 Connected。必须在写
                // Waiting 前读取最新快照，否则这次较晚的写入会把真实连接永久盖掉。
                var latestRuntime = CoreRuntime.Get();
                var hasLiveConnection = latestRuntime.State == CoreRuntimeState.Connected
                                        && currentPids.Contains(latestRuntime.Pid);
                if (!hasLiveConnection)
                    CoreRuntime.Set(CoreRuntimeState.Waiting, "核心已映射，等待接收管道", pid);
            }
            catch (RemoteCallTimeoutException exc)
            {
                // 目标线程仍可能停在 TLS/DllMain 里，ManualMapInjector 因此故意保留了
                // 已分配的内存不释放——如果就这样让下一轮 Reconcile 照常重试，会每隔
                // ReconcileIntervalMs 在同一个进程里再泄漏一份镜像。把这个 pid 记下来，
                // 本次会话内不再对它做任何操作，交给用户重启 QQ 来恢复。
                lock (_gate) _stuckPids.Add(pid);
                CoreRuntime.Set(CoreRuntimeState.Error,
                    $"注入超时（pid={pid}）：目标进程可能已残留映像，需要重启 QQ 才能恢复：{exc.Message}", pid);
            }
            catch (Exception exc)
            {
                // 一个实例注入失败（架构不匹配、目标刚好在这一刻退出……）不该拖累
                // 同一批里的其他候选——继续尝试剩下的 pid，只把这一个的失败报出来；
                // 这个 pid 仍然不在 _handles 里，下一轮 Reconcile 会自然重试。
                CoreRuntime.Set(CoreRuntimeState.Error, $"注入失败（pid={pid}）：{exc.Message}", pid);
            }
        }
    }

    /// <summary>这一轮没有活要干时该报什么状态——通常是"等待接收管道"，但如果有 pid
    /// 因为远程调用超时被跳过（<see cref="_stuckPids"/>），不能让这句"一切正常"的
    /// Waiting 把刚报出来的 Error 悄悄盖掉：那样用户只会在出事的那一瞬间看到一次
    /// 提示，下一轮（最多 2 秒后）就又变回岁月静好，等于没提示过。</summary>
    private void ReportIdleState(IReadOnlySet<int> currentPids, string waitingDetail, CoreState targetState)
    {
        int stuckCount;
        lock (_gate) stuckCount = _stuckPids.Count;
        if (stuckCount > 0)
        {
            CoreRuntime.Set(CoreRuntimeState.Error,
                $"有 {stuckCount} 个 QQ 进程注入超时后被跳过，需要重启 QQ 才能恢复");
            return;
        }
        // RecvCapture 可能在这一轮扫描期间刚把状态推进到 Connected；在这里重新读取，
        // 避免用 Reconcile 开头取得的旧快照把刚建立的连接覆盖回 Waiting。
        var runtime = CoreRuntime.Get();
        if (targetState == CoreState.Running && runtime.State == CoreRuntimeState.Connected && currentPids.Contains(runtime.Pid))
            return;

        int[] activePids;
        lock (_gate)
            activePids = _handles.Keys
                .Concat(_externalPids)
                .Where(currentPids.Contains)
                .Distinct()
                .Order()
                .ToArray();

        // 下一轮 Reconcile 时所有已映射/复用的进程都会从 candidates 中排除。旧代码
        // 在这里调用 CoreRuntime.Set(state, detail) 时没带 pid，默认参数 0 会把上一轮
        // 写入的真实 PID 抹掉。优先保持仍然活跃的当前 PID，多开时再取最小 PID。
        var pid = runtime.Pid > 0 && activePids.Contains(runtime.Pid)
            ? runtime.Pid
            : activePids.FirstOrDefault();
        CoreRuntime.Set(targetState == CoreState.Paused ? CoreRuntimeState.Paused : CoreRuntimeState.Waiting,
            targetState == CoreState.Paused ? "监听已暂停，消息正在积压" : waitingDetail, pid);
    }

    private void UnloadAll(HashSet<int> currentPids)
    {
        List<KeyValuePair<int, MapHandle>> owned;
        lock (_gate) owned = [.. _handles];

        foreach (var (pid, handle) in owned)
        {
            if (!currentPids.Contains(pid))
            {
                lock (_gate) _handles.Remove(pid);
                continue;
            }
            if (!_recheckMainProcess(pid))
            {
                // pid 仍然出现在这一轮的快照里，但紧邻卸载前的复核失败——多半是
                // 旧的 QQ 进程刚退出，系统把同一个 pid 立刻分给了别的进程。旧的
                // 映射地址对这个新进程而言毫无意义，继续 RemoteCall 只会让它
                // 平白崩溃；直接丢弃这个陈旧句柄，不去调用 _unload。
                lock (_gate) _handles.Remove(pid);
                continue;
            }
            try
            {
                _unload(pid, handle);
                lock (_gate)
                {
                    if (_handles.TryGetValue(pid, out var current) && current.Equals(handle))
                        _handles.Remove(pid);
                }
            }
            catch (Exception exc)
            {
                // 一个 pid 卸载失败（远程调用超时、目标已经在崩溃边缘……）不该拖累
                // 同一批里其他要卸载的进程——继续处理剩下的，只把这一个的失败报出来。
                // 保留在 _handles 里，下一轮 Reconcile 会自然重试卸载。
                CoreRuntime.Set(CoreRuntimeState.Error, $"卸载失败（pid={pid}）：{exc.Message}", pid);
            }
        }
    }

    private static IReadOnlyList<int> DefaultEnumeratePids()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return QqProcessLocator.EnumerateQqPids(WindowsQqProcessSource.Enumerate);
    }

    private static MapHandle DefaultInject(int pid, string dllPath)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("手动映射装载器仅支持 Windows x64");
        return ManualMapInjector.Inject(pid, dllPath);
    }

    private static void DefaultUnload(int pid, MapHandle handle)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("手动映射装载器仅支持 Windows x64");
        ManualMapInjector.Unload(pid, handle);
    }

    // 跟 DefaultEnumeratePids 同样的套路：非 Windows 上直接放行（这条分支反正不会被
    // _supported 真正调用到，只是防止在测试里意外触碰真实 Win32 P/Invoke）。
    private static bool DefaultRecheckMainProcess(int pid) =>
        !OperatingSystem.IsWindows() || WindowsQqProcessSource.IsStillMainQqProcess(pid);

    /// <summary>只探测 SnowLuma control 管道是否存在，不建立持久连接。</summary>
    public static bool HookPipeAvailable(int pid)
    {
        if (!OperatingSystem.IsWindows()) return false;

        if (NativeMethods.WaitNamedPipeW($@"\\.\pipe\mojo.{pid}.control", 0)) return true;
        // ERROR_SEM_TIMEOUT / ERROR_PIPE_BUSY 都表示管道存在，只是当前没有空闲实例。
        return Marshal.GetLastWin32Error() is 121 or 231;
    }
}
