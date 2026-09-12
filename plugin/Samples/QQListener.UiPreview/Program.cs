using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using QQListener.Models;
using QQListener.Services;
using QQListener.Views.Windows;

namespace QQListener.UiPreview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var native = args.Length > 0 && args[0] == "--native-check";
        var render = native || args.Length > 0 && args[0] == "--render";
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

        foreach (var kind in Enum.GetValues<MessagePreviewKind>())
        {
            var window = QqMessageWindow.ShowMessage(
                MessagePreviewSamples.Create(kind, MessageImportance.Normal), TimeSpan.FromMinutes(1));
            Pump(TimeSpan.FromSeconds(3));
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
        return 0;
    }

    private static void VerifyPendingDigest(string directory)
    {
        var media = MessagePreviewSamples.Create(MessagePreviewKind.Media, MessageImportance.Normal);
        var quote = MessagePreviewSamples.Create(MessagePreviewKind.Quote, MessageImportance.Important) with
        {
            Sender = "李四", Conversation = "另一个群", SourcePid = 1, Scene = "group"
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
