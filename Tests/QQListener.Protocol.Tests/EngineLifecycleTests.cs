using Microsoft.Extensions.Logging.Abstractions;
using QQListener.Core;
using QQListener.Models;
using QQListener.Native;
using QQListener.Services;
using QQListener.Protocol;
using System.Collections.Concurrent;

namespace QQListener.Protocol.Tests;

public sealed class EngineLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDownloadCannotStartAnOldSession(bool switchEngine)
    {
        CoreController.ResetToDefault();
        IReadOnlyList<int> pids = new[] { 11 };
        // An actual host reports HasExited before it has attached to QQ.
        var old = new FakeHost { HoldPreparation = true, Exited = true };
        var replacement = new FakeHost();
        var hosts = new Queue<FakeHost>([old, replacement]);
        var settings = new QqListenerSettings { Engine = ListenerEngine.PMHQ };
        var pipeline = new MessageEventPipeline(settings, new MessageRuleEvaluator(settings),
            new QqListenerDiagnosticsState(), NullLogger<MessageEventPipeline>.Instance, TimeProvider.System);
        using var runtime = new QqEngineService(settings, pipeline, new AlwaysOffClassProvider(), TimeProvider.System,
            new EngineCommandRouter(), enumeratePids: () => Volatile.Read(ref pids), createPmhqHost: () => hosts.Dequeue());
        try
        {
            await runtime.StartAsync(default);
            await old.PreparationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var session = typeof(QqEngineService).GetField("_current",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(runtime)!;
            var startup = (Task)session.GetType().GetField("ReceiveTask")!.GetValue(session)!;
            await Task.Delay(650);
            Assert.False(old.Disposed);
            if (switchEngine) settings.Engine = ListenerEngine.SnowLuma;
            else Volatile.Write(ref pids, new[] { 22 });
            await WaitUntil(() => old.Disposed);
            if (!switchEngine) await WaitUntil(() => CoreRuntime.Get() is { State: CoreRuntimeState.Connected, Pid: 22 });
            // Even a preparation operation that ignores cancellation cannot inject after switching.
            old.ReleasePreparation.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, old.Pid);
            if (!switchEngine) Assert.Equal(22, CoreRuntime.Get().Pid);
        }
        finally
        {
            old.ReleasePreparation.TrySetResult();
            await runtime.StopAsync(default);
            CoreController.ResetToDefault();
            CoreRuntime.ResetToDefault();
        }
    }

    [Fact]
    public async Task LateAccountFailureCannotUnbindTheReplacementSession()
    {
        CoreController.ResetToDefault();
        IReadOnlyList<int> pids = new[] { 11 };
        var old = new FakeHost { HoldReceiveCleanup = true };
        var replacement = new FakeHost();
        var hosts = new Queue<FakeHost>([old, replacement]);
        var router = new EngineCommandRouter();
        var changes = 0;
        router.SessionChanged += () => Interlocked.Increment(ref changes);
        var settings = new QqListenerSettings { Engine = ListenerEngine.PMHQ };
        var pipeline = new MessageEventPipeline(settings, new MessageRuleEvaluator(settings),
            new QqListenerDiagnosticsState(), NullLogger<MessageEventPipeline>.Instance, TimeProvider.System);
        using var runtime = new QqEngineService(settings, pipeline, new AlwaysOffClassProvider(), TimeProvider.System,
            router, enumeratePids: () => Volatile.Read(ref pids), createPmhqHost: () => hosts.Dequeue());
        try
        {
            await runtime.StartAsync(default);
            await WaitUntil(() => old.Pid == 11);
            // Capture the actual receiver task to await its entire exception handler, not a timing delay.
            var session = typeof(QqEngineService).GetField("_current",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(runtime)!;
            var receiver = (Task)session.GetType().GetField("ReceiveTask")!.GetValue(session)!;
            old.AccountChanged = true;
            await old.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            old.Exited = true;
            Volatile.Write(ref pids, new[] { 22 });
            await WaitUntil(() => CoreRuntime.Get() is { State: CoreRuntimeState.Connected, Pid: 22 });
            var newBindingChanges = Volatile.Read(ref changes);
            old.ReleaseCleanup.TrySetResult();
            await receiver.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(newBindingChanges, Volatile.Read(ref changes));
            Assert.Equal(CoreRuntimeState.Connected, CoreRuntime.Get().State);
            Assert.Equal(22, CoreRuntime.Get().Pid);
        }
        finally
        {
            old.ReleaseCleanup.TrySetResult();
            await runtime.StopAsync(default);
            CoreController.ResetToDefault();
            CoreRuntime.ResetToDefault();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementQqIsNotBlockedByTheOldPmhqSession(bool accountChanged)
    {
        CoreController.ResetToDefault();
        IReadOnlyList<int> pids = new[] { 11 };
        var hosts = new ConcurrentQueue<FakeHost>();
        var settings = new QqListenerSettings { Engine = ListenerEngine.PMHQ };
        var pipeline = new MessageEventPipeline(settings, new MessageRuleEvaluator(settings),
            new QqListenerDiagnosticsState(), NullLogger<MessageEventPipeline>.Instance, TimeProvider.System);
        using var runtime = new QqEngineService(settings, pipeline, new AlwaysOffClassProvider(), TimeProvider.System,
            new EngineCommandRouter(), enumeratePids: () => Volatile.Read(ref pids), createPmhqHost: () =>
            {
                var host = new FakeHost();
                hosts.Enqueue(host);
                return host;
            });
        try
        {
            await runtime.StartAsync(default);
            await WaitUntil(() => hosts.TryPeek(out var host) && host.Pid == 11);
            var old = hosts.Single();
            if (accountChanged)
            {
                // Both processes are visible when the old account becomes invalid.
                Volatile.Write(ref pids, new[] { 11, 22 });
                old.AccountChanged = true;
                await WaitUntil(() => old.Disposed);
                await Task.Delay(650);
                Assert.Single(hosts);
            }
            old.Exited = true;
            Volatile.Write(ref pids, new[] { 22 });
            await WaitUntil(() => hosts.Any(host => host.Pid == 22));
            Assert.Equal(2, hosts.Count);
            Assert.Equal(new[] { 11, 22 }, hosts.Select(host => host.Pid));
            Assert.True(old.Disposed);
        }
        finally
        {
            await runtime.StopAsync(default);
            CoreController.ResetToDefault();
            CoreRuntime.ResetToDefault();
        }
    }

    private sealed class FakeHost : IPmhqHost, IPmhqClient
    {
        public volatile int Pid;
        public volatile bool Exited;
        public volatile bool AccountChanged;
        public volatile bool Disposed;
        public bool HoldReceiveCleanup;
        public bool HoldPreparation;
        public readonly TaskCompletionSource PreparationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleasePreparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IPmhqClient Client => this;
        public bool HasExited => Exited;
        public bool NativeInitialized => true;
        public bool IsConnected => !Disposed;
        public async Task PrepareAsync(string? proxy, CancellationToken ct)
        {
            PreparationStarted.TrySetResult();
            if (HoldPreparation) await ReleasePreparation.Task;
        }
        public void Start(int qqPid) => Pid = qqPid;
        public Task<PmhqHealth> GetHealthAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (AccountChanged) throw new PmhqAccountChangedException();
            return Task.FromResult(new PmhqHealth(Pid, "10000", "") { IsLocalPipe = true });
        }
        public async Task ReceiveAsync(Action<CapturedMessage> receive, Action<PmhqHealth> onHealth, CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally
            {
                if (HoldReceiveCleanup)
                {
                    CleanupStarted.TrySetResult();
                    await ReleaseCleanup.Task;
                }
            }
        }
        public byte[] Send(int pid, string command, byte[] body) => [];
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task SwitchingBackStillWaitsForTheOldHookProcessToExit()
    {
        CoreController.ResetToDefault();
        IReadOnlyList<int> pids = new[] { 11 };
        var created = 0;
        var settings = new QqListenerSettings();
        var router = new EngineCommandRouter();
        var pipeline = new MessageEventPipeline(settings, new MessageRuleEvaluator(settings),
            new QqListenerDiagnosticsState(), NullLogger<MessageEventPipeline>.Instance, TimeProvider.System);
        using var runtime = new QqEngineService(settings, pipeline, new AlwaysOffClassProvider(), TimeProvider.System,
            router, createSnowCore: () =>
            {
                Interlocked.Increment(ref created);
                return new CoreService(enumeratePids: () => Volatile.Read(ref pids), findBinary: () => "fixture.dll",
                    injectFunc: (_, _) => new MapHandle(0x1000, 0, 0, 0x1000), unloadFunc: (_, _) => { },
                    hookActive: _ => false, recheckMainProcess: _ => true, supported: true);
            }, enumeratePids: () => Volatile.Read(ref pids));
        try
        {
            await runtime.StartAsync(default);
            await WaitUntil(() => Volatile.Read(ref created) == 1);
            settings.Engine = ListenerEngine.PMHQ;
            await WaitUntil(() => CoreRuntime.Get().Detail.Contains("重启 QQ", StringComparison.Ordinal));
            settings.Engine = ListenerEngine.SnowLuma;
            await Task.Delay(650);
            Assert.Equal(1, Volatile.Read(ref created));
            Volatile.Write(ref pids, Array.Empty<int>());
            await WaitUntil(() => Volatile.Read(ref created) == 2);
        }
        finally
        {
            await runtime.StopAsync(default);
            CoreController.ResetToDefault();
            CoreRuntime.ResetToDefault();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
