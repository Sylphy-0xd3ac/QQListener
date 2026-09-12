using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using QQListener.Models;

namespace QQListener.Views.Windows;

/// <summary>
/// QQ 消息的独立浮窗。
///
/// ClassIsland 的提醒覆盖层是一条很窄的顶栏，塞发送者 + 正文会挤成一团，所以内容
/// 单独开一个窗口显示：正文卡片宽 520，内容超高时滚动。
/// </summary>
public partial class QqMessageWindow : Window
{
    /// <summary>ClassIsland 的提醒是「遮罩 + 覆盖层」两段，浮窗得盖住整段，
    /// 否则正文会先消失、顶栏还亮着。</summary>
    private static readonly TimeSpan MaskDuration = TimeSpan.FromSeconds(2);

    /// <summary>给 ClassIsland 顶栏让出的高度（逻辑像素）。</summary>
    private const double TopBarClearanceDip = 62;

    /// <summary>入场时卡片从下方多少逻辑像素滑上来。对齐 PySide6 版的 50px。</summary>
    private const double SlideDistanceDip = 50;

    /// <summary>入场位移 600ms OutBack、淡入 500ms OutExpo、出场淡出 300ms——
    /// 三个数都照抄 PySide6 版 <c>notify_window.py</c> 的 init_animation / close_animation。</summary>
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan EnterFade = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExitFade = TimeSpan.FromMilliseconds(300);

    private static readonly Easing SlideEasing = new BackEaseOut();

    private DispatcherTimer? _closeTimer;
    private DispatcherTimer? _slideTimer;
    private IDisposable? _showTimer;
    private IDisposable? _exitTimer;
    private MessageContentView? _messageContent;
    private MessageReplyBar? _replyBar;

    /// <summary>动画的落点。位移期间 <see cref="Window.Position"/> 会低于它，
    /// 所以重新居中（SizeChanged）必须改这个锚点而不是直接写 Position，
    /// 否则一次尺寸变化就会把正在滑动的窗口瞬移到终点。</summary>
    private PixelPoint _anchor;

    /// <summary>当前离落点还差多少物理像素（向下为正）。</summary>
    private double _slideOffset;

    private bool _performanceMode;

