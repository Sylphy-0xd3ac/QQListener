using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQListener.Models;
using QQListener.Services;
using QQListener.Views.Windows;
using QQListener.Core;
using FluentAvalonia.UI.Controls;
using System.Diagnostics;

namespace QQListener.Views.SettingsPages;

[SettingsPageInfo("qqlistener.settings", "QQListener", "", "")]
public partial class QqListenerSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    private readonly QqListenerSettings _settings;
    private readonly QqListenerDiagnosticsState _diagnostics;
    private readonly QqListenerPluginInfo _pluginInfo;
    private readonly bool _autoSaveEnabled;
    private readonly QqListenerSettingsPersistenceCoordinator? _persistenceCoordinator;
    private DispatcherTimer? _saveTimer;
    private bool _providerAvailableCache;
    private bool _providerProbed;
    private int _selectedEngineIndex;
    private bool _changingEngine;
    public IReadOnlyList<string> EngineNames { get; } = ["SnowLuma", "PMHQ（实验性）"];
    public bool IsPmhqSelected => _settings.Engine == ListenerEngine.PMHQ;
    public string EngineDescription => IsPmhqSelected
        ? "PMHQ 为内置本机引擎，无需启动凭证；尚未验证真实 QQ 发包。切换引擎后需要重启 QQ。"
        : "SnowLuma 为默认引擎。切换引擎后需要重启 QQ，避免同时装载不同钩子。";

    public int SelectedEngineIndex
    {
        get => _selectedEngineIndex;
        set
        {
            if (value is < 0 or > 1 || _selectedEngineIndex == value || _changingEngine) return;
            _selectedEngineIndex = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedEngineIndex)));
            _ = ChangeEngineAsync((ListenerEngine)value);
        }
    }
    private string _previewResultText = "走一遍完整的提醒链路，看看提醒实际长什么样，不需要真的收到 QQ 消息。";

    public QqListenerSettings Settings => _settings;
    public QqListenerDiagnosticsState Diagnostics => _diagnostics;
    public QqListenerPluginInfo PluginInfo => _pluginInfo;
    public KernelSetupViewModel KernelSetup { get; }

    public string PreviewResultText
    {
        get => _previewResultText;
        private set => SetPreviewProperty(ref _previewResultText, value, nameof(PreviewResultText));
    }

    /// <summary>是否能找到 QqNotificationProvider 实例，决定快捷按钮是否可点。</summary>
    public bool ProviderAvailable
    {
        get
        {
            if (!_providerProbed)
            {
                _providerProbed = true;
                _providerAvailableCache = TryGetProvider() != null;
            }
            return _providerAvailableCache;
        }
    }

    public bool CanPoll => ProviderAvailable && _settings.IsEnabled;
    public bool CanReplay => ProviderAvailable && _settings.IsEnabled;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public QqListenerSettingsPage()
        : this(new QqListenerSettings(), new QqListenerDiagnosticsState(), QqListenerPluginInfo.Designer, false)
    {
        _diagnostics.RecordRuntimeInfo("设计器", "未通过插件宿主加载", "", GetType().Assembly.Location, null);
    }

    public QqListenerSettingsPage(
        QqListenerSettings settings,
        QqListenerDiagnosticsState diagnostics,
        QqListenerPluginInfo pluginInfo)
        : this(settings, diagnostics, pluginInfo, true)
    {
    }

    private QqListenerSettingsPage(
        QqListenerSettings settings,
        QqListenerDiagnosticsState diagnostics,
        QqListenerPluginInfo pluginInfo,
        bool autoSaveEnabled)
    {
        _settings = settings;
        _selectedEngineIndex = (int)settings.Engine;
        _diagnostics = diagnostics;
        _pluginInfo = pluginInfo;
        _autoSaveEnabled = autoSaveEnabled;
        // 目录计算必须跟 Plugin.cs 里 CoreService 用的是同一个方法——两边算出
        // 不一样的路径，会出现"向导显示安装成功，核心却永远找不到文件"的问题。
        KernelSetup = new KernelSetupViewModel(
            _settings,
            QQListener.Core.KernelLocator.ResolveNativeDirectory(_settings.SettingsDirectory));
        DataContext = this;
        InitializeComponent();

        if (_autoSaveEnabled)
        {
            _persistenceCoordinator = new QqListenerSettingsPersistenceCoordinator(
                _settings,
                _diagnostics,
                OnSettingsPropertyChanged);
        }
    }

    private void OnSettingsPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QqListenerSettings.Engine)) RefreshEngineSelection();
        if (e.PropertyName == nameof(QqListenerSettings.IsEnabled))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPoll)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanReplay)));
        }

        if (_persistenceCoordinator?.IsDirty == true)
        {
            DebounceSave();
        }
    }

    private void RefreshEngineSelection()
    {
        _selectedEngineIndex = (int)_settings.Engine;
        foreach (var name in new[] { nameof(SelectedEngineIndex), nameof(IsPmhqSelected), nameof(EngineDescription) })
            PropertyChanged?.Invoke(this, new(name));
    }

    private async Task ChangeEngineAsync(ListenerEngine engine)
    {
        _changingEngine = true;
        try
        {
            if (engine == _settings.Engine) return;
            if (TopLevel.GetTopLevel(this) is not { } owner) return;
            var dialog = new ContentDialog
            {
                Title = engine == ListenerEngine.PMHQ ? "PMHQ 是实验性功能" : "切换为 SnowLuma",
                Content = engine == ListenerEngine.PMHQ
                    ? "PMHQ 使用本机 QQ，无需 auth_token 或 manager。真实 QQ 注入、签名和发包尚未验证，可能无法接收消息或导致 QQ 异常。切换后请退出并重启 QQ。"
                    : "切换后请退出并重启 QQ。旧进程退出前，插件会等待，不会向同一个 QQ 进程装载另一套钩子。",
                PrimaryButtonText = "切换",
                CloseButtonText = "取消"
            };
            if (await dialog.ShowAsync(owner) == ContentDialogResult.Primary) _settings.Engine = engine;
        }
        catch (Exception ex) { _diagnostics.RecordError(ex, "切换消息引擎失败"); }
        finally { _changingEngine = false; RefreshEngineSelection(); }
    }

    private async void OnOpenPmhqConfigurationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var bundle = new PmhqBundle(KernelLocator.ResolveNativeDirectory(_settings.SettingsDirectory));
            await Task.Run(bundle.Prepare);
            Process.Start(new ProcessStartInfo(bundle.BinaryDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { _diagnostics.RecordError(ex, "打开 PMHQ 内核目录失败"); }
    }

    private void OnRecheckPmhqClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        IAppHost.Host?.Services.GetService<QqEngineService>()?.RequestRecheck();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _persistenceCoordinator?.Attach();
        KernelSetup.Attach();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _persistenceCoordinator?.Detach();
        if (_saveTimer != null)
        {
            _saveTimer.Stop();
            _saveTimer.Tick -= OnSaveTimerTick;
            _saveTimer = null;
        }

        KernelSetup.Detach();

        base.OnDetachedFromVisualTree(e);
    }

    private void DebounceSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += OnSaveTimerTick;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        _persistenceCoordinator?.SavePendingSettings();
    }

    // ===== 快捷操作 按钮 handlers =====

    private async void OnPollNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var provider = TryGetProvider();
            if (provider == null) return;
            await provider.PollOnceManuallyAsync();
        }
        catch (Exception ex)
        {
            _diagnostics.RecordError(ex, "手动扫一次失败");
        }
    }

    private void OnResetClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var provider = TryGetProvider();
            if (provider == null) return;
            provider.ResetDetectionState();
            _diagnostics.RecordIgnored("已手动重置去重与冷却状态");
        }
        catch (Exception ex)
        {
            _diagnostics.RecordError(ex, "重置去重失败");
        }
    }

    private async void OnReplayLatestClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (!_settings.IsEnabled) return;

            var provider = TryGetProvider();
            if (provider == null || !_settings.IsEnabled) return;

            await provider.ReplayLatestAsync();
        }
        catch (Exception ex)
        {
            _diagnostics.RecordError(ex, "重示最后一条失败");
        }
    }

    private async void OnChooseDownloadsDirectoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider.CanPickFolder != true) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择附件下载目录", AllowMultiple = false
            });
            using var folder = folders.FirstOrDefault();
            var path = folder?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path)) _settings.DownloadsDirectory = path;
        }
        catch (Exception exception)
        {
            _diagnostics.RecordError(exception, "选择附件下载目录失败");
        }
    }

    private void OnShowTestNotificationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowTestNotification(MessagePreviewKind.Text);

    private void OnShowTestImportantNotificationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowTestNotification(MessagePreviewKind.Media);

    private void OnShowTestCallingNotificationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        ShowTestNotification(MessagePreviewKind.Quote);

    /// <summary>
    /// 走真实的提醒链路弹一条测试消息——看到的就是插件实际的提醒外观。
    ///
    /// 这是个 async void 辅助方法（不是事件处理器本身），异常没有 Task 可以观察，
    /// 会直接顶着调用它的同步上下文冒出去；DI 解析必须也在 try 里——插件重载/
    /// 宿主关闭期间 ServiceProvider 可能已经被 dispose，GetService 会抛
    /// ObjectDisposedException，不接住就是整个 ClassIsland 崩溃，而不是设置页
    /// 上一行"找不到提醒提供方"的提示。
    /// </summary>
    private async void ShowTestNotification(MessagePreviewKind kind)
    {
        try
        {
            var host = IAppHost.Host?.Services;
            var provider = host?.GetService<QqNotificationProvider>()
                           ?? host?.GetServices<IHostedService>().OfType<QqNotificationProvider>().FirstOrDefault();
            if (provider is null)
            {
                PreviewResultText = "找不到 QQListener 提醒提供方";
                return;
            }

            await provider.ShowTestMessageAsync(MessageImportance.Normal, kind: kind);
        }
        catch (Exception ex)
        {
            _diagnostics.RecordError(ex, "显示测试提醒失败");
            PreviewResultText = "显示测试提醒失败，请查看诊断信息";
        }
    }

    private void OnOpenKernelWizardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var wizard = new KernelSetupWizardWindow(KernelSetup);
        if (TopLevel.GetTopLevel(this) is Window owner)
            wizard.ShowDialog(owner);
        else
            wizard.Show();
    }

    private void SetPreviewProperty(ref string field, string value, string propertyName)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static QqNotificationProvider? TryGetProvider()
    {
        var services = IAppHost.Host?.Services;
        if (services == null) return null;
        return services.GetService<QqNotificationProvider>()
               ?? services.GetServices<IHostedService>().OfType<QqNotificationProvider>().FirstOrDefault();
    }
}
