using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQListener.Core;
using QQListener.Models;

namespace QQListener.Views.SettingsPages;

/// <summary>
/// 内核安装向导的视图模型：EULA 阅读同意、下载/更新 SnowLuma 官方内核、显示实时诊断状态。
///
/// 合规边界（跟 <c>KernelInstaller</c> 一致）：只从 SnowLuma 官方 release 下载，
/// 加速代理由用户自己填；未同意 EULA 前拒绝下载。
/// </summary>
public partial class KernelSetupViewModel : ObservableObject
{
    private readonly QqListenerSettings _settings;
    private readonly KernelInstaller _installer;
    private readonly DispatcherTimer _runtimeTimer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KernelVersionLine))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KernelVersionLine))]
    private string _currentVersionText = "未安装";

    /// <summary>内核卡片第三行：装了什么版本、现在是什么状态，一句话说完。</summary>
    public string KernelVersionLine => _settings.Engine == ListenerEngine.PMHQ
        ? $"PMHQ {PmhqBundle.Version}（按需下载 · 实验性） · {ControlStateText}"
        : !IsSupported
        ? "原生捕获仅在 Windows x64 上可用"
        : IsInstalled
            ? $"内核 {CurrentVersionText} · {ControlStateText}"
            : "内核尚未安装，请在高级设置中重新触发安装向导";

    [ObservableProperty]
    private string _latestVersionText = "尚未检查";

    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _legalText = "";

    [ObservableProperty]
    private string _runtimeStateText = "等待核心服务启动";

    /// <summary>页头左侧那一行大字，两到四个字，够一眼看完。</summary>
    [ObservableProperty]
    private string _runtimeStateHeadline = "启动中";

    /// <summary>页头状态点的颜色。写死十六进制而不是取主题资源：这四个语义色
    /// （绿/黄/红/灰）在深浅两套主题下对比度都够，交给主题反而会在某些配色下糊掉。</summary>
    [ObservableProperty]
    private IBrush _runtimeStateBrush = new SolidColorBrush(Color.Parse("#9E9E9E"));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KernelVersionLine))]
    private string _controlStateText = "运行中";

    /// <summary>暂停/恢复按钮上的字随状态变——一个写死的"暂停/恢复"看不出点下去会发生什么。</summary>
    [ObservableProperty]
    private string _pauseButtonText = "暂停监听";

    /// <summary>卸载是破坏性操作（要重新走一遍注入才能恢复），点一下就执行容易误触——
    /// 用一个"我知道后果"复选框替代真正的长按手势，勾选之后卸载按钮才会启用。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnloadCoreCommand))]
    private bool _confirmUnload;

    public bool EulaAccepted
    {
        get => _settings.KernelEulaAccepted;
        set
        {
            if (_settings.KernelEulaAccepted == value) return;
            _settings.ModifyAndSave(s => s.KernelEulaAccepted = value);
            OnPropertyChanged();
        }
    }

    public string DownloadProxy
    {
        get => _settings.KernelDownloadProxy;
        set
        {
            if (_settings.KernelDownloadProxy == value) return;
            _settings.ModifyAndSave(s => s.KernelDownloadProxy = value ?? "");
            OnPropertyChanged();
        }
    }

    /// <summary>只有 Windows 上 SnowLuma 才有官方内核；其他平台向导整体隐藏。</summary>
    public bool IsSupported { get; } = OperatingSystem.IsWindows();

    public KernelSetupViewModel(QqListenerSettings settings, string nativeDirectory)
    {
        _settings = settings;
        _installer = new KernelInstaller(nativeDirectory);
        // 诊断状态是后台线程写的全局快照，定期拉取刷新，不需要事件总线；
        // 计时器随设置页 Attach/Detach 起停，不在这里直接开始。
        _runtimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runtimeTimer.Tick += (_, _) =>
        {
            RefreshRuntimeState();
            RefreshControlState();
        };

        RefreshInstalledState();
        RefreshRuntimeState();
        RefreshControlState();
    }

    // 引用计数而不是布尔开关：设置页和安装向导窗口共用同一个实例，各自独立
    // Attach/Detach。向导打开时设置页可能被切走触发 Detach——如果直接停计时器，
    // 向导里"每秒刷新，不需要重启"的承诺就假了。计数归零才真正停，同时也顺带
    // 保证重复调用 Attach 不会把同一个事件处理器订阅两次。
    private int _attachCount;

    /// <summary>可视树/窗口进入显示状态时调用；跟 <see cref="Detach"/> 成对，
    /// 调用方可能被反复显示/隐藏而不销毁重建，所以这里不是一次性的构造逻辑。</summary>
    public void Attach()
    {
        if (++_attachCount != 1) return;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _runtimeTimer.Start();
        RefreshRuntimeState();
        RefreshControlState();
    }

    public void Detach()
    {
        if (_attachCount <= 0) return; // 防御不配对的 Detach，别把计数拉进负数
        if (--_attachCount != 0) return;
        _runtimeTimer.Stop();
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QqListenerSettings.Engine)) OnPropertyChanged(nameof(KernelVersionLine));
        if (e.PropertyName is nameof(QqListenerSettings.KernelEulaAccepted))
            OnPropertyChanged(nameof(EulaAccepted));
        if (e.PropertyName is nameof(QqListenerSettings.KernelDownloadProxy))
            OnPropertyChanged(nameof(DownloadProxy));
    }

    private void RefreshInstalledState()
    {
        IsInstalled = _installer.IsCoreInstalled();
        CurrentVersionText = _installer.CurrentVersion() ?? "未安装";
    }

    private void RefreshRuntimeState()
    {
        // CoreRuntime 的初始快照是 Waiting（"等待核心服务启动"），而非 Windows 上核心
        // 服务根本不会启动，状态就永远停在那句话上——比"不支持"更容易让人误以为
        // 只是还没转起来。这里直接按平台盖掉。
        var snapshot = IsSupported ? CoreRuntime.Get() : new CoreRuntimeSnapshot(CoreRuntimeState.Unsupported);
        RuntimeStateText = _settings.Engine == ListenerEngine.PMHQ && !string.IsNullOrWhiteSpace(snapshot.Detail)
            ? snapshot.Detail : FormatRuntimeState(snapshot);

        RuntimeStateHeadline = snapshot.State switch
        {
            CoreRuntimeState.Unsupported => "不支持",
            CoreRuntimeState.Detached => "未装载",
            CoreRuntimeState.Paused => "已暂停",
            CoreRuntimeState.NoQq => "等待 QQ",
            CoreRuntimeState.Waiting => "等待管道",
            CoreRuntimeState.Connected => "监听中",
            CoreRuntimeState.Error => "异常",
            _ => "启动中"
        };

        RuntimeStateBrush = new SolidColorBrush(Color.Parse(snapshot.State switch
        {
            CoreRuntimeState.Connected => "#3FB950",
            CoreRuntimeState.Waiting or CoreRuntimeState.NoQq or CoreRuntimeState.Paused => "#D29922",
            CoreRuntimeState.Error => "#F85149",
            _ => "#9E9E9E"
        }));
    }

    internal static string FormatRuntimeState(CoreRuntimeSnapshot snapshot) => snapshot.State switch
    {
        CoreRuntimeState.Unsupported => "当前平台不支持核心注入",
        CoreRuntimeState.Detached => "核心未装载",
        CoreRuntimeState.Paused => "监听已暂停（核心仍在 QQ 中）",
        CoreRuntimeState.NoQq => "未找到正在运行的 QQ",
        CoreRuntimeState.Waiting when snapshot.Pid > 0 =>
            $"核心已就绪，等待接收管道（QQ pid={snapshot.Pid}）",
        CoreRuntimeState.Waiting => string.IsNullOrWhiteSpace(snapshot.Detail)
            ? "正在识别 QQ 主进程"
            : snapshot.Detail,
        CoreRuntimeState.Connected when snapshot.Pid > 0 => $"已连接（QQ pid={snapshot.Pid}）",
        CoreRuntimeState.Connected => "已连接到 QQ 接收管道",
        CoreRuntimeState.Error => $"错误：{snapshot.Detail}",
        _ => snapshot.Detail
    };

    /// <summary>
    /// CoreController 是进程级静态三态机（Running/Paused/Detached），之前没有任何
    /// UI 入口能触发暂停或卸载——不光是"少个按钮"，PendingQueue（暂停期间的积压
    /// 队列）等一整套后端逻辑因此在生产上从未被真正驱动过。
    /// </summary>
    private void RefreshControlState()
    {
        var state = CoreController.GetState();
        ControlStateText = state switch
        {
            CoreState.Running => "运行中",
            CoreState.Paused => "已暂停",
            CoreState.Detached => "已卸载",
            _ => "未知"
        };
        PauseButtonText = state switch
        {
            CoreState.Paused => "恢复监听",
            CoreState.Detached => "重新装载",
            _ => "暂停监听"
        };
        UnloadCoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>单击语义：运行⇄暂停；已卸载时点它会重新触发注入。</summary>
    [RelayCommand]
    private void TogglePause()
    {
        CoreController.Toggle();
        RefreshControlState();
    }

    /// <summary>
    /// 对应类型注释里"长按（需二次确认）"的约定：设置页场景没有长按手势，改用
    /// "先勾选确认、按钮才会启用"代替，效果同样是不能一次误触就执行。
    /// </summary>
    private bool CanUnloadCore() => ConfirmUnload && CoreController.GetState() != CoreState.Detached;

    [RelayCommand(CanExecute = nameof(CanUnloadCore))]
    private void UnloadCore()
    {
        CoreController.Unload();
        ConfirmUnload = false;
        RefreshControlState();
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在检查更新…";
        try
        {
            // 检查更新本身也要走用户配的加速代理——不然填了代理也没用，
            // 光是查版本这一步就直连 api.github.com 卡死。
            var proxy = string.IsNullOrWhiteSpace(DownloadProxy) ? null : DownloadProxy.Trim();
            var status = await _installer.CheckUpdateAsync(proxy: proxy);
            RefreshInstalledState();
            LatestVersionText = status.LatestVersion ?? "未知";
            HasUpdate = status.HasUpdate;
            StatusText = status.Error.Length > 0
                ? $"检查更新失败：{status.Error}"
                : status.HasUpdate
                    ? $"发现新版本 {status.LatestVersion}"
                    : "已是最新版本";
        }
        catch (Exception ex)
        {
            StatusText = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (IsBusy) return;
        if (!EulaAccepted)
        {
            StatusText = "请先阅读并同意 SnowLuma 的 EULA / LICENSE / 隐私说明";
            return;
        }

        IsBusy = true;
        StatusText = "正在下载核心…";
        try
        {
            var proxy = string.IsNullOrWhiteSpace(DownloadProxy) ? null : DownloadProxy.Trim();
            var version = await _installer.DownloadAndInstallAsync(proxy);
            RefreshInstalledState();
            HasUpdate = false;
            StatusText = $"安装完成：{version}";
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ViewLegalTextAsync(string kind)
    {
        if (IsBusy) return;
        IsBusy = true;
        LegalText = "加载中…";
        try
        {
            var proxy = string.IsNullOrWhiteSpace(DownloadProxy) ? null : DownloadProxy.Trim();
            LegalText = await _installer.FetchLegalTextAsync(kind, proxy);
        }
        catch (Exception ex)
        {
            LegalText = $"加载失败：{ex.Message}\n\n请检查网络或加速代理设置。";
        }
        finally
        {
            IsBusy = false;
        }
    }

}
