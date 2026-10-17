using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using QQListener.Models;
using QQListener.Services;

namespace QQListener.Views.Windows;

/// <summary>确认时引用原消息发送预设文本；取消只收起通知。</summary>
public sealed class MessageReplyBar : StackPanel, IDisposable
{
    private readonly MessageEvent _message;
    private readonly string _text;
    private readonly MessageReplyService _replyService;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Button _confirm;
    private readonly Button _cancel;
    private readonly TextBlock _status;
    private bool _disposed;
    private bool _sending;

    public event Action? SendStarted;
    public event Action? SendFailed;
    public event Action? DismissRequested;

    public MessageReplyBar(MessageEvent message, string defaultReplyText = "收到",
        MessageReplyService? replyService = null)
    {
        _message = message;
        _text = string.IsNullOrWhiteSpace(defaultReplyText) ? "收到" : defaultReplyText.Trim();
        _replyService = replyService ?? new MessageReplyService();
        Spacing = 8;
        _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _confirm = new Button { Content = "确定", MinWidth = 80, Padding = new Thickness(18, 7) };
        _confirm.Classes.Add("accent");
        ToolTip.SetTip(_confirm, $"引用这条消息并回复：{_text}");
        _cancel = new Button { Content = "取消", MinWidth = 80, Padding = new Thickness(18, 7) };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8
        };
        row.Children.Add(_cancel);
        row.Children.Add(_confirm);
        Children.Add(_status);
        Children.Add(row);
        _cancel.Click += (_, _) => { if (!_sending) DismissRequested?.Invoke(); };
        _confirm.Click += async (_, _) => await SendAsync();
    }

    private async Task SendAsync()
    {
        if (_disposed || _sending) return;
        _sending = true;
        _confirm.IsEnabled = _cancel.IsEnabled = false;
        _status.Text = "发送中…";
        SendStarted?.Invoke();
        try
        {
            var result = await _replyService.SendAsync(_message, _text, _lifetime.Token);
            if (_disposed) return;
            if (result.Result != 0) throw new IOException("QQ 拒绝发送");
            _status.Text = "已发送";
            DismissRequested?.Invoke();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!_disposed)
            {
                _status.Text = "发送失败，请重试";
                SendFailed?.Invoke();
            }
        }
        finally
        {
            _sending = false;
            if (!_disposed) _confirm.IsEnabled = _cancel.IsEnabled = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
