using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQListener.Services;

namespace QQListener.Automations.Actions;

/// <summary>
/// 发一条假消息走完整的提醒链路，用来确认提醒 UI 的实际样子。
///
/// 和「显示最后一条 QQ 消息」不同：那个依赖真的收到过消息，而本机（或非 Windows
/// 平台）根本读不到通知中心。这个动作自带内容，任何时候都能触发。
/// </summary>
[ActionInfo(AutomationId, "显示测试 QQ 消息", "", addDefaultToMenu: true)]
public class ShowTestMessageAction : ActionBase
{
    internal const string AutomationId = "QQListener.ShowTestMessage";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();

        var host = IAppHost.Host?.Services;
        if (host == null) return;

        var provider = host.GetService<QqNotificationProvider>()
                       ?? host.GetServices<IHostedService>().OfType<QqNotificationProvider>().FirstOrDefault();
        if (provider is null) return;

        await provider.ShowTestMessageAsync();
    }
}
