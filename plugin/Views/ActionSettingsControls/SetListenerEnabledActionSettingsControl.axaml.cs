using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClassIsland.Core.Abstractions.Controls;
using QQListener.Automations.Actions;

namespace QQListener.Views.ActionSettingsControls;

public class SetListenerEnabledActionSettingsControl : ActionSettingsControlBase<SetListenerEnabledActionSettings>
{
    private readonly ToggleSwitch _enabledSwitch;
    private readonly ToggleSwitch _duringClassSwitch;
    private bool _isUpdatingFromSettings;

    public SetListenerEnabledActionSettingsControl()
    {
        _enabledSwitch = new ToggleSwitch
        {
            OnContent = "启用监听",
            OffContent = "禁用监听"
        };
        _enabledSwitch.PropertyChanged += OnSwitchPropertyChanged;

        _duringClassSwitch = new ToggleSwitch
        {
            OnContent = "上课时禁用",
            OffContent = "上课时照常提醒"
        };
        _duringClassSwitch.PropertyChanged += OnSwitchPropertyChanged;

        Content = new StackPanel
        {
            Spacing = 8,
            Margin = new(10),
            Children =
            {
                new TextBlock { Text = "QQListener 监听状态" },
                _enabledSwitch,
                _duringClassSwitch,
                new TextBlock
                {
                    Text = "上课时禁用：上课期间收到的消息一律不提醒；预备铃、课间与放学后照常。",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7
                }
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncFromSettings();
        Settings.PropertyChanged += SettingsOnPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Settings.PropertyChanged -= SettingsOnPropertyChanged;
    }

    private void SettingsOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Settings.IsEnabled) or nameof(Settings.DisableDuringClass))
        {
            SyncFromSettings();
        }
    }

    private void OnSwitchPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_isUpdatingFromSettings || e.Property != ToggleSwitch.IsCheckedProperty)
        {
            return;
        }

        if (ReferenceEquals(sender, _enabledSwitch) && _enabledSwitch.IsChecked is bool enabled)
        {
            Settings.IsEnabled = enabled;
        }
        else if (ReferenceEquals(sender, _duringClassSwitch) && _duringClassSwitch.IsChecked is bool duringClass)
        {
            Settings.DisableDuringClass = duringClass;
        }
    }

    private void SyncFromSettings()
    {
        _isUpdatingFromSettings = true;
        try
        {
            _enabledSwitch.IsChecked = Settings.IsEnabled;
            _duringClassSwitch.IsChecked = Settings.DisableDuringClass;
        }
        finally
        {
            _isUpdatingFromSettings = false;
        }
    }
}
