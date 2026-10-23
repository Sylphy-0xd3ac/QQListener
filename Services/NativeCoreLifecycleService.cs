using Microsoft.Extensions.Hosting;
using QQListener.Core;

namespace QQListener.Services;

/// <summary>
/// 插件生命周期钩子：跟着宿主启动/停止 <see cref="CoreService"/> 的注入生命周期。
///
/// 只在 Windows 构建里注册（见 <c>Plugin.cs</c>）；<see cref="CoreService"/> 自己在
/// 不支持的平台上也会把 Start/Stop 变成空操作，这里的条件编译只是不必要地创建实例都不做。
/// </summary>
internal sealed class NativeCoreLifecycleService(CoreService coreService) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        coreService.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        coreService.Stop();
        // CoreController/CoreRuntime 是进程级静态状态，不会跟着这个实例的生命周期
        // 自动清零。如果 ClassIsland 支持"禁用插件再重新启用"而不重启整个宿主进程，
        // 不重置的话下一次启用时新的 CoreService 首次 Reconcile 会读到这次卸载前
        // 残留的目标状态（比如 Detached），而不是干净的默认值。
        //
        // 但 CoreRuntime 不能无条件重置：Stop() 超时收尾时会往里面写一条 Error，
        // 这是"核心可能没有真正停下"这件事唯一的提示渠道——设置页此时大概率已经
        // 不可见了。如果这里照样清空成默认的 Waiting，这条提示还没被任何人看到
        // 就被自己抹掉了，等于这次修复在生产路径上从未真正生效过。
        CoreController.ResetToDefault();
        if (CoreRuntime.Get().State != CoreRuntimeState.Error)
            CoreRuntime.ResetToDefault();
        return Task.CompletedTask;
    }
}
