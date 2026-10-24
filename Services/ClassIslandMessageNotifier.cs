using Avalonia.Threading;
using ClassIsland.Core.Models.Notification;
using QQListener.Views.Windows;
using QQListener.Models;
using System.ComponentModel;

namespace QQListener.Services;

public class ClassIslandMessageNotifier : IDisposable
{
    private readonly QqListenerSettings settings;
    private readonly QqListenerDiagnosticsState? _diagnostics;
    private readonly MessageNotificationQueue _queue = new();
    private QqMessageWindow? _activeWindow;
    private bool _disposed;
    private readonly MessageReplyService? _replyService;
    private readonly Core.EngineCommandRouter? _engines;

    public ClassIslandMessageNotifier(QqListenerSettings settings, QqListenerDiagnosticsState? diagnostics = null,
        MessageReplyService? replyService = null, Core.EngineCommandRouter? engines = null)
    {
        this.settings = settings;
        _diagnostics = diagnostics;
        _replyService = replyService;
        _engines = engines;
        if (_engines is not null) _engines.SessionChanged += OnEngineSessionChanged;
        settings.PropertyChanged += OnSettingsChanged;
    }

    public async Task ShowAsync(
        MessageEvent messageEvent,
        Action<NotificationRequest> showNotification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(messageEvent)) return;

        void DoShow()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(messageEvent)) return;
            _queue.Enqueue(new QueuedMessageNotification(messageEvent, showNotification, cancellationToken));
            ShowNext();
            _activeWindow?.UpdateQueuedCount(_queue.MessageCount);
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            DoShow();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            DoShow,
            DispatcherPriority.Normal,
            cancellationToken);
    }

    /// <summary>纯粹构造一次提醒请求，不产生任何副作用（不弹浮窗）。</summary>
    public NotificationRequest BuildRequest(MessageEvent messageEvent)
    {
        var duration = GetDuration(messageEvent);
        var maskText = BuildMaskText(messageEvent);
        var overlay = NotificationContent.CreateSimpleTextContent(maskText, content =>
        {
            content.Duration = duration;
            content.SpeechContent = settings.SpeechEnabled ? messageEvent.SpeechText : "";
        });

        return new NotificationRequest
        {
            MaskContent = NotificationContent.CreateTwoIconsMask(maskText, rightIcon: "\uE02B", factory: content =>
            {
                content.Duration = TimeSpan.FromSeconds(2);
                // 宿主工厂默认把遮罩标题也设为语音内容，必须显式清空，避免发送者念两遍。
                content.SpeechContent = "";
            }),
            OverlayContent = overlay,
            RequestNotificationSettings =
            {
                IsSettingsEnabled = true,
                IsSpeechEnabled = settings.SpeechEnabled,
                IsNotificationEffectEnabled = true,
                IsNotificationSoundEnabled = settings.NotificationSoundEnabled,
                NotificationSoundPath = Path.Combine(
                    Path.GetDirectoryName(typeof(ClassIslandMessageNotifier).Assembly.Location)!,
                    "Assets", "Sounds", messageEvent.IsImportant ? "important_sound.mp3" : "notify_sound.mp3"),
                IsNotificationTopmostEnabled = GetTopmost(messageEvent.Importance)
            }
        };
    }

    private void ShowNext()
    {
        if (_disposed || _activeWindow is not null) return;
        while (_queue.TakeNext() is { } item)
        {
            if (!IsCurrent(item.Message)) continue;
            var request = BuildRequest(item.Message);
            try
            {
                item.ShowNotification(request);
                // A host callback can synchronously change the engine/account before a window exists.
                if (!IsCurrent(item.Message))
                {
                    request.Cancel();
                    continue;
                }
                if (request.CancellationToken.IsCancellationRequested || request.CompletedToken.IsCancellationRequested
                    || request.State == ClassIsland.Core.Enums.Notification.NotificationState.None)
                    continue;
                var window = QqMessageWindow.ShowMessage(item.Message, request.OverlayContent!.Duration,
                    settings.DownloadsDirectory, true, settings.PerformanceMode, settings.DefaultReplyText,
                    request, item.CancellationToken, settings.AutoDownloadFiles, settings.ReplyOnConfirmEnabled,
                    settings.ReplyWithOkReaction, _replyService, _engines);
                _activeWindow = window;
                window.UpdateQueuedCount(_queue.MessageCount);
                window.Closed += (_, _) =>
                {
                    _activeWindow = null;
                    ShowNext();
                };
                return;
            }
            catch (Exception exception)
            {
                request.Cancel();
                _diagnostics?.RecordError(exception, "显示排队提醒失败");
            }
        }
    }

    private bool IsCurrent(MessageEvent message) => _engines?.IsCurrent(message) ?? true;

    public async Task CancelAllAsync()
    {
        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else await Dispatcher.UIThread.InvokeAsync(Clear);
    }

    private void Clear()
    {
        _queue.Clear();
        _activeWindow?.SafeClose();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(QqListenerSettings.IsEnabled) or nameof(QqListenerSettings.Engine))) return;
        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else Dispatcher.UIThread.Post(Clear);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_engines is not null) _engines.SessionChanged -= OnEngineSessionChanged;
        _disposed = true;
        settings.PropertyChanged -= OnSettingsChanged;
        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else Dispatcher.UIThread.Post(Clear);
    }

    private void OnEngineSessionChanged()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) Clear();
        else Dispatcher.UIThread.Post(Clear);
    }

    /// <summary>顶栏直接放发送者（有群名就带上），正文交给浮窗。</summary>
    internal static string BuildMaskText(MessageEvent messageEvent)
    {
        var sender = !string.IsNullOrWhiteSpace(messageEvent.Sender)
            ? messageEvent.Sender
            : messageEvent.Conversation;
        if (string.IsNullOrWhiteSpace(sender)) return "新消息";

        var conversation = messageEvent.Conversation;
        return !string.IsNullOrWhiteSpace(conversation)
               && !string.Equals(conversation, sender, StringComparison.Ordinal)
            ? sender + " · " + conversation
            : sender;
    }

    public bool GetTopmost(MessageImportance importance) => importance switch
    {
        MessageImportance.Ignored => false,
        MessageImportance.Calling => settings.CallingTopmost,
        MessageImportance.Important => settings.ImportantTopmost,
        _ => settings.NormalTopmost
    };

    public TimeSpan GetDuration(MessageEvent message) => GetDuration(message.Importance);

    public TimeSpan GetDuration(MessageImportance importance) => importance switch
    {
        MessageImportance.Calling => TimeSpan.FromSeconds(
            Math.Clamp(settings.CallingDurationSeconds, 1, 86400)),
        MessageImportance.Important => TimeSpan.FromSeconds(
            Math.Clamp(settings.ImportantDurationSeconds, 1, 3600)),
        _ => TimeSpan.FromSeconds(
            Math.Clamp(settings.NormalDurationSeconds, 1, 3600))
    };
}
