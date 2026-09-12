using QQListener.Core;

namespace QQListener.Protocol.Tests;

/// <summary>对应 Python 侧 test_core_controller.py。<see cref="CoreController"/> 是进程内全局单例，
/// 每个测试前后都重置，跟 pytest 的 autouse fixture 是同一个用意。</summary>
public class CoreControllerTests : IDisposable
{
    public CoreControllerTests() => CoreController.ResetToDefault();
    public void Dispose() => CoreController.ResetToDefault();

    [Fact]
    public void DefaultIsRunning()
    {
        Assert.Equal(CoreState.Running, CoreController.GetState());
        Assert.True(CoreController.IsRunning());
    }

    [Fact]
    public void ToggleRunningToPausedAndBack()
    {
        Assert.Equal(CoreState.Paused, CoreController.Toggle());
        Assert.False(CoreController.IsRunning());
        Assert.Equal(CoreState.Running, CoreController.Toggle());
    }

    [Fact]
    public void ToggleFromDetachedStartsRunning()
    {
        CoreController.SetState(CoreState.Detached);
        Assert.Equal(CoreState.Running, CoreController.Toggle());
    }

    [Fact]
    public void UnloadGoesDetachedThenToggleRestarts()
    {
        Assert.Equal(CoreState.Detached, CoreController.Unload());
        Assert.Equal(CoreState.Detached, CoreController.GetState());
        Assert.Equal(CoreState.Running, CoreController.Toggle());
    }

    [Fact]
    public void ListenerFiresWithNewState()
    {
        var seen = new List<CoreState>();
        CoreController.AddListener(seen.Add);

        CoreController.Toggle(); // Running -> Paused

        Assert.Equal([CoreState.Paused], seen);
    }

    [Fact]
    public void NoEmitWhenStateUnchanged()
    {
        var seen = new List<CoreState>();
        CoreController.AddListener(seen.Add);

        CoreController.SetState(CoreState.Running); // 已经是 Running

        Assert.Empty(seen);
    }
}