    public QqMessageWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
    }

    private QqMessageWindow(MessageEvent message, string downloadsDirectory)
    {
        SenderText = string.IsNullOrWhiteSpace(message.Sender) ? "未知发送者" : message.Sender;
        ConversationText = message.Conversation ?? "";
        SenderLine = !string.IsNullOrWhiteSpace(ConversationText)
                     && !string.Equals(ConversationText, SenderText, StringComparison.Ordinal)
            ? SenderText + " · " + ConversationText
            : SenderText;
        BodyText = string.IsNullOrWhiteSpace(message.Body) ? "（空消息）" : message.Body;
        TimeText = message.ReceivedAt.ToLocalTime().ToString("HH:mm");

        // 属性先赋值再加载 XAML，绑定一次就能取到正确内容。
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
        _messageContent = new MessageContentView(message, downloadsDirectory);
        this.FindControl<ContentControl>("MessageContentHost")!.Content = _messageContent;

        var replyTarget = message.IsPendingDigest ? message.PendingMessages[^1] : message;
        if (replyTarget.CanReply)
        {
            var replyBar = new MessageReplyBar(replyTarget);
            if (message.IsPendingDigest)
                replyBar.Children.Insert(0, new TextBlock
                {
                    Text = $"回复最后一条：{replyTarget.DisplaySender}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = Avalonia.Media.Brushes.White,
                    FontSize = 12
                });
            // 正在输入时别让窗口的自动关闭计时器把它收走；发完/取消后重新计满一轮展示时间。
            replyBar.ComposerOpened += () => _closeTimer?.Stop();
            replyBar.ComposerClosed += () => { if (!_closing) _closeTimer?.Start(); };
            var host = this.FindControl<ContentControl>("ReplyBarHost")!;
            host.Content = replyBar;
            host.IsVisible = true;
            _replyBar = replyBar;
        }

        Closed += (_, _) =>
        {
            _closing = true;
            _showTimer?.Dispose();
            _exitTimer?.Dispose();
            _closeTimer?.Stop();
            StopSlide();
            _messageContent.Dispose();
            _replyBar?.Dispose();
        };
    }

    public string SenderText { get; } = "";
    public string ConversationText { get; } = "";

    /// <summary>顶行的发送者：有群名就并成「张三 · 高三2班」，不另起一行。</summary>
    public string SenderLine { get; } = "";

    public string BodyText { get; } = "";
    public string TimeText { get; } = "";
    public bool IsNativeBackdropClipped { get; private set; }
    /// <summary>弹一个浮窗，到时自动关闭；交互不延长显示时间。</summary>
    public static QqMessageWindow ShowMessage(MessageEvent message, TimeSpan duration,
        string? downloadsDirectory = null, bool topmost = true, bool performanceMode = false)
    {
        var window = new QqMessageWindow(message, downloadsDirectory ?? new QqListenerSettings().DownloadsDirectory)
        {
            Topmost = topmost,
            _performanceMode = performanceMode
        };
        window.ApplyPerformanceMode();
        window.Opened += (_, _) =>
        {
            window.PlaceOnScreen();
            var radius = window.FindControl<Border>("Card")!.CornerRadius.TopLeft;
            window.IsNativeBackdropClipped = NativeBackdropCorners.Apply(window, radius);
        };
        // SizeToContent 的最终尺寸在 Opened 之后才定稿，尺寸一变就重新居中。
        window.SizeChanged += (_, _) => window.PlaceOnScreen();

        // 等 ClassIsland 那条蓝色遮罩展开完再露面，否则两个东西同时animate，
        // 看起来像是抢着弹出来。
        window._showTimer = DispatcherTimer.RunOnce(
            () =>
            {
                if (window._closing) return;
                try
                {
                    window.Show();
                    window.AnimateIn();
                    window._closeTimer = new DispatcherTimer { Interval = duration };
                    window._closeTimer.Tick += (_, _) => window.SafeClose();
                    window._closeTimer.Start();
                }
                catch
                {
                    // 这 2 秒空窗期里窗口从未 Show 过，Closed 事件不会触发——
                    // 之前这里异常直接冒到 Avalonia 的 dispatcher 循环，在
                    // ClassIsland 里就是未处理异常；_messageContent/_replyBar
                    // 持有的 CTS 和已加载的位图也会永远不释放。这里手动做一遍
                    // Closed 处理器本该做的收尾，并且不让异常继续往外冒。
                    window._closing = true;
                    window._messageContent?.Dispose();
                    window._replyBar?.Dispose();
                }
            },
            MaskDuration);

        return window;
    }

    /// <summary>屏幕正上方居中，贴在 ClassIsland 顶栏正下方。</summary>
    private void PlaceOnScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        MaxWidth = area.Width / scale;
        MaxHeight = Math.Max(100, area.Height / scale - TopBarClearanceDip);

        // Bounds 是逻辑像素，Position 是物理像素——不换算就永远居不了中。
        var width = (int)Math.Round(Bounds.Width * scale);
        _anchor = new PixelPoint(
            area.X + Math.Max(0, (area.Width - width) / 2),
            area.Y + (int)Math.Round(TopBarClearanceDip * scale));
        ApplyPosition();
    }

    private void ApplyPosition() =>
        Position = new PixelPoint(_anchor.X, _anchor.Y + (int)Math.Round(_slideOffset));

    /// <summary>性能模式：纯色背板顶掉毛玻璃，并且关掉窗口级透明度提示——
    /// 实时模糊采样是这个浮窗最贵的一笔绘制开销。</summary>
    private void ApplyPerformanceMode()
    {
        if (!_performanceMode) return;

        TransparencyLevelHint = [WindowTransparencyLevel.None];
        if (this.FindControl<Control>("AcrylicBackdrop") is { } acrylic) acrylic.IsVisible = false;
        if (this.FindControl<Control>("SolidBackdrop") is { } solid) solid.IsVisible = true;
        // 动画也一并去掉：Transitions 留着的话下面把 Opacity 设成 1 仍会走 500ms 补间。
        if (this.FindControl<Border>("Card") is { } card) card.Transitions = null;
    }

    /// <summary>
    /// 入场：卡片淡入（500ms OutExpo，走 XAML 里声明的 Transition），同时整个窗口从
    /// 下方 50px 滑到位（600ms OutBack，带一点回弹）。位移做在窗口上而不是卡片上——
    /// 卡片一挪，它让出来的那条边会露出窗口级的模糊背板。
    /// </summary>
    private void AnimateIn()
    {
        var card = this.FindControl<Border>("Card");
        if (card is null) return;
        card.Opacity = 1;

        if (_performanceMode) return;
        StartSlideIn();
    }

    private void StartSlideIn()
    {
        var scale = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.Scaling ?? 1;
        if (scale <= 0) scale = 1;
        var distance = SlideDistanceDip * scale;

        _slideOffset = distance;
        ApplyPosition();

        var clock = Stopwatch.StartNew();
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += (_, _) =>
        {
            if (_closing)
            {
                StopSlide();
                return;
            }

            var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / SlideDuration.TotalMilliseconds, 0, 1);
            // BackEaseOut 会冲过 1 再弹回来，所以 offset 中途会变成负的（略微过冲到锚点上方），
            // 这正是 PySide6 版 OutBack 的观感。
            _slideOffset = distance * (1 - SlideEasing.Ease(progress));
            ApplyPosition();

            if (progress >= 1)
            {
                _slideOffset = 0;
                ApplyPosition();
                StopSlide();
            }
        };
        _slideTimer.Start();
    }

    private void StopSlide()
    {
        _slideTimer?.Stop();
        _slideTimer = null;
    }

    /// <summary>淡出内容，动画结束后关闭原生窗口与模糊背板。幂等——可以被外部
    /// （<see cref="Services.ClassIslandMessageNotifier"/> 强制收拢过多浮窗时）安全地重复调用。</summary>
    internal void SafeClose()
    {
        _closeTimer?.Stop();
        _closeTimer = null;
        if (_closing) return;
        _closing = true;
        _showTimer?.Dispose();
        StopSlide();

        var card = this.FindControl<Border>("Card");
        if (card is null || _performanceMode)
        {
            Close();
            return;
        }

        // 出场比入场快：500ms 淡入是为了让人注意到，300ms 淡出是为了别碍事。
        card.Transitions =
        [
            new DoubleTransition
            {
                Property = OpacityProperty,
                Duration = ExitFade,
                Easing = new LinearEasing()
            }
        ];
        card.Opacity = 0;
        _exitTimer = DispatcherTimer.RunOnce(Close, ExitFade);
    }

    private bool _closing;
}
