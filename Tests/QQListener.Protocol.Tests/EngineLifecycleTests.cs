using Microsoft.Extensions.Logging.Abstractions;
using QQListener.Core;
using QQListener.Models;
using QQListener.Native;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public sealed class EngineLifecycleTests
{
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
