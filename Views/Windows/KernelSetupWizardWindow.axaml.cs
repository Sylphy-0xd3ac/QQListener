using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using QQListener.Views.SettingsPages;

namespace QQListener.Views.Windows;

/// <summary>
/// SnowLuma 内核安装向导：说明 → 协议 → 下载安装 → 完成，四步走。
///
/// 跟设置页共用同一个 <see cref="KernelSetupViewModel"/> 实例（由设置页构造并持有），
/// 这里只是给它加一层"一步步来"的呈现，状态本身不重复。
/// </summary>
public partial class KernelSetupWizardWindow : Window, INotifyPropertyChanged
{
    private const int WelcomeStep = 0;
    private const int EulaStep = 1;
    private const int DownloadStep = 2;
    private const int DoneStep = 3;

    private int _step;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public KernelSetupViewModel Kernel { get; }

    /// <summary>设计器用；真实调用走带参数的构造函数。</summary>
    public KernelSetupWizardWindow()
        : this(new KernelSetupViewModel(new Models.QqListenerSettings(), AppContext.BaseDirectory))
    {
    }

    public KernelSetupWizardWindow(KernelSetupViewModel kernel)
    {
        Kernel = kernel;
        // 向导窗口跟设置页共用同一个 ViewModel；自己也要 Attach，否则用户开着
        // 向导时切走设置页，会把共享的运行状态刷新计时器停掉（Attach 是引用计数的，
        // 不会因为这里也调用一次就打乱设置页自己的计次）。
        //
        // Attach() 和事件订阅必须包在 try 里：AvaloniaXamlLoader.Load 出错时窗口
        // 从未真正打开，Closed 永远不会触发，这个 +1 就永久回不来了——设置页每次
        // 点"安装向导"都会再泄漏一次，_runtimeTimer 会跑到插件生命周期结束。
        Kernel.Attach();
        try
        {
            Kernel.PropertyChanged += OnKernelPropertyChanged;
            DataContext = this;
            AvaloniaXamlLoader.Load(this);
        }
        catch
        {
            Kernel.PropertyChanged -= OnKernelPropertyChanged;
            Kernel.Detach();
            throw;
        }
        Closed += (_, _) =>
        {
            Kernel.PropertyChanged -= OnKernelPropertyChanged;
            Kernel.Detach();
        };
    }

    public bool IsWelcomeStep => _step == WelcomeStep;
    public bool IsEulaStep => _step == EulaStep;
    public bool IsDownloadStep => _step == DownloadStep;
    public bool IsDoneStep => _step == DoneStep;
    public bool IsNotDoneStep => !IsDoneStep;

    public bool CanGoBack => _step > WelcomeStep;

    /// <summary>协议步骤没同意之前、下载步骤内核没真正装好之前，都不让往下走——
    /// 否则用户能跳过下载直接看到"完成"页，那页上却显示着并不成立的运行状态。</summary>
    public bool CanGoNext => _step switch
    {
        EulaStep => Kernel.EulaAccepted,
        DownloadStep => Kernel.IsInstalled,
        DoneStep => false,
        _ => true
    };

    public FontWeight WelcomeStepWeight => StepWeight(WelcomeStep);
    public FontWeight EulaStepWeight => StepWeight(EulaStep);
    public FontWeight DownloadStepWeight => StepWeight(DownloadStep);
    public FontWeight DoneStepWeight => StepWeight(DoneStep);

    public IBrush WelcomeStepBrush => StepBrush(WelcomeStep);
    public IBrush EulaStepBrush => StepBrush(EulaStep);
    public IBrush DownloadStepBrush => StepBrush(DownloadStep);
    public IBrush DoneStepBrush => StepBrush(DoneStep);

    private FontWeight StepWeight(int step) => step == _step ? FontWeight.SemiBold : FontWeight.Normal;

    private static readonly IBrush ActiveStepBrush = Brushes.White;
    private static readonly IBrush InactiveStepBrush = Brushes.Gray;

    private IBrush StepBrush(int step) => step == _step ? ActiveStepBrush : InactiveStepBrush;

    private void OnKernelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KernelSetupViewModel.EulaAccepted) or nameof(KernelSetupViewModel.IsInstalled))
            Raise(nameof(CanGoNext));
    }

    private void OnBackClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => GoTo(_step - 1);

    private void OnNextClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => GoTo(_step + 1);

    private void OnFinishClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void GoTo(int step)
    {
        _step = Math.Clamp(step, WelcomeStep, DoneStep);
        Raise(nameof(IsWelcomeStep), nameof(IsEulaStep), nameof(IsDownloadStep), nameof(IsDoneStep),
            nameof(IsNotDoneStep), nameof(CanGoBack), nameof(CanGoNext),
            nameof(WelcomeStepWeight), nameof(EulaStepWeight), nameof(DownloadStepWeight), nameof(DoneStepWeight),
            nameof(WelcomeStepBrush), nameof(EulaStepBrush), nameof(DownloadStepBrush), nameof(DoneStepBrush));
    }

    private void Raise(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
