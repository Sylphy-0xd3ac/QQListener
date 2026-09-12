using QQListener.Core;
using QQListener.Models;
using QQListener.Views.SettingsPages;
using Xunit;

namespace QQListener.Tests.Views.SettingsPages;

/// <summary>
/// CoreController 的三态机之前完全没有生产 UI 入口能触发暂停/卸载——不只是"少个
/// 按钮"，暂停期间的积压队列等一整套后端逻辑因此在生产上从未被真正驱动过。
/// 这里锁定新加的两个命令确实驱动了 CoreController 本身。
/// </summary>
public sealed class KernelSetupViewModelTests : IDisposable
{
    public KernelSetupViewModelTests() => CoreController.ResetToDefault();
    public void Dispose() => CoreController.ResetToDefault();

    private static KernelSetupViewModel CreateViewModel() =>
        new(new QqListenerSettings(), Path.GetTempPath());

    [Fact]
    public void TogglePauseCommandFlipsCoreControllerState()
    {
        var vm = CreateViewModel();

        vm.TogglePauseCommand.Execute(null);
        Assert.Equal(CoreState.Paused, CoreController.GetState());
        Assert.Equal("已暂停", vm.ControlStateText);

        vm.TogglePauseCommand.Execute(null);
        Assert.Equal(CoreState.Running, CoreController.GetState());
        Assert.Equal("运行中", vm.ControlStateText);
    }

    [Fact]
    public void UnloadCoreCommandRequiresConfirmationCheckboxAndResetsItAfterUse()
    {
        var vm = CreateViewModel();
        Assert.False(vm.UnloadCoreCommand.CanExecute(null));

        vm.ConfirmUnload = true;
        Assert.True(vm.UnloadCoreCommand.CanExecute(null));

        vm.UnloadCoreCommand.Execute(null);

        Assert.Equal(CoreState.Detached, CoreController.GetState());
        Assert.Equal("已卸载", vm.ControlStateText);
        // 卸载完成后复选框会自动清空——防止连点误触把"确认"状态留在原地。
        Assert.False(vm.ConfirmUnload);
        Assert.False(vm.UnloadCoreCommand.CanExecute(null));
    }

    [Fact]
    public void UnloadCoreCommandIsDisabledWhenAlreadyDetached()
    {
        var vm = CreateViewModel();
        vm.ConfirmUnload = true;
        vm.UnloadCoreCommand.Execute(null);

        // 已经是 Detached 了，即便再勾一次确认框也不该允许重复执行。
        vm.ConfirmUnload = true;
        Assert.False(vm.UnloadCoreCommand.CanExecute(null));
    }

    [Fact]
    public void WaitingStateOnlyShowsPidAfterARealQqProcessWasIdentified()
    {
        Assert.Equal(
            "等待核心服务启动",
            KernelSetupViewModel.FormatRuntimeState(
                new CoreRuntimeSnapshot(CoreRuntimeState.Waiting, "等待核心服务启动")));
        Assert.Equal(
            "核心已就绪，等待接收管道（QQ pid=1234）",
            KernelSetupViewModel.FormatRuntimeState(
                new CoreRuntimeSnapshot(CoreRuntimeState.Waiting, "核心已映射", 1234)));
    }

    [Fact]
    public void ConnectedStateWithoutPidDoesNotRenderPidZero()
    {
        var text = KernelSetupViewModel.FormatRuntimeState(
            new CoreRuntimeSnapshot(CoreRuntimeState.Connected, "已连接"));

        Assert.Equal("已连接到 QQ 接收管道", text);
        Assert.DoesNotContain("pid=0", text);
    }
}
