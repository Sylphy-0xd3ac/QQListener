using CommunityToolkit.Mvvm.ComponentModel;

namespace QQListener.Automations.Actions;

public partial class SetListenerEnabledActionSettings : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>上课期间不弹提醒。默认开启——课堂软件的插件在上课时把人打断，
    /// 是它最不该做的事。</summary>
    [ObservableProperty]
    private bool _disableDuringClass = true;
}
