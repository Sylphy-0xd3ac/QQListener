using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using QQListener.Models;
using QQListener.Services;

namespace QQListener.Views.Windows;

/// <summary>富消息与引用使用同一个渲染器。关闭时取消预览、释放位图，文件下载继续。</summary>
public sealed class MessageContentView : StackPanel, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly MessageAttachmentDownloader Downloader = new(Http);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Bitmap> _bitmaps = [];
    private readonly string _directory;
    private readonly MessageAttachmentDownloader _downloader;
    private readonly MessageImageDownloader _imageDownloader;
    private readonly bool _autoDownloadFiles;
    private bool _disposed;

    public MessageContentView(MessageEvent message, string directory,
        MessageAttachmentDownloader? downloader = null, bool autoDownloadFiles = false,
        Core.EngineCommandRouter? engines = null)
    {
        _directory = directory;
        _downloader = downloader ?? Downloader;
        _imageDownloader = new MessageImageDownloader(_downloader, fetchForImage: engines is null ? null
            : image => engines.ForSession(image.EngineSession).FetchRKeys(image.SourcePid));
        _autoDownloadFiles = autoDownloadFiles;
        Spacing = 10;
        AddBlocks(this, MessageContentPresentation.Create(message));
    }

    private static T Themed<T>(T control, AvaloniaProperty property, string resource) where T : Control
    {
        control.SetResourceReference(property, resource);
        return control;
    }

    private static TextBlock Label(string text, double size = 16) => Themed(new TextBlock
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap
    }, TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

    private void AddBlocks(StackPanel panel, IReadOnlyList<MessageContentBlock> blocks, bool compact = false)
    {
        // 与原版 MessageEntry 一致：上方是被引用内容，下方是当前正文。
        foreach (var block in blocks.Where(b => b.Kind == "quote").Concat(blocks.Where(b => b.Kind != "quote")))
        {
            if (block.Kind == "message")
            {
                var entry = new StackPanel { Spacing = 8 };
                var sender = Label(block.Text, 14);
                sender.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                entry.Children.Add(sender);
                AddBlocks(entry, block.Children ?? []);
                panel.Children.Add(new Border
                {
                    BorderBrush = this.FindResource("ControlStrokeColorDefaultBrush") as IBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 10), Child = entry
                });
            }
            else if (block.Kind == "quote")
            {
                var quote = new StackPanel { Spacing = 6 };
                if (!string.IsNullOrWhiteSpace(block.Text))
                {
                    var sender = Label(block.Text, 12);
                    sender.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                    quote.Children.Add(sender);
                }
                AddBlocks(quote, block.Children ?? [], compact: true);
                panel.Children.Add(new Border
                {
                    BorderBrush = this.FindResource("AccentTextFillColorPrimaryBrush") as IBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(14, 2, 0, 2), Child = quote
                });
                var divider = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
                divider.Children.Add(new Border
                {
                    Height = 1, Background = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center
                });
                var title = Label("引用消息", 12);
                title.Margin = new Thickness(10, 0);
                Grid.SetColumn(title, 1);
                divider.Children.Add(title);
                var line = new Border
                {
                    Height = 1, Background = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(line, 2);
                divider.Children.Add(line);
                panel.Children.Add(divider);
            }
            else if (block.Kind == "text")
            {
                var label = Label(block.Text, compact ? 14 : 16);
                if (compact) label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                panel.Children.Add(label);
            }
            else panel.Children.Add(CreateAttachment(block, compact));
        }
    }

    private Control CreateAttachment(MessageContentBlock block, bool compact)
    {
        var isImage = block.Kind == "image";
        var status = Label("", 12);
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        // 一律左对齐。图片居中的话，"文字—图片—文字"这种消息读起来是折线：
        // 两段文字贴左边，中间的图片却飘在中央，还会在两侧留下大片空白。
        var image = new Image
        {
            MaxHeight = compact ? 110 : 280, MaxWidth = compact ? 170 : 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            Stretch = Stretch.Uniform, Focusable = true
        };
        var ring = new ProgressRing { Width = 22, Height = 22, IsVisible = false, IsActive = true };
        var button = new Button { Content = block.Kind == "image" ? "加载图片" : "下载" };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(button);
        actions.Children.Add(ring);
        Control result;
        if (isImage)
        {
            var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(image);
            content.Children.Add(actions);
            content.Children.Add(status);
            result = content;
        }
        else
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,Auto"), ColumnSpacing = 12 };
            var icon = Label(block.Kind == "video" ? "▶" : block.Kind == "record" ? "♪" : "▤", 24);
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            row.Children.Add(icon);
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Label(block.Text, 14));
            text.Children.Add(status);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            result = new Border
            {
                MinHeight = 62, Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(8),
                Background = this.FindResource("SubtleFillColorSecondaryBrush") as IBrush,
                BorderBrush = this.FindResource("ControlStrokeColorDefaultBrush") as IBrush, BorderThickness = new Thickness(1),
                Child = row
            };
        }
        string? localPath = null;
        var downloading = false;

        // 离线测试图随插件打包；不需要访问网络，也不触碰 QQ。
        if (block.Kind == "image" && block.Url == "avares://QQListener/icon.png")
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri(block.Url));
                var bitmap = new Bitmap(stream);
                _bitmaps.Add(bitmap);
                image.Source = bitmap;
                image.MaxHeight = compact ? 110 : 160;
                actions.IsVisible = false;
                status.IsVisible = false;
            }
            catch (Exception) { status.Text = "预览图片无法加载"; }
        }
        else if (string.IsNullOrWhiteSpace(block.Url))
        {
            button.IsEnabled = false;
            status.Text = "消息未提供下载地址";
        }

        async Task ActivateAsync()
        {
            if (_disposed || downloading) return;
            downloading = true;
            button.IsEnabled = false;
            try
            {
                if (localPath is not null)
                {
                    var topLevel = TopLevel.GetTopLevel(this);
                    // 未知类型在文件管理器中查看，不能把远端文件名当作可执行程序启动。
                    var opened = topLevel is not null && (CanOpenFile(localPath)
                        ? await topLevel.Launcher.LaunchFileInfoAsync(new FileInfo(localPath))
                        : await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_directory)));
                    if (!opened)
                        status.Text = "无法打开，请从下载目录中打开此文件";
                    return;
                }
                ring.IsVisible = true;
                ring.IsIndeterminate = true;
                status.Text = "下载中…";
                var progress = new Progress<double>(value =>
                {
                    if (_disposed || !downloading) return;
                    ring.IsIndeterminate = value < 0;
                    ring.Value = Math.Max(0, value * 100);
                    status.Text = value < 0 ? "下载中…" : $"下载中 {value:P0}";
                });
                var path = isImage
                    ? await _imageDownloader.DownloadAsync(block, _directory, progress, _lifetime.Token)
                    : await _downloader.DownloadAsync(block.Url, _directory, block.Text,
                        progress, block.Kind == "file" ? CancellationToken.None : _lifetime.Token);
                downloading = false;
                if (_disposed) return;
                if (!isImage) localPath = path;
                button.Content = CanOpenFile(path) ? "打开" : "打开下载目录";
                status.Text = "已保存到下载目录";
                if (block.Kind == "image")
                {
                    // 解码在后台进行，避免大图片阻塞宿主的 UI 线程。
                    var bitmap = await Task.Run(() =>
                    {
                        using var stream = File.OpenRead(path);
                        return Bitmap.DecodeToWidth(stream, 960);
                    });
                    if (_disposed) bitmap.Dispose();
                    else
                    {
                        _bitmaps.Add(bitmap);
                        localPath = path;
                        image.Source = bitmap;
                        actions.IsVisible = false;
                        status.IsVisible = false;
                        ToolTip.SetTip(image, "点击用系统图片查看器打开");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!_disposed) status.Text = "下载超时，请重试";
            }
            catch (QqImageDownloadException exception)
            {
                if (!_disposed) status.Text = exception.Message;
            }
            catch (HttpRequestException exception) when (isImage && exception.StatusCode is { })
            {
                if (!_disposed) status.Text = $"图片下载失败（HTTP {(int)exception.StatusCode.Value}），请重试";
            }
            catch (Exception)
            {
                // 不把含临时令牌的 URL 或异常信息显示到课堂屏幕上。
                if (!_disposed) status.Text = localPath is null
                    ? "下载失败，请检查地址、目录或网络后重试"
                    : "文件已保存，无法预览或打开";
            }
            finally
            {
                downloading = false;
                if (!_disposed) { button.IsEnabled = true; ring.IsVisible = false; }
            }
        }
        button.Click += async (_, _) => await ActivateAsync();
        image.PointerPressed += async (_, e) =>
        {
            if (localPath is null) return;
            e.Handled = true;
            await ActivateAsync();
        };
        image.KeyDown += async (_, e) =>
        {
            if (localPath is null || e.Key != Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            await ActivateAsync();
        };
        if ((isImage || block.Kind == "file" && _autoDownloadFiles)
            && Uri.TryCreate(block.Url, UriKind.Absolute, out var attachmentUri)
            && attachmentUri.Scheme is "http" or "https")
        {
            var started = false;
            result.AttachedToVisualTree += async (_, _) =>
            {
                if (started) return;
                started = true;
                await ActivateAsync();
            };
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var bitmap in _bitmaps) bitmap.Dispose();
        _bitmaps.Clear();
    }

    private static bool CanOpenFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or
        ".pdf" or ".txt" or ".docx" or ".xlsx" or ".pptx" or
        ".mp4" or ".mkv" or ".mov" or ".webm" or ".mp3" or ".wav" or ".ogg";
}
