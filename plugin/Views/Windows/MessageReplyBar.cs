using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QQListener.Models;
using QQListener.Services;

namespace QQListener.Views.Windows;

/// <summary>
/// 浮窗底部的回复条：默认只是右下角一个不起眼的小按钮，点开才变成输入框 + 发送。
///
/// 只在 <see cref="MessageEvent.CanReply"/> 为真时才会被创建——toast 来源没有
/// 回哪去的路由信息，压根不该出现这个入口。
/// </summary>
public sealed class MessageReplyBar : StackPanel, IDisposable
{
    // 打开输入框会暂停浮窗的自动关闭；如果用户既不发送也不取消就晾在这，
    // 得有个兜底让浮窗最终还是能自己消失——教室大屏可经不起一直卡着不关。
    private static readonly TimeSpan ComposerTimeout = TimeSpan.FromSeconds(45);

    private readonly MessageEvent _message;
    private readonly MessageReplyService _replyService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Button _replyToggle;
    private readonly Panel _composer;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly TextBlock _status;
    private DispatcherTimer? _composerTimeout;
    private bool _disposed;
    private bool _sending;

    /// <summary>展开输入框时触发——窗口据此暂停自动关闭，别让用户打到一半就被收走。</summary>
    public event Action? ComposerOpened;

    /// <summary>收起输入框时触发（发送完成或用户取消）——窗口据此恢复/重置自动关闭计时。</summary>
    public event Action? ComposerClosed;

    public MessageReplyBar(MessageEvent message, MessageReplyService? replyService = null)
    {
        _message = message;
        _replyService = replyService ?? new MessageReplyService();
        Spacing = 8;

        _replyToggle = new Button
        {
            Content = "↩ 回复",
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0.55,
            Background = Brushes.Transparent,
            Padding = new Avalonia.Thickness(8, 2),
        };
        _replyToggle.Click += (_, _) => ShowComposer();

        _input = new TextBox { Watermark = "回复…", AcceptsReturn = false };
        _send = new Button { Content = "发送" };
        var cancel = new Button { Content = "×", Padding = new Avalonia.Thickness(8, 2) };
        _status = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#BCC5D1")),
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8,
        };
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(_send, 1);
        Grid.SetColumn(cancel, 2);
        row.Children.Add(_input);
        row.Children.Add(_send);
        row.Children.Add(cancel);

        var composerStack = new StackPanel { Spacing = 4 };
        composerStack.Children.Add(row);
        composerStack.Children.Add(_status);
        _composer = new Panel { IsVisible = false };
        _composer.Children.Add(composerStack);

        Children.Add(_replyToggle);
        Children.Add(_composer);

        cancel.Click += (_, _) => HideComposer();
        _send.Click += async (_, _) => await SendAsync();
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SendAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; HideComposer(); }
        };
    }

    private void ShowComposer()
    {
        _replyToggle.IsVisible = false;
        _composer.IsVisible = true;
        _status.Text = "";
        _input.Focus();
        ComposerOpened?.Invoke();

        _composerTimeout?.Stop();
        _composerTimeout = new DispatcherTimer { Interval = ComposerTimeout };
        _composerTimeout.Tick += (_, _) => HideComposer();
        _composerTimeout.Start();
    }

    private void HideComposer()
    {
        if (_disposed) return;
        _composerTimeout?.Stop();
        _composerTimeout = null;
        _composer.IsVisible = false;
        _replyToggle.IsVisible = true;
        _input.Text = "";
        _status.Text = "";
        ComposerClosed?.Invoke();
    }

    private async Task SendAsync()
    {
        if (_disposed || _sending) return;
        var text = _input.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        _sending = true;
        _send.IsEnabled = false;
        _status.Text = "发送中…";
        try
        {
            await _replyService.SendAsync(_message, text, _lifetime.Token);
            if (_disposed) return;
            _status.Text = "已发送";
            _input.Text = "";
            await Task.Delay(TimeSpan.FromSeconds(1.2), _lifetime.Token);
            HideComposer();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 不把管道内部错误细节显示到课堂屏幕上。
            if (!_disposed) _status.Text = "发送失败，请重试";
        }
        finally
        {
            _sending = false;
            if (!_disposed) _send.IsEnabled = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _composerTimeout?.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
