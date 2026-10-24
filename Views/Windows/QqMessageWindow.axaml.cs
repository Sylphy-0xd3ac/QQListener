using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClassIsland.Core.Models.Notification;
using QQListener.Models;
using QQListener.Services;

namespace QQListener.Views.Windows;

/// <summary>
/// QQ 消息的独立浮窗。
///
/// ClassIsland 的提醒覆盖层是一条很窄的顶栏，塞发送者 + 正文会挤成一团，所以内容
/// 单独开一个窗口显示：正文卡片宽 520，内容超高时滚动。
/// </summary>
public partial class QqMessageWindow : Window
{
    /// <summary>无宿主请求的独立预览在遮罩预留时间后显示。</summary>
    private static readonly TimeSpan MaskDuration = TimeSpan.FromSeconds(2);

    /// <summary>给 ClassIsland 顶栏让出的高度（逻辑像素）。</summary>
    private const double TopBarClearanceDip = 62;

    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(260);
    private static readonly Easing SlideEasing = new CubicEaseOut();
    private double _drawerProgress = 1;
    private double _drawerHeight;
    private DispatcherTimer? _placementTimer;

    private DispatcherTimer? _closeTimer;
    private DispatcherTimer? _hostTimer;
    private DispatcherTimer? _slideTimer;
    private IDisposable? _showTimer;
    private IDisposable? _exitTimer;
    private MessageContentView? _messageContent;
    private MessageReplyBar? _replyBar;
    private NotificationPresentationLifetime? _notificationLifetime;

    private PixelPoint _anchor;

    private bool _performanceMode;

