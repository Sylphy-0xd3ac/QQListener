using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Enums.Notification;

namespace QQListener.Services;

internal sealed class NotificationPresentationLifetime : IDisposable
{
    internal static bool IsContentPlaying(NotificationRequest request) =>
        request.State == NotificationState.Playing
        && request.OverlaySession.SessionStartTime != DateTime.MinValue
        && !request.OverlaySession.IsCompleted
        && !request.CancellationToken.IsCancellationRequested
        && !request.CompletedToken.IsCancellationRequested;

    private readonly NotificationRequest _request;
    private readonly CancellationTokenRegistration _canceled;
    private readonly CancellationTokenRegistration _completed;
    private readonly CancellationTokenRegistration _stopped;
    private int _disposed;

    public NotificationPresentationLifetime(
        NotificationRequest request, Action closeWindow, CancellationToken cancellationToken)
    {
        _request = request;
        _canceled = request.CancellationToken.Register(closeWindow);
        _completed = request.CompletedToken.Register(closeWindow);
        _stopped = cancellationToken.Register(Cancel);
    }

    public void Cancel()
    {
        if (!_request.CancellationToken.IsCancellationRequested)
            _request.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _canceled.Dispose();
        _completed.Dispose();
        _stopped.Dispose();
        Cancel();
    }
}
