using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using QQListener.Models;

namespace QQListener.Automations.Actions;

[ActionInfo(AutomationId, "设置 QQListener 监听状态", "\uE73C", addDefaultToMenu: false)]
public class SetListenerEnabledAction(QqListenerSettings settings) : ActionBase<SetListenerEnabledActionSettings>
{
    internal const string AutomationId = "QQListener.SetListenerEnabled";

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        // 一次写完再存盘：ModifyAndSave 期间会压制自动保存，两个字段拆成两次调用
        // 就是两次落盘，中间还会有一瞬间的半套状态被别处读到。
        settings.ModifyAndSave(x =>
        {
            x.IsEnabled = Settings.IsEnabled;
            x.DisableDuringClass = Settings.DisableDuringClass;
        });
    }
}
