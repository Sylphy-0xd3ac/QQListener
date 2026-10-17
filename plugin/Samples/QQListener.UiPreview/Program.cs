using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using QQListener.Views;
using QQListener.Models;
using QQListener.Pipes;
using QQListener.Protocol;
using QQListener.Views.SettingsPages;
using QQListener.Services;
using QQListener.Views.Windows;

namespace QQListener.UiPreview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var native = args.Length > 0 && args[0] == "--native-check";
        var automation = args.Contains("--automation-check");
        var render = native || automation || args.Length > 0 && args[0] == "--render";
        var app = AppBuilder.Configure<PreviewApplication>();
        if (!render) return app.UsePlatformDetect().StartWithClassicDesktopLifetime(args);

        var directory = Path.GetFullPath(args.Length > 1 ? args[1] : "ui-previews");
        Directory.CreateDirectory(directory);
        if (native)
        {
            app.UsePlatformDetect().SetupWithoutStarting();
            var window = QqMessageWindow.ShowMessage(
                MessagePreviewSamples.Create(MessagePreviewKind.Quote, MessageImportance.Normal), TimeSpan.FromMinutes(1));
            Pump(TimeSpan.FromSeconds(3));
            Console.WriteLine($"Native transparency: {window.ActualTransparencyLevel}");
            Console.WriteLine($"Native corners clipped: {window.IsNativeBackdropClipped}, handle: {window.TryGetPlatformHandle()?.HandleDescriptor}");
            if (OperatingSystem.IsMacOS() && !window.IsNativeBackdropClipped)
                throw new InvalidOperationException("原生模糊层未裁切圆角");
            if (window.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur &&
                window.ActualTransparencyLevel != WindowTransparencyLevel.Blur)
                throw new InvalidOperationException("系统未启用背景模糊");
            // 屏幕录制权限并非原生材质测试的前提；按需另外请求屏幕截图。
            if (OperatingSystem.IsMacOS() && args.Contains("--capture"))
            {
                var scale = window.Screens.ScreenFromWindow(window)?.Scaling ?? 1;
                var region = $"{(int)(window.Position.X / scale)},{(int)(window.Position.Y / scale)}," +
                             $"{(int)window.Bounds.Width},{(int)window.Bounds.Height}";
                Console.WriteLine($"Native window: position={window.Position}, bounds={window.Bounds}, scale={scale}, capture={region}");
                var capture = new System.Diagnostics.ProcessStartInfo("/usr/sbin/screencapture");
                foreach (var argument in new[] { "-x", "-R", region, Path.Combine(directory, "Native.png") })
                    capture.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(capture)!;
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    window.Close();
                    throw new InvalidOperationException("原生截图失败");
                }
            }
            window.Close();
            return 0;
        }
        app.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        VerifyAutomationCompatibility();
        if (automation) return 0;

        foreach (var kind in Enum.GetValues<MessagePreviewKind>())
        {
            var window = QqMessageWindow.ShowMessage(
                MessagePreviewSamples.Create(kind, MessageImportance.Normal), TimeSpan.FromMinutes(1));
            Pump(TimeSpan.FromSeconds(3));
            WaitForDrawer(window);
            if (window.FindControl<TextBlock>("Identifiers") is not null)
                throw new InvalidOperationException("号码组件必须删除");
            if (kind != MessagePreviewKind.Text &&
                !window.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null))
                throw new InvalidOperationException("内置图片未渲染");
            using var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("未生成预览帧");
            frame.Save(Path.Combine(directory, kind + ".png"));

            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            if (scroll.Extent.Height > scroll.Viewport.Height)
            {
                scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
                Dispatcher.UIThread.RunJobs();
                using var scrolled = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("未生成滚动预览帧");
                scrolled.Save(Path.Combine(directory, kind + "-scrolled.png"));
                if (scroll.Offset.Y <= 0) throw new InvalidOperationException("长消息无法滚动");
            }

            if (window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "新消息"))
                throw new InvalidOperationException("不应再显示重复的标题行");
            if (window.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Content as string is "关闭" or "查看号码" or "隐藏号码"))
                throw new InvalidOperationException("不应再显示底部操作栏");
            window.Close();
            Console.WriteLine($"{kind}: image / no header or footer OK");
        }
        VerifyPendingDigest(directory);
        VerifyImageDownload();
        VerifyInteractionLifetime();
        VerifyScaledIslandAnchor();
        VerifyReplyActions(directory);
        VerifyThemesAndSettings(directory);
        return 0;
    }

    private static void VerifyAutomationCompatibility()
    {
        // Exercise the same peer called by the native macOS accessibility bridge.
        var empty = new ItemsRepeater();
        try
        {
            new ItemsRepeaterAutomationPeer(empty).GetChildren();
            throw new InvalidOperationException("空列表未复现预期的上游空引用，需重新核对兼容保护");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("Reproduced FluentAvalonia empty repeater accessibility crash");
        }
        ItemsRepeaterAutomationCompatibility.Install();
        ItemsRepeaterAutomationCompatibility.Install();
        if (new ItemsRepeaterAutomationPeer(empty).GetChildren().Count != 0)
            throw new InvalidOperationException("空列表辅助功能子项不为空");
        var repeater = new ItemsRepeater
        {
            ItemsSource = new Control[] { new Button { Content = "first" }, new Button { Content = "second" } }
        };
        var window = new Window { Width = 680, Height = 800, Content = repeater };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(100));
        var childCount = new ItemsRepeaterAutomationPeer(repeater).GetChildren().Count;
        // Virtualization may only realize the first row in the headless viewport.
        if (childCount < 1 || childCount != repeater.GetVisualChildren().Count())
            throw new InvalidOperationException($"非空列表辅助功能被破坏: {childCount}, visuals={repeater.GetVisualChildren().Count()}, bounds={repeater.Bounds}");
        repeater.ItemsSource = Array.Empty<string>();
        Pump(TimeSpan.FromMilliseconds(100));
        if (new ItemsRepeaterAutomationPeer(repeater).GetChildren().Count != 0)
            throw new InvalidOperationException("清空列表后辅助功能仍有子项");
        window.Content = new QqListenerSettingsPage();
        Pump(TimeSpan.FromMilliseconds(200));
        void Traverse(AutomationPeer peer)
        {
            foreach (var child in peer.GetChildren()) Traverse(child);
        }
        Traverse(ControlAutomationPeer.CreatePeerForElement(window)!);
        foreach (var expander in window.GetVisualDescendants().OfType<SettingsExpander>())
            expander.IsExpanded = true;
        Pump(TimeSpan.FromMilliseconds(400));
        Traverse(ControlAutomationPeer.CreatePeerForElement(window)!);
        window.Close();
        Console.WriteLine("Accessibility: empty / populated / cleared lists and collapsed / expanded settings OK");
    }

    private static void VerifyScaledIslandAnchor()
    {
        var background = new Border
        {
            Width = 240, Height = 40, Background = Brushes.Gray,
            RenderTransform = new ScaleTransform(0.65, 0.65),
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute)
        };
        var canvas = new Canvas();
        Canvas.SetLeft(background, 40);
        Canvas.SetTop(background, 20);
        canvas.Children.Add(background);
        var host = new Window { Width = 500, Height = 200, Content = canvas, Position = new PixelPoint(120, 100) };
        host.Show();
        Pump(TimeSpan.FromMilliseconds(100));
        void Verify()
        {
            var left = background.PointToScreen(new Point(0, 0));
            var right = background.PointToScreen(new Point(background.Bounds.Width, background.Bounds.Height));
            var anchor = ClassIslandSurface.BottomCenter(background);
            if (Math.Abs(anchor.X - (left.X + right.X) / 2.0) > 1 || anchor.Y != right.Y)
                throw new InvalidOperationException("宿主缩放后抽屉锚点未对齐通知栏中心");
            var oldCenter = left.X + background.Bounds.Width * host.RenderScaling / 2;
            if (Math.Abs(oldCenter - anchor.X) < 10)
                throw new InvalidOperationException("测试未覆盖宿主缩放造成的中心偏移");
        }
        Verify();
        host.Position = new PixelPoint(210, 140);
        background.Width = 320;
        Pump(TimeSpan.FromMilliseconds(100));
        Verify();
        host.Close();
        Console.WriteLine("Island anchor: visual zoom / moved window / resized bar centered OK");
    }

    private static void WaitForDrawer(QqMessageWindow window)
    {
        for (var i = 0; i < 30 && window.FindControl<Border>("Card")!.RenderTransform is not null; i++)
            Pump(TimeSpan.FromMilliseconds(50));
        Pump(TimeSpan.FromMilliseconds(100));
        var card = window.FindControl<Border>("Card")!;
        var viewport = window.FindControl<Panel>("DrawerViewport")!;
        if (card.RenderTransform is not null || viewport.Bounds.Height + 1 < card.Bounds.Height)
            throw new InvalidOperationException("抽屉展开后仍裁切正文");
    }

    private static void VerifyThemesAndSettings(string directory)
    {
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            var sample = MessagePreviewSamples.Create(MessagePreviewKind.Quote, MessageImportance.Normal) with
            {
                SourcePid = 1, Scene = "group", RawSeq = 42
            };
            var message = QqMessageWindow.ShowMessage(sample, TimeSpan.FromSeconds(30));
            Pump(TimeSpan.FromSeconds(3));
            WaitForDrawer(message);
            using (var frame = message.CaptureRenderedFrame()!)
                frame.Save(Path.Combine(directory, $"Reply-{theme}.png"));
            var position = message.Position;
            var height = message.Bounds.Height;
            message.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "取消")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Pump(TimeSpan.FromMilliseconds(100));
            if (message.IsVisible && (message.Bounds.Height >= height || message.Position.Y != position.Y))
                throw new InvalidOperationException("抽屉应保持上沿固定，向上收回");
            Pump(TimeSpan.FromMilliseconds(300));
            if (message.IsVisible) throw new InvalidOperationException("取消后未收起通知");
            var settings = new Window { Width = 680, Height = 800, Content = new QqListenerSettingsPage() };
            settings.Show();
            Pump(TimeSpan.FromMilliseconds(300));
            using (var frame = settings.CaptureRenderedFrame()!)
                frame.Save(Path.Combine(directory, $"Settings-{theme}.png"));
            settings.Close();
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Console.WriteLine("Light/dark message and settings previews OK");
    }

    private static void VerifyReplyActions(string directory)
    {
        var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal) with
        {
            SourcePid = 1, Scene = "group", RawSeq = 42
        };
        ReplyTransport? transport = null;
        var fail = false;
        var service = new MessageReplyService(_ => transport = new ReplyTransport(fail));
        using var bar = new MessageReplyBar(message, "老师，已收到", service);
        var dismissals = 0;
        bar.DismissRequested += () => dismissals++;
        var window = new Window { Width = 400, Content = bar };
        window.Show();
        Button Button(string text) => bar.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == text);
        Button("取消").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        if (dismissals != 1 || transport is not null)
            throw new InvalidOperationException("取消不应发送消息");
        fail = true;
        Button("确定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(300));
        if (dismissals != 1 || !Button("确定").IsEnabled ||
            !bar.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "发送失败，请重试"))
            throw new InvalidOperationException("QQ 拒绝发送时必须保留重试入口");
        fail = false;
        Button("确定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(300));
        if (dismissals != 2 || transport is null)
            throw new InvalidOperationException("确认成功后应收起提醒");
        var request = new FrameReader().Push(transport.Request).Single();
        var fields = Wire.DecodeFields(request.Body);
        var body = Wire.FieldBytes(fields, 3);
        var rich = Wire.FieldBytes(Wire.DecodeFields(body), 1);
        var segments = RichText.ParseElems(rich, true);
        if (MessageText.Render(segments.Where(seg => seg.Type == "text")) != "老师，已收到" ||
            segments.FirstOrDefault(seg => seg.Type == "reply")?.Get<ulong>("seq") != 42)
            throw new InvalidOperationException("确定必须发送配置文本并引用原消息");
        window.Close();
        Console.WriteLine("Reply: cancel / server rejection / retry / configured text / quote OK (mock pipe)");
    }

    private sealed class ReplyTransport : IPipeTransport
    {
        private readonly byte[] _response;
        private int _offset;
        public byte[] Request { get; private set; } = [];
        public ReplyTransport(bool fail)
        {
            _response = Wire.Concat(Hqp1.EncodeFrame(PipeOp.Hello),
                Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
                Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1,
                    body: Wire.EncodeVarintField(1, fail ? 1UL : 0UL)));
        }
        public int Read(Span<byte> buffer)
        {
            var length = Math.Min(buffer.Length, _response.Length - _offset);
            _response.AsSpan(_offset, length).CopyTo(buffer);
            _offset += length;
            return length;
        }
        public void Write(ReadOnlySpan<byte> buffer) => Request = buffer.ToArray();
        public void Dispose() { }
    }

    private static void VerifyPendingDigest(string directory)
    {
        var media = MessagePreviewSamples.Create(MessagePreviewKind.Media, MessageImportance.Normal);
        var quote = MessagePreviewSamples.Create(MessagePreviewKind.Quote, MessageImportance.Important) with
        {
            Sender = "李四", Conversation = "另一个群", SourcePid = 1, Scene = "group", RawSeq = 42
        };
        var digest = PendingMessageDigest.Create([media, quote], 5)!;
        var window = QqMessageWindow.ShowMessage(digest, TimeSpan.FromMinutes(1));
        Pump(TimeSpan.FromSeconds(3));
        try
        {
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
            if (!texts.Contains("高三2班 / 张三") || !texts.Contains("另一个群 / 李四")
                || !texts.Contains("引用消息") || !texts.Contains("课堂材料.pdf")
                || !texts.Contains("回复最后一条：另一个群 / 李四"))
                throw new InvalidOperationException("积压摘要丢失发送者、附件、引用或回复目标");
            if (window.GetVisualDescendants().OfType<Image>().Count(i => i.Source is not null) != 2)
                throw new InvalidOperationException("摘要中的正文图片与引用图片必须都能显示");
            using var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("摘要未生成预览帧");
            frame.Save(Path.Combine(directory, "PendingDigest.png"));
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            Dispatcher.UIThread.RunJobs();
            using var bottom = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("摘要未生成滚动预览帧");
            bottom.Save(Path.Combine(directory, "PendingDigest-scrolled.png"));
        }
        finally { window.Close(); }
        Console.WriteLine("Pending digest: ordered messages / images / quote / attachment / explicit reply target OK");
    }

    private static void Pump(TimeSpan duration)
    {
        using var deadline = new CancellationTokenSource(duration);
        Dispatcher.UIThread.MainLoop(deadline.Token);
    }

    private static void VerifyInteractionLifetime()
    {
        var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal);
        var pending = QqMessageWindow.ShowMessage(message, TimeSpan.FromSeconds(1));
        pending.Close();
        Pump(TimeSpan.FromSeconds(2.3));
        if (pending.IsVisible) throw new InvalidOperationException("已关闭的延迟窗口重新出现");

        var window = QqMessageWindow.ShowMessage(message, TimeSpan.FromSeconds(1));
        Pump(TimeSpan.FromSeconds(2.3));
        if (!window.IsVisible) throw new InvalidOperationException("计时开始前窗口未显示");
        var point = new Point(30, 30);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseWheel(point, new Vector(0, -1));
        window.Focus();
        Pump(TimeSpan.FromSeconds(1.3));
        if (window.IsVisible) throw new InvalidOperationException("交互不应阻止到时自动关闭");
        Console.WriteLine("Window: pending close / automatic dismissal after interaction OK");
    }

    private static void VerifyImageDownload()
    {
        using var resource = AssetLoader.Open(new Uri("avares://QQListener/icon.png"));
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        using var client = new HttpClient(new ImageHandler(bytes.ToArray()));
        var directory = Path.Combine(Path.GetTempPath(), "qq-ui-preview-" + Guid.NewGuid().ToString("N"));
        var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal) with
        {
            Segments = [new QQListener.Protocol.Segment { Type = "image", Name = "图片", Url = "https://example.invalid/image" }]
        };
        using var view = new MessageContentView(message, directory, new MessageAttachmentDownloader(client));
        var window = new Window { Content = view, Width = 520, Height = 360 };
        try
        {
            window.Show();
            Pump(TimeSpan.FromSeconds(2));
            if (!view.GetVisualDescendants().OfType<Image>().Any(i => i.Source is not null))
                throw new InvalidOperationException("图片未自动下载显示");
            if (!view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "已保存到下载目录"))
                throw new InvalidOperationException("下载完成状态丢失");
            if (Directory.GetFiles(directory, "*.png").Length != 1)
                throw new InvalidOperationException("图片未保存为可打开的文件");
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("Attachment: automatic image / completed status / saved file OK (mock HTTP)");
    }

    private sealed class ImageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}

public sealed class PreviewApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentAvaloniaTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var content = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
            content.Children.Add(new TextBlock
            {
                Text = "QQListener 消息窗口预览", FontSize = 22, Foreground = Brushes.White
            });
            content.Children.Add(new TextBlock
            {
                Text = "使用插件实际的消息窗口与内置样例；此处不展示 ClassIsland 顶栏。",
                TextWrapping = TextWrapping.Wrap
            });
            foreach (var kind in Enum.GetValues<MessagePreviewKind>())
            {
                var button = new Button { Content = kind.ToString() };
                button.Click += (_, _) => QqMessageWindow.ShowMessage(
                    MessagePreviewSamples.Create(kind, MessageImportance.Normal), TimeSpan.FromSeconds(30));
                content.Children.Add(button);
            }
            desktop.MainWindow = new Window
            {
                Title = "QQListener UI Preview", Width = 540, Height = 340, Content = content
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
