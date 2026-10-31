using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using QQListener.Views.SettingsPages;

namespace QQListener.Views.Windows;

public partial class EngineDownloadWindow : Window
{
    public EngineDownloadWindow() { AvaloniaXamlLoader.Load(this); }
    public EngineDownloadWindow(KernelSetupViewModel model) : this()
    {
        DataContext = model;
        model.Attach();
        Closed += (_, _) => model.Detach();
    }
}
