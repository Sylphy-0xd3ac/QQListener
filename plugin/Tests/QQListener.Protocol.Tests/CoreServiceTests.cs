using QQListener.Core;
using QQListener.Native;

namespace QQListener.Protocol.Tests;

/// <summary>对应 Python 侧 test_core_service.py + test_core_runtime.py 里跟 CoreService 有关的部分。</summary>
public class CoreServiceTests : IDisposable
{
    public CoreServiceTests() => CoreRuntime.ResetToDefault();
    public void Dispose() => CoreRuntime.ResetToDefault();

    private static MapHandle Handle(int pid) => new((ulong)pid * 0x1000, (ulong)pid * 0x1000 + 0x100, 0, 0x3000);

    [Fact]
    public async Task HostStartupInjectsWithoutClickingLoad()
    {
        CoreController.ResetToDefault();
        var injected = new TaskCompletionSource();
        using var service = new CoreService(enumeratePids: () => [11], findBinary: () => "hook.dll",
            injectFunc: (pid, _) => { injected.TrySetResult(); return Handle(pid); },
            unloadFunc: (_, _) => { }, hookActive: _ => false,
            recheckMainProcess: _ => true, supported: true);
        var lifecycle = new QQListener.Services.NativeCoreLifecycleService(service);
        try
        {
            await lifecycle.StartAsync(CancellationToken.None);
            await injected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { await lifecycle.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public void PauseStillAttachesNewQqProcessesSoTheyCanAccumulateMessages()
    {
        using var service = new CoreService(enumeratePids: () => [11], findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid), unloadFunc: (_, _) => { },
            hookActive: _ => false, recheckMainProcess: _ => true, supported: true);
        service.Reconcile(CoreState.Paused);
        Assert.Contains(11, service.ActivePids);
    }

    [Fact]
    public void ReconcileRunningInjectsEachPidOnce()
    {
        var injected = new List<(int Pid, string Path)>();
        var service = new CoreService(
            enumeratePids: () => [11, 22],
            findBinary: () => "C:/native/hook.dll",
            injectFunc: (pid, path) => { injected.Add((pid, path)); return Handle(pid); },
            unloadFunc: (_, _) => { },
            hookActive: _ => false,
            supported: true);

        service.Reconcile(CoreState.Running);
        service.Reconcile(CoreState.Running);

        Assert.Equal([(11, "C:/native/hook.dll"), (22, "C:/native/hook.dll")], injected);
        Assert.Equal(new Dictionary<int, MapHandle> { [11] = Handle(11), [22] = Handle(22) }, service.Handles);
    }

    [Fact]
    public void ReconcilePausedPreservesLoadedHandles()
    {
        var unloaded = new List<(int, MapHandle)>();
        var service = new CoreService(
            enumeratePids: () => [11],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (pid, handle) => unloaded.Add((pid, handle)),
            hookActive: _ => false,
            supported: true);
        service.Reconcile(CoreState.Running);

        service.Reconcile(CoreState.Paused);

        Assert.Equal(new Dictionary<int, MapHandle> { [11] = Handle(11) }, service.Handles);
        Assert.Empty(unloaded);
    }

    [Fact]
    public void ReconcileDetachedUnloadsOwnedHandlesOnly()
    {
        var unloaded = new List<(int, MapHandle)>();
        var service = new CoreService(
            enumeratePids: () => [11, 22],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (pid, handle) => unloaded.Add((pid, handle)),
            hookActive: pid => pid == 22,
            supported: true);
        service.Reconcile(CoreState.Running);

        service.Reconcile(CoreState.Detached);

        Assert.Equal([(11, Handle(11))], unloaded);
        Assert.Empty(service.Handles);
    }

    [Fact]
    public void ReconcileDropsHandleAfterProcessExitsWithoutUnload()
    {
        var pids = new List<int> { 11 };
        var unloaded = new List<(int, MapHandle)>();
        var service = new CoreService(
            enumeratePids: () => pids,
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (pid, handle) => unloaded.Add((pid, handle)),
            hookActive: _ => false,
            supported: true);
        service.Reconcile(CoreState.Running);
        pids.Clear();

        service.Reconcile(CoreState.Running);

        Assert.Empty(service.Handles);
        Assert.Empty(unloaded);
    }

    [Fact]
    public void EnumerationFailurePreservesPreviousSnapshot()
    {
        var shouldFail = false;
        IReadOnlyList<int> EnumeratePids()
        {
            if (shouldFail) throw new IOException("snapshot failed");
            return [11];
        }

        var service = new CoreService(
            enumeratePids: EnumeratePids,
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (_, _) => { },
            hookActive: _ => false,
            supported: true);
        service.Reconcile(CoreState.Running);
        shouldFail = true;

        Assert.Throws<IOException>(() => service.Reconcile(CoreState.Running));
        Assert.Equal(new Dictionary<int, MapHandle> { [11] = Handle(11) }, service.Handles);
    }

    [Fact]
    public void ExternalHookIsReusedAndNeverUnloaded()
    {
        var injected = new List<(int, string)>();
        var unloaded = new List<(int, MapHandle)>();
        var service = new CoreService(
            enumeratePids: () => [33],
            findBinary: () => "hook.dll",
            injectFunc: (pid, path) => { injected.Add((pid, path)); return Handle(pid); },
            unloadFunc: (pid, handle) => unloaded.Add((pid, handle)),
            hookActive: _ => true,
            supported: true);

        service.Reconcile(CoreState.Running);

        var waiting = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Waiting, waiting.State);
        Assert.Equal(33, waiting.Pid);
        service.Reconcile(CoreState.Detached);

        Assert.Empty(injected);
        Assert.Empty(unloaded);
    }

    [Fact]
    public void RunningWithoutBinaryRaisesClearError()
    {
        var service = new CoreService(
            enumeratePids: () => [11],
            findBinary: () => null,
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (_, _) => { },
            hookActive: _ => false,
            supported: true);

        var error = Assert.Throws<FileNotFoundException>(() => service.Reconcile(CoreState.Running));
        Assert.Contains("snowluma-win32-x64.dll", error.Message);
    }

    [Fact]
    public void UnsupportedPlatformIsANoop()
    {
        var service = new CoreService(
            enumeratePids: () => throw new InvalidOperationException("should not enumerate"),
            findBinary: () => throw new InvalidOperationException("should not locate"),
            supported: false);

        service.Reconcile(CoreState.Running);

        Assert.Empty(service.Handles);
    }

    [Fact]
    public void ReportsNoQqInsteadOfRunning()
    {
        var service = new CoreService(enumeratePids: () => [], supported: true);

        service.Reconcile(CoreState.Running);

        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.NoQq, snapshot.State);
        Assert.Contains("QQ", snapshot.Detail);
    }

