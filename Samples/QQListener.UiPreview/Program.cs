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
        var sessionCheck = args.Contains("--session-check");
        var render = native || automation || sessionCheck || args.Length > 0 && args[0] == "--render";
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
        if (sessionCheck)
        {
            VerifyNotificationSessions();
            VerifyPreviewNotifications();
            return 0;
        }
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
        VerifyOptionalFileDownload();
        VerifyInteractionLifetime();
        VerifyNotificationCancellation();
        VerifyHostNotificationTiming();
        VerifyNotificationQueue();
        VerifyNotificationSessions();
        VerifyPreviewNotifications();
        VerifyScaledIslandAnchor();
        VerifyReplyActions(directory);
        VerifyReactionActions();
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

            var pmhqSettings = new QqListenerSettings { Engine = ListenerEngine.PMHQ };
            var page = new QqListenerSettingsPage(pmhqSettings, new QqListenerDiagnosticsState(), QqListenerPluginInfo.Designer);
            var experimental = new Window { Width = 680, Height = 800, Content = page };
            experimental.Show();
            Pump(TimeSpan.FromMilliseconds(300));
            var selector = experimental.GetVisualDescendants().OfType<ComboBox>()
                .Single(combo => combo.Items.Cast<object>().Any(item => item as string == "PMHQ（实验性）"));
            if (selector.SelectedIndex != 1) throw new InvalidOperationException("引擎选择未恢复为 PMHQ");
            var engine = experimental.GetVisualDescendants().OfType<SettingsExpander>()
                .Single(item => item.Header as string == "消息引擎");
            engine.IsExpanded = true;
            Pump(TimeSpan.FromMilliseconds(300));
            engine.BringIntoView();
            Pump(TimeSpan.FromMilliseconds(300));
            if (!experimental.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text?.StartsWith("实验性功能：", StringComparison.Ordinal) == true))
                throw new InvalidOperationException("PMHQ 实验性警告不可见");
            using (var frame = experimental.CaptureRenderedFrame()!)
                frame.Save(Path.Combine(directory, $"Settings-PMHQ-{theme}.png"));
            experimental.Close();
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
        using var bar = new MessageReplyBar(message, "老师，已收到", service, replyOnConfirmEnabled: true);
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
        transport = null;
        using var optionalBar = new MessageReplyBar(message, "收到", service);
        var optionalDismissals = 0;
        optionalBar.DismissRequested += () => optionalDismissals++;
        var optionalWindow = new Window { Content = optionalBar };
        optionalWindow.Show();
        optionalBar.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "确定")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        if (optionalDismissals != 1 || transport is not null)
            throw new InvalidOperationException("关闭确认回复时，确定只能收起提醒，不能发消息");
        optionalWindow.Close();
        Console.WriteLine("Reply: cancel / server rejection / retry / configured text / quote OK (mock pipe)");
    }

    private static void VerifyReactionActions()
    {
        var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal) with
        {
            SourcePid = 1, Scene = "group", RawSeq = 42, ConversationId = "123456"
        };
        ReplyTransport? transport = null;
        var fail = true;
        var sends = 0;
        var service = new MessageReplyService(_ =>
        {
            sends++;
            return transport = new ReplyTransport(fail, reaction: true);
        });
        using var bar = new MessageReplyBar(message, replyService: service,
            replyOnConfirmEnabled: true, replyWithOkReaction: true);
        var dismissals = 0;
        bar.DismissRequested += () => dismissals++;
        var window = new Window { Width = 400, Content = bar };
        window.Show();
        Button Button(string text) => bar.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Content as string == text);
        Button("取消").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        if (dismissals != 1 || sends != 0)
            throw new InvalidOperationException("取消不能添加表情回应");
        Button("确定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(300));
        if (dismissals != 1 || sends != 1 || !Button("确定").IsEnabled || !Button("取消").IsEnabled
            || !bar.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "发送失败，请重试"))
            throw new InvalidOperationException("表情回应被拒绝时必须保留重试入口");
        fail = false;
        Button("确定").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(300));
        if (dismissals != 2 || sends != 2 || transport is null)
            throw new InvalidOperationException("表情回应成功后应收起提醒");
        var request = new FrameReader().Push(transport.Request).Single();
        var body = Wire.DecodeFields(Wire.FieldBytes(Wire.DecodeFields(request.Body), 4));
        if (request.Cmd != QQListener.Oidb.MessageReaction.Command || Wire.FieldInt(body, 2) != 123456
            || Wire.FieldInt(body, 3) != 42 || Wire.FieldString(body, 4) != "124")
            throw new InvalidOperationException("确定必须回应原群消息，不能发送普通表情消息");
        window.Close();
        foreach (var enabled in new[] { false, true })
        {
            var target = enabled ? message with { Scene = "c2c" } : message;
            using var optionalBar = new MessageReplyBar(target, replyService: service,
                replyOnConfirmEnabled: enabled, replyWithOkReaction: true);
            var optionalDismissals = 0;
            optionalBar.DismissRequested += () => optionalDismissals++;
            var optionalWindow = new Window { Content = optionalBar };
            optionalWindow.Show();
            if (enabled && !optionalBar.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text?.Contains("仅支持群消息") == true))
                throw new InvalidOperationException("私聊必须说明不支持表情回应");
            optionalBar.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "确定")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            if (optionalDismissals != 1 || sends != 2)
                throw new InvalidOperationException("关闭回复或私聊时不能添加回应或退回发送文本");
            optionalWindow.Close();
        }
        Console.WriteLine("Reaction: original group / sequence / OK face / cancellation / rejection / retry / private no-send OK (mock pipe)");
    }

    private sealed class ReplyTransport : IPipeTransport
    {
        private readonly byte[] _response;
        private int _offset;
        public byte[] Request { get; private set; } = [];
        public ReplyTransport(bool fail, bool reaction = false)
        {
            _response = Wire.Concat(Hqp1.EncodeFrame(PipeOp.Hello),
                Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
                Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1,
                    body: Wire.Concat(Wire.EncodeVarintField(reaction ? 3 : 1, fail ? 1UL : 0UL),
                        !reaction && !fail ? Wire.EncodeVarintField(11, 88) : [])));
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
        var window = QqMessageWindow.ShowMessage(digest, TimeSpan.FromMinutes(1), replyOnConfirmEnabled: true);
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

    private static void VerifyOptionalFileDownload()
    {
        foreach (var autoDownload in new[] { false, true })
        {
            var directory = Path.Combine(Path.GetTempPath(), "qq-file-preview-" + Guid.NewGuid().ToString("N"));
            var handler = new FileHandler();
            using var client = new HttpClient(handler);
            var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal) with
            {
                Segments = [new Segment { Type = "file", Name = "课堂材料.txt", Url = "https://example.invalid/file" }]
            };
            using var view = new MessageContentView(message, directory, new MessageAttachmentDownloader(client), autoDownload);
            var window = new Window { Content = view };
            try
            {
                window.Show();
                Pump(TimeSpan.FromMilliseconds(200));
                if (!autoDownload)
                {
                    if (handler.Requests != 0 || Directory.Exists(directory))
                        throw new InvalidOperationException("关闭自动下载时不能自动请求或保存文件");
                    view.GetVisualDescendants().OfType<Button>().Single()
                        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                }
                Pump(TimeSpan.FromMilliseconds(100));
                view.Dispose();
                window.Close();
                handler.Complete();
                Pump(TimeSpan.FromMilliseconds(400));
                if (handler.Requests != 1 || Directory.GetFiles(directory, "*.txt").Length != 1)
                    throw new InvalidOperationException("文件应按开关自动或手动下载，且关闭窗口后继续保存");
            }
            finally
            {
                window.Close();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
        Console.WriteLine("Attachment: opt-in file download / manual download / continue after close OK (mock HTTP)");
    }

    private static void VerifyNotificationCancellation()
    {
        var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal);
        var pendingRequest = new ClassIsland.Core.Models.Notification.NotificationRequest();
        var pending = QqMessageWindow.ShowMessage(message, TimeSpan.FromMinutes(1), notificationRequest: pendingRequest);
        pendingRequest.Cancel();
        Pump(TimeSpan.FromSeconds(2.3));
        if (pending.IsVisible)
            throw new InvalidOperationException("顶栏取消后不能再弹出延迟浮窗");

        var request = new ClassIsland.Core.Models.Notification.NotificationRequest();
        var window = QqMessageWindow.ShowMessage(message, TimeSpan.FromMinutes(1), notificationRequest: request);
        StartHostOverlay(request);
        Pump(TimeSpan.FromMilliseconds(500));
        if (!window.Topmost || !window.IsVisible)
            throw new InvalidOperationException("消息浮窗必须置顶显示");
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "取消")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(500));
        if (window.IsVisible || !request.CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("取消必须同时关闭浮窗并中断对应的 ClassIsland 通知");
        Console.WriteLine("Notification: topmost / pending cancellation / cancel floating window and host request OK");
    }

    private static void SetHostState(ClassIsland.Core.Models.Notification.NotificationRequest request,
        ClassIsland.Core.Enums.Notification.NotificationState state) =>
        request.GetType().GetProperty(nameof(request.State))!.SetValue(request, state);

    private static void StartHostOverlay(ClassIsland.Core.Models.Notification.NotificationRequest request)
    {
        request.OverlaySession.GetType().GetProperty(nameof(request.OverlaySession.SessionStartTime))!
            .SetValue(request.OverlaySession, DateTime.Now);
        SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Playing);
    }

    private static void CompleteHostNotification(ClassIsland.Core.Models.Notification.NotificationRequest request)
    {
        var source = (CancellationTokenSource)request.GetType().GetProperty("CompletedTokenSource",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(request)!;
        source.Cancel();
    }

    private static void VerifyHostNotificationTiming()
    {
        var message = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal);
        var request = new ClassIsland.Core.Models.Notification.NotificationRequest
        {
            OverlayContent = new() { Duration = TimeSpan.FromMilliseconds(100) }
        };
        var window = QqMessageWindow.ShowMessage(message, TimeSpan.FromMilliseconds(100),
            performanceMode: true, notificationRequest: request);
        SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Queued);
        Pump(TimeSpan.FromSeconds(2.2));
        if (window.IsVisible) throw new InvalidOperationException("宿主排队时不能提前显示浮窗");
        SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Playing);
        Pump(TimeSpan.FromMilliseconds(150));
        if (window.IsVisible) throw new InvalidOperationException("宿主遮罩阶段不能提前显示正文");
        StartHostOverlay(request);
        Pump(TimeSpan.FromMilliseconds(500));
        if (!window.IsVisible || request.CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("浮窗应跟随宿主正文，不得按独立计时提前关闭");
        SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Paused);
        Pump(TimeSpan.FromMilliseconds(150));
        if (window.IsVisible || request.CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("宿主被抢占时应隐藏浮窗，不能取消待恢复通知");
        StartHostOverlay(request);
        Pump(TimeSpan.FromMilliseconds(150));
        if (!window.IsVisible) throw new InvalidOperationException("宿主恢复后应继续显示浮窗");
        CompleteHostNotification(request);
        Pump(TimeSpan.FromMilliseconds(150));
        if (window.IsVisible) throw new InvalidOperationException("宿主完成后必须收起浮窗");
        Console.WriteLine("Notification: host queue / mask / actual overlay / pause / resume / completion OK (mock host)");
    }

    private static void VerifyNotificationQueue()
    {
        var settings = new QqListenerSettings { PerformanceMode = true };
        using var notifier = new ClassIslandMessageNotifier(settings);
        var requests = new List<ClassIsland.Core.Models.Notification.NotificationRequest>();
        void Show(ClassIsland.Core.Models.Notification.NotificationRequest request)
        {
            requests.Add(request);
            SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Queued);
        }
        var first = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal);
        notifier.ShowAsync(first, Show, default).GetAwaiter().GetResult();
        notifier.ShowAsync(first with { Body = "后续普通消息", ConversationId = "10001" }, Show, default).GetAwaiter().GetResult();
        notifier.ShowAsync(first with { Importance = MessageImportance.Important, Body = "重要消息" }, Show, default).GetAwaiter().GetResult();
        if (requests.Count != 1 || requests[0].CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("后续消息应排队，不得挤掉当前提醒或抢先提交宿主");
        requests[0].Cancel();
        Pump(TimeSpan.FromMilliseconds(400));
        if (requests.Count != 2 || requests[1].OverlayContent!.Duration != TimeSpan.FromSeconds(settings.ImportantDurationSeconds))
            throw new InvalidOperationException("当前提醒结束后应优先显示重要消息");
        CompleteHostNotification(requests[1]);
        Pump(TimeSpan.FromMilliseconds(400));
        if (requests.Count != 3) throw new InvalidOperationException("重要消息完成后应继续显示普通消息");
        notifier.ShowAsync(first, Show, default).GetAwaiter().GetResult();
        settings.IsEnabled = false;
        Pump(TimeSpan.FromMilliseconds(400));
        if (!requests[2].CancellationToken.IsCancellationRequested || requests.Count != 3)
            throw new InvalidOperationException("暂停监听时必须收起当前提醒并清除旧等待队列");
        Console.WriteLine("Notification queue: no eviction / important first / continue after cancellation / clear on pause OK");
    }

    private static void VerifyNotificationSessions()
    {
        var router = new Core.EngineCommandRouter();
        var api = Oidb.QqControlApi.OverRequests((_, _, _) => []);
        var firstSession = Guid.NewGuid();
        var secondSession = Guid.NewGuid();
        router.Bind(firstSession, api);
        using var notifier = new ClassIslandMessageNotifier(new QqListenerSettings { PerformanceMode = true }, engines: router);
        var calls = 0;
        void Show(ClassIsland.Core.Models.Notification.NotificationRequest request)
        {
            calls++;
            request.Cancel();
        }
        var old = MessagePreviewSamples.Create(MessagePreviewKind.Text, MessageImportance.Normal)
            with { EngineSession = firstSession };
        notifier.ShowAsync(old, Show, default).GetAwaiter().GetResult();
        if (calls != 1) throw new InvalidOperationException("当前会话消息应正常提交宿主");

        // A poll result is already in the worker when the engine/account changes.
        router.Bind(secondSession, api);
        notifier.ShowAsync(old, Show, default).GetAwaiter().GetResult();
        notifier.ShowAsync(PendingMessageDigest.Create([old, old])!, Show, default).GetAwaiter().GetResult();
        var current = old with { EngineSession = secondSession };
        notifier.ShowAsync(PendingMessageDigest.Create([current, old])!, Show, default).GetAwaiter().GetResult();
        if (calls != 1) throw new InvalidOperationException("旧批次、旧摘要和混合会话摘要不得显示或朗读");
        notifier.ShowAsync(PendingMessageDigest.Create([current, current])!, Show, default).GetAwaiter().GetResult();
        if (calls != 2) throw new InvalidOperationException("当前会话摘要应正常提交宿主");

        // ShowAsync started on a worker, but its UI continuation has not run yet.
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = Task.Run(async () =>
        {
            var show = notifier.ShowAsync(current, Show, default);
            posted.SetResult();
            await show;
        });
        posted.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        router.Bind(Guid.NewGuid(), api);
        Pump(TimeSpan.FromMilliseconds(150));
        delayed.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (calls != 2) throw new InvalidOperationException("等待 UI 调度期间失效的消息不得显示");

        // A synchronous host callback may itself change the session.
        var reentrant = old with { EngineSession = Guid.NewGuid() };
        router.Bind(reentrant.EngineSession, api);
        ClassIsland.Core.Models.Notification.NotificationRequest? last = null;
        notifier.ShowAsync(reentrant, request =>
        {
            last = request;
            SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Queued);
            router.Bind(Guid.Empty, null);
        }, default).GetAwaiter().GetResult();
        if (last?.CancellationToken.IsCancellationRequested != true)
            throw new InvalidOperationException("宿主回调内切换会话必须取消旧通知，不得再创建浮窗");

        // Let an active window close before the notifier's session-clear callback runs.
        var queuedRouter = new Core.EngineCommandRouter();
        queuedRouter.Bind(firstSession, api);
        var requests = new List<ClassIsland.Core.Models.Notification.NotificationRequest>();
        queuedRouter.SessionChanged += () =>
        {
            requests[0].Cancel();
            Pump(TimeSpan.FromMilliseconds(250));
        };
        using var queuedNotifier = new ClassIslandMessageNotifier(new QqListenerSettings { PerformanceMode = true }, engines: queuedRouter);
        void Queue(ClassIsland.Core.Models.Notification.NotificationRequest request)
        {
            requests.Add(request);
            SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Queued);
        }
        queuedNotifier.ShowAsync(old, Queue, default).GetAwaiter().GetResult();
        queuedNotifier.ShowAsync(PendingMessageDigest.Create([old, old])!, Queue, default).GetAwaiter().GetResult();
        queuedRouter.Bind(secondSession, api);
        if (requests.Count != 1)
            throw new InvalidOperationException("实际出队时必须重新检查会话，旧摘要不得提交宿主");
        Console.WriteLine("Notification session: current / stale batch / mixed digest / UI dispatch / dequeue / reentrant switch OK");
    }

    private static void VerifyPreviewNotifications()
    {
        var router = new Core.EngineCommandRouter();
        var settings = new QqListenerSettings { PerformanceMode = true };
        using var notifier = new ClassIslandMessageNotifier(settings, engines: router);
        var calls = 0;
        void Show(ClassIsland.Core.Models.Notification.NotificationRequest request)
        {
            calls++;
            request.Cancel();
        }
        foreach (var connected in new[] { false, true })
        {
            if (connected) router.Bind(Guid.NewGuid(), Oidb.QqControlApi.OverRequests((_, _, _) => []));
            foreach (var kind in Enum.GetValues<MessagePreviewKind>())
            {
                var before = calls;
                notifier.ShowPreviewAsync(kind, MessageImportance.Normal, Show, default).GetAwaiter().GetResult();
                if (calls != before + 1) throw new InvalidOperationException("有无引擎连接均须显示内置测试提醒");
                // A payload claiming to be a preview must not bypass the real-message entry point.
                notifier.ShowAsync(MessagePreviewSamples.Create(kind, MessageImportance.Normal), Show, default).GetAwaiter().GetResult();
                if (calls != before + 1) throw new InvalidOperationException("普通消息入口不能凭空会话或 preview 标签绕过校验");
            }
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                notifier.ShowPreviewAsync(MessagePreviewKind.Text, MessageImportance.Normal, Show, cancelled.Token)
                    .GetAwaiter().GetResult();
                throw new InvalidOperationException("已取消的测试提醒不应继续显示");
            }
            catch (OperationCanceledException) { }
        }
        var requests = new List<ClassIsland.Core.Models.Notification.NotificationRequest>();
        void Queue(ClassIsland.Core.Models.Notification.NotificationRequest request)
        {
            requests.Add(request);
            SetHostState(request, ClassIsland.Core.Enums.Notification.NotificationState.Queued);
        }
        notifier.ShowPreviewAsync(MessagePreviewKind.Text, MessageImportance.Normal, Queue, default).GetAwaiter().GetResult();
        notifier.ShowPreviewAsync(MessagePreviewKind.Media, MessageImportance.Normal, Queue, default).GetAwaiter().GetResult();
        router.Bind(Guid.Empty, null);
        if (requests.Count != 1 || requests[0].CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("引擎会话结束不应中断独立的本地预览");
        requests[0].Cancel();
        Pump(TimeSpan.FromMilliseconds(400));
        if (requests.Count != 2) throw new InvalidOperationException("会话变化后排队预览仍应继续显示");
        settings.IsEnabled = false;
        Pump(TimeSpan.FromMilliseconds(150));
        if (!requests[1].CancellationToken.IsCancellationRequested)
            throw new InvalidOperationException("停止监听仍须取消活动预览");
        Console.WriteLine("Preview notifications: text / media / quote with and without engine; isolated queue / cancellation OK");
    }

    private sealed class FileHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        public void Complete() => _complete.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            await _complete.Task.WaitAsync(cancellationToken);
            var content = new StringContent("课堂文件测试");
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        }
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