    public QqMessageWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = this;
    }

    private QqMessageWindow(MessageEvent message, string downloadsDirectory, string defaultReplyText,
        bool autoDownloadFiles, bool replyOnConfirmEnabled, bool replyWithOkReaction,
        MessageReplyService? replyService, Core.EngineCommandRouter? engines)
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
        _messageContent = new MessageContentView(message, downloadsDirectory, autoDownloadFiles: autoDownloadFiles, engines: engines);
        this.FindControl<ContentControl>("MessageContentHost")!.Content = _messageContent;

        var replyTarget = message.IsPendingDigest ? message.PendingMessages[^1] : message;
        var host = this.FindControl<ContentControl>("ReplyBarHost")!;
        host.IsVisible = true;
        if (replyTarget.CanReply || replyTarget.CanReact)
        {
            var replyBar = new MessageReplyBar(replyTarget, defaultReplyText, replyService,
                replyOnConfirmEnabled: replyOnConfirmEnabled, replyWithOkReaction: replyWithOkReaction);
            if (message.IsPendingDigest && replyOnConfirmEnabled)
                replyBar.Children.Insert(0, new TextBlock
                {
                    Text = $"{(replyWithOkReaction ? "回应" : "回复")}最后一条：{replyTarget.DisplaySender}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 12
                });
            replyBar.SendStarted += () => _closeTimer?.Stop();
            replyBar.SendFailed += () => { if (!_closing) _closeTimer?.Start(); };
            replyBar.DismissRequested += SafeClose;
            host.Content = replyBar;
            _replyBar = replyBar;
        }
        else
        {
            var cancel = new Button
            {
                Content = "取消", MinWidth = 80,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
            };
            cancel.Click += (_, _) => SafeClose();
            host.Content = cancel;
        }

        Closed += (_, _) =>
        {
            _closing = true;
            _showTimer?.Dispose();
            _exitTimer?.Dispose();
            _closeTimer?.Stop();
            _hostTimer?.Stop();
            _placementTimer?.Stop();
            StopSlide();
            _messageContent.Dispose();
            _replyBar?.Dispose();
            _notificationLifetime?.Dispose();
        };
    }

    public string SenderText { get; } = "";
    public string ConversationText { get; } = "";

    /// <summary>顶行的发送者：有群名就并成「张三 · 高三2班」，不另起一行。</summary>
    public string SenderLine { get; } = "";

    public string BodyText { get; } = "";
    public string TimeText { get; } = "";
    public bool IsNativeBackdropClipped { get; private set; }

    internal void UpdateQueuedCount(int count)
    {
        var label = this.FindControl<TextBlock>("QueuedMessagesText")!;
        label.Text = $"还有 {count} 条消息待显示";
        label.IsVisible = count > 0;
    }
    /// <summary>浮窗跟随宿主覆盖层的播放生命周期；独立预览按时关闭。</summary>
    public static QqMessageWindow ShowMessage(MessageEvent message, TimeSpan duration,
        string? downloadsDirectory = null, bool topmost = true, bool performanceMode = false, string defaultReplyText = "收到",
        NotificationRequest? notificationRequest = null, CancellationToken cancellationToken = default,
        bool autoDownloadFiles = false, bool replyOnConfirmEnabled = false, bool replyWithOkReaction = false,
        MessageReplyService? replyService = null, Core.EngineCommandRouter? engines = null)
    {
        var window = new QqMessageWindow(message, downloadsDirectory ?? new QqListenerSettings().DownloadsDirectory,
            defaultReplyText, autoDownloadFiles, replyOnConfirmEnabled, replyWithOkReaction, replyService, engines)
        {
            Topmost = topmost,
            _performanceMode = performanceMode
        };
        if (notificationRequest is not null)
            window._notificationLifetime = new NotificationPresentationLifetime(notificationRequest,
                () => Dispatcher.UIThread.Post(window.SafeClose), cancellationToken);
        window.ApplyHostSurface();
        window.Opened += (_, _) =>
        {
            window.ApplyHostSurface();
            window.PlaceOnScreen();
            if (window._placementTimer is null)
            {
                window._placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                window._placementTimer.Tick += (_, _) => window.PlaceOnScreen();
            }
            window._placementTimer.Start();
            var radius = window.FindControl<Border>("Card")!.CornerRadius.TopLeft;
            window.IsNativeBackdropClipped = NativeBackdropCorners.Apply(window, radius);
        };
        // SizeToContent 的最终尺寸在 Opened 之后才定稿，尺寸一变就重新居中。
        window.SizeChanged += (_, _) => window.PlaceOnScreen();

        void ShowContent()
        {
            if (window._closing || window.IsVisible) return;
            try
            {
                window.Show();
                window.AnimateIn();
                if (notificationRequest is null)
                {
                    window._closeTimer = new DispatcherTimer { Interval = duration };
                    window._closeTimer.Tick += (_, _) => window.SafeClose();
                    window._closeTimer.Start();
                }
            }
            catch
            {
                window.SafeClose();
            }
        }

        if (notificationRequest is null)
            window._showTimer = DispatcherTimer.RunOnce(ShowContent, MaskDuration);
        else
        {
            window._hostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            window._hostTimer.Tick += (_, _) =>
            {
                if (window._closing) return;
                if (notificationRequest.CancellationToken.IsCancellationRequested
                    || notificationRequest.CompletedToken.IsCancellationRequested)
                    window.SafeClose();
                else if (NotificationPresentationLifetime.IsContentPlaying(notificationRequest))
                    ShowContent();
                else if (window.IsVisible)
                {
                    window.StopSlide();
                    window._placementTimer?.Stop();
                    window.Hide();
                }
            };
            window._hostTimer.Start();
        }

        return window;
    }

    private void ApplyHostSurface()
    {
        var backdrop = this.FindControl<Border>("SurfaceBackdrop")!;
        if (ClassIslandSurface.Find() is { } host)
        {
            RequestedThemeVariant = host.Window.ActualThemeVariant;
            backdrop.Background = host.Background.Background;
            backdrop.Opacity = host.Background.Opacity;
            this.FindControl<Border>("SurfaceFrame")!.BorderBrush = host.Background.BorderBrush;
            TransparencyLevelHint = host.Window.TransparencyLevelHint;
        }
        if (_performanceMode)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.None];
            backdrop.Opacity = 1;
        }
    }

    /// <summary>紧贴宿主可见岛的下沿；宿主不可见时退回屏幕顶部。</summary>
    private void PlaceOnScreen()
    {
        var host = ClassIslandSurface.Find();
        var origin = host is { } surface
            ? (PixelPoint?)ClassIslandSurface.BottomCenter(surface.Background)
            : null;
        var screen = origin is { } point ? Screens.ScreenFromPoint(point) : Screens.ScreenFromWindow(this);
        screen ??= Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        MaxWidth = Math.Min(520, area.Width / scale);
        var width = (int)Math.Round(Bounds.Width * scale);
        var center = origin?.X ?? area.X + area.Width / 2;
        var top = origin?.Y ?? area.Y + (int)Math.Round(TopBarClearanceDip * scale);
        top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Bottom - 100));
        MaxHeight = Math.Max(100, (area.Bottom - top) / scale);
        _anchor = new PixelPoint(Math.Clamp(center - width / 2, area.X, Math.Max(area.X, area.Right - width)), top);
        Position = _anchor;
    }

    private void AnimateIn()
    {
        if (_performanceMode) return;
        var card = this.FindControl<Border>("Card")!;
        _drawerHeight = card.Bounds.Height;
        if (_drawerHeight <= 0) return;
        // Freeze the content's layout while only the clipped viewport changes height.
        card.Height = _drawerHeight;
        SetDrawerProgress(0);
        AnimateDrawer(1, SlideDuration);
    }

    private void SetDrawerProgress(double progress)
    {
        _drawerProgress = progress;
        this.FindControl<Panel>("DrawerViewport")!.Height = Math.Max(1, _drawerHeight * progress);
        this.FindControl<Border>("Card")!.RenderTransform =
            new TranslateTransform(0, -_drawerHeight * (1 - progress));
    }

    private void AnimateDrawer(double target, TimeSpan duration)
    {
        StopSlide();
        var start = _drawerProgress;
        var clock = Stopwatch.StartNew();
        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _slideTimer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            SetDrawerProgress(start + (target - start) * SlideEasing.Ease(progress));
            if (progress >= 1)
            {
                StopSlide();
                if (target == 1)
                {
                    // Let late images and send-status text resize the card after entry.
                    this.FindControl<Panel>("DrawerViewport")!.Height = double.NaN;
                    var card = this.FindControl<Border>("Card")!;
                    card.Height = double.NaN;
                    card.RenderTransform = null;
                }
            }
        };
        _slideTimer.Start();
    }

    private void StopSlide()
    {
        _slideTimer?.Stop();
        _slideTimer = null;
    }

    /// <summary>向岛下沿收回抽屉，动画结束后关闭窗口，可安全地重复调用。</summary>
    internal void SafeClose()
    {
        _closeTimer?.Stop();
        _closeTimer = null;
        if (_closing) return;
        _closing = true;
        _notificationLifetime?.Cancel();
        _hostTimer?.Stop();
        _showTimer?.Dispose();
        StopSlide();

        var card = this.FindControl<Border>("Card");
        if (card is null || _performanceMode)
        {
            Close();
            return;
        }

        _drawerHeight = card.Bounds.Height;
        card.Height = _drawerHeight;
        if (_drawerHeight > 0) AnimateDrawer(0, ExitDuration);
        _exitTimer = DispatcherTimer.RunOnce(Close, ExitDuration);
    }

    private bool _closing;
}