    [Fact]
    public void ReportsWaitingAfterImageMapping()
    {
        var handle = new MapHandle(0x1000, 0x1100, 0, 0x3000);
        var service = new CoreService(
            enumeratePids: () => [22],
            findBinary: () => "hook.dll",
            injectFunc: (_, _) => handle,
            hookActive: _ => false,
            supported: true);

        service.Reconcile(CoreState.Running);

        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Waiting, snapshot.State);
        Assert.Equal(22, snapshot.Pid);
    }

    [Fact]
    public void SubsequentReconcileKeepsTheMappedProcessPidWhileWaitingForPipe()
    {
        var service = new CoreService(
            enumeratePids: () => [22],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            hookActive: _ => false,
            supported: true);

        service.Reconcile(CoreState.Running);
        service.Reconcile(CoreState.Running);

        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Waiting, snapshot.State);
        Assert.Equal(22, snapshot.Pid);
    }

    [Fact]
    public void PipeConnectionEstablishedDuringScanIsNotOverwrittenByIdleWaitingState()
    {
        var service = new CoreService(
            enumeratePids: () => [44],
            hookActive: pid =>
            {
                CoreRuntime.Set(CoreRuntimeState.Connected, "已连接", pid);
                return true;
            },
            supported: true);

        service.Reconcile(CoreState.Running);

        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Connected, snapshot.State);
        Assert.Equal(44, snapshot.Pid);
    }

    [Fact]
    public void PipeConnectionEstablishedAsInjectionReturnsIsNotOverwrittenByWaitingState()
    {
        var service = new CoreService(
            enumeratePids: () => [55],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) =>
            {
                CoreRuntime.Set(CoreRuntimeState.Connected, "已连接", pid);
                return Handle(pid);
            },
            hookActive: _ => false,
            recheckMainProcess: _ => true,
            supported: true);

        service.Reconcile(CoreState.Running);

        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Connected, snapshot.State);
        Assert.Equal(55, snapshot.Pid);
    }

    [Fact]
    public void StalePidFailingTheLastMomentRecheckIsSkippedNotInjected()
    {
        // 模拟 TOCTOU：enumeratePids 快照里还在，但紧邻 OpenProcess 之前的复核
        // 发现它已经不再是 QQ 主进程了（比如被系统回收给了别的进程）。
        var injected = new List<int>();
        var service = new CoreService(
            enumeratePids: () => [11],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => { injected.Add(pid); return Handle(pid); },
            hookActive: _ => false,
            recheckMainProcess: _ => false,
            supported: true);

        service.Reconcile(CoreState.Running);

        Assert.Empty(injected);
        Assert.Empty(service.Handles);
    }

    [Fact]
    public void RecheckRejectionForOnePidDoesNotBlockOthersInTheSameBatch()
    {
        var service = new CoreService(
            enumeratePids: () => [11, 22],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            hookActive: _ => false,
            recheckMainProcess: pid => pid != 11,
            supported: true);

        service.Reconcile(CoreState.Running);

        Assert.Equal(new Dictionary<int, MapHandle> { [22] = Handle(22) }, service.Handles);
    }

    [Fact]
    public void InjectionFailureForOnePidDoesNotBlockOthersInTheSameBatch()
    {
        // candidates 按 pid 升序处理；让失败的那个排在后面，这样它报出的 Error
        // 是这一轮 Reconcile 里最后一次 CoreRuntime.Set，不会被前一个成功的
        // pid 顺带覆盖掉——CoreRuntime 只有一份全局快照，这是既有设计，不是本
        // 次要修的东西，这里只关心"一个失败不拖累另一个"这件事本身。
        var service = new CoreService(
            enumeratePids: () => [11, 22],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => pid == 22
                ? throw new InvalidOperationException("boom")
                : Handle(pid),
            hookActive: _ => false,
            recheckMainProcess: _ => true,
            supported: true);

        service.Reconcile(CoreState.Running);

        Assert.Equal(new Dictionary<int, MapHandle> { [11] = Handle(11) }, service.Handles);
        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Error, snapshot.State);
        Assert.Contains("22", snapshot.Detail);
    }

    [Fact]
    public void StaleHandleFailingRecheckIsDroppedWithoutCallingUnload()
    {
        var unloaded = new List<int>();
        var stillMainProcess = true;
        var service = new CoreService(
            enumeratePids: () => [11],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (pid, _) => unloaded.Add(pid),
            hookActive: _ => false,
            recheckMainProcess: _ => stillMainProcess,
            supported: true);
        service.Reconcile(CoreState.Running);
        Assert.NotEmpty(service.Handles);

        // 下一轮 Reconcile 之前，pid 11 被系统回收给了别的进程——复核失败。
        stillMainProcess = false;
        service.Reconcile(CoreState.Detached);

        Assert.Empty(unloaded);
        Assert.Empty(service.Handles);
    }

    [Fact]
    public void StopTimeoutReportsErrorAndBlocksReentrantStartUntilResolved()
    {
        // 用一个卡住不返回的 enumeratePids 模拟 Reconcile 卡在慢速远程调用里的场景：
        // 之前 Stop() 超时会直接静默 return，Start() 的重入检查也完全不吭声，
        // 核心从此没有任何提示地失效。这个测试锁定修复后的行为：两边都要报 Error。
        //
        // 用信号代替 Thread.Sleep 确认后台线程真的已经卡进 enumeratePids——sleep
        // 赌的是"应该来得及"，一旦线程启动被系统调度推迟，Run() 的第一次循环检查
        // 会看到 _stopping 已经是 true 而提前退出，Stop() 的 Join 就会意外成功，
        // 走到 unloadOwned 的 Reconcile(Detached) 分支，在测试线程上第二次调用这个
        // 没有超时的阻塞 enumeratePids，永久挂死整个测试进程而不是报错。
        // block.Wait 本身也给了 10 秒上限：万一 stopTimeout 的修复出现回归，最坏
        // 结果是这条用例超时失败，而不是真的挂死。
        // stopTimeout 缩到 200ms：这个值本身不是要验证的东西，没必要为了触发一次
        // "等待超时"就让整条用例耗掉几秒挂钟时间。
        var entered = new ManualResetEventSlim(false);
        var block = new ManualResetEventSlim(false);
        var service = new CoreService(
            enumeratePids: () =>
            {
                entered.Set();
                block.Wait(TimeSpan.FromSeconds(10));
                return [];
            },
            stopTimeout: TimeSpan.FromMilliseconds(200),
            supported: true);
        service.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "后台线程没有进入 enumeratePids");

        service.Stop();
        Assert.Equal(CoreRuntimeState.Error, CoreRuntime.Get().State);

        service.Start();
        var afterReentrantStart = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Error, afterReentrantStart.State);
        Assert.Contains("上一次停止尚未完成", afterReentrantStart.Detail);

        // 收尾：放开卡住的线程，确认它真的跑完最后一次 Reconcile 才结束测试——
        // 不这样做的话，这个后台线程可能在下一个测试运行期间才悄悄改 CoreRuntime。
        block.Set();
        Assert.True(
            SpinWait.SpinUntil(() => CoreRuntime.Get().State != CoreRuntimeState.Error, TimeSpan.FromSeconds(5)),
            "后台线程没有在放开阻塞后完成收尾");
    }

    [Fact]
    public void UnloadFailureForOnePidDoesNotBlockOthersInTheSameBatch()
    {
        // 跟注入循环的隔离对称：一个 pid 卸载失败（远程调用超时、目标已经在崩溃
        // 边缘……）不该拖累同一批里其他要卸载的进程。
        var unloaded = new List<int>();
        var service = new CoreService(
            enumeratePids: () => [11, 22],
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) => Handle(pid),
            unloadFunc: (pid, _) =>
            {
                if (pid == 11) throw new InvalidOperationException("boom");
                unloaded.Add(pid);
            },
            hookActive: _ => false,
            recheckMainProcess: _ => true,
            supported: true);
        service.Reconcile(CoreState.Running);

        service.Reconcile(CoreState.Detached);

        Assert.Equal([22], unloaded);
        // pid 11 卸载失败：句柄留着等下一轮重试，不能凭空消失。
        Assert.Equal(new Dictionary<int, MapHandle> { [11] = Handle(11) }, service.Handles);
        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Error, snapshot.State);
        Assert.Contains("11", snapshot.Detail);
    }

    [Fact]
    public void RemoteCallTimeoutDuringInjectionStopsRetryingThatPidInsteadOfLeakingEveryTick()
    {
        // ManualMapInjector 对远程调用超时是故意保留目标内存不释放的（回收会导致
        // 确定性崩溃）——如果照旧让这个 pid 留在 candidates 里，下一轮 Reconcile
        // 会在同一个进程里再泄漏一份镜像。这个 pid 应该被记下来，不再重试。
        var attempts = 0;
        var service = new CoreService(
            enumeratePids: () => [11],
            findBinary: () => "hook.dll",
            injectFunc: (_, _) => { attempts++; throw new RemoteCallTimeoutException("boom"); },
            hookActive: _ => false,
            recheckMainProcess: _ => true,
            supported: true);

        service.Reconcile(CoreState.Running);
        service.Reconcile(CoreState.Running);

        Assert.Equal(1, attempts);
        Assert.Empty(service.Handles);
        var snapshot = CoreRuntime.Get();
        Assert.Equal(CoreRuntimeState.Error, snapshot.State);
        Assert.Contains("重启 QQ", snapshot.Detail);
    }

    [Fact]
    public void StuckPidRetriesAgainAfterTheProcessActuallyExits()
    {
        // "本次会话不再重试" 不该变成 "永远不再重试"——旧进程真的退出之后，
        // 这个记号得跟着清掉，不然一个偶发的远程调用超时会永久卡死这个 pid。
        var pids = new List<int> { 11 };
        var attempts = 0;
        var service = new CoreService(
            enumeratePids: () => pids,
            findBinary: () => "hook.dll",
            injectFunc: (pid, _) =>
            {
                attempts++;
                if (attempts == 1) throw new RemoteCallTimeoutException("boom");
                return Handle(pid);
            },
            hookActive: _ => false,
            recheckMainProcess: _ => true,
            supported: true);
        service.Reconcile(CoreState.Running);
        Assert.Empty(service.Handles);

        pids.Clear();
        service.Reconcile(CoreState.Running); // 进程退出：_stuckPids 里的记号被清掉
        pids.Add(11); // 系统把 pid 11 分配给了一个新的 QQ 主进程
        service.Reconcile(CoreState.Running);

        Assert.Equal(2, attempts);
        Assert.Equal(new Dictionary<int, MapHandle> { [11] = Handle(11) }, service.Handles);
    }
}
