using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using QQListener.Models;
using QQListener.Services;

namespace QQListener.Views.Windows;

/// <summary>确认时可选发送预设文本或群消息表情回应；取消只收起通知。</summary>
public sealed class MessageReplyBar : StackPanel, IDisposable
{
    private readonly MessageEvent _message;
    private readonly string _text;
    private readonly MessageReplyService _replyService;
    private readonly bool _replyOnConfirmEnabled;
    private readonly bool _replyWithOkReaction;
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
        MessageReplyService? replyService = null, bool replyOnConfirmEnabled = false,
        bool replyWithOkReaction = false)
    {
        _message = message;
        _text = string.IsNullOrWhiteSpace(defaultReplyText) ? "收到" : defaultReplyText.Trim();
        _replyService = replyService ?? new MessageReplyService();
        _replyWithOkReaction = replyWithOkReaction;
        _replyOnConfirmEnabled = replyOnConfirmEnabled && (replyWithOkReaction ? message.CanReact : message.CanReply);
        Spacing = 8;
        _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _confirm = new Button { Content = "确定", MinWidth = 80, Padding = new Thickness(18, 7) };
        _confirm.Classes.Add("accent");
        ToolTip.SetTip(_confirm, _replyOnConfirmEnabled
            ? replyWithOkReaction ? "给这条群消息添加 OK 表情回应"
                : message.Scene == "group" ? $"引用这条消息并回复：{_text}" : $"回复：{_text}"
            : "收起这条提醒，不发送回复");
        if (replyOnConfirmEnabled && replyWithOkReaction && !message.CanReact)
            _status.Text = "OK 表情回应仅支持群消息；此消息确认只收起提醒";
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
        if (!_replyOnConfirmEnabled)
        {
            DismissRequested?.Invoke();
            return;
        }
        _sending = true;
        _confirm.IsEnabled = _cancel.IsEnabled = false;
        _status.Text = "发送中…";
        SendStarted?.Invoke();
        try
        {
            if (_replyWithOkReaction)
                await _replyService.SendOkReactionAsync(_message, _lifetime.Token);
            else
            {
                var result = await _replyService.SendAsync(_message, _text, _lifetime.Token);
                if (result.Result != 0) throw new IOException("QQ 拒绝发送");
            }
            if (_disposed) return;
            _status.Text = _replyWithOkReaction ? "已添加 OK 表情回应" : "已发送";
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
