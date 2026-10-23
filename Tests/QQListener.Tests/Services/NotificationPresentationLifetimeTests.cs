using ClassIsland.Core.Models.Notification;
using QQListener.Services;
using Xunit;

namespace QQListener.Tests.Services;

public sealed class NotificationPresentationLifetimeTests
{
    [Fact]
    public void ContentIsOnlyVisibleDuringTheActualHostOverlaySession()
    {
        var request = new NotificationRequest();
        Assert.False(NotificationPresentationLifetime.IsContentPlaying(request));
        typeof(NotificationRequest).GetProperty(nameof(request.State))!
            .SetValue(request, ClassIsland.Core.Enums.Notification.NotificationState.Playing);
        Assert.False(NotificationPresentationLifetime.IsContentPlaying(request));
        typeof(NotificationPlayingSessionInfo).GetProperty(nameof(request.OverlaySession.SessionStartTime))!
            .SetValue(request.OverlaySession, DateTime.Now);
        Assert.True(NotificationPresentationLifetime.IsContentPlaying(request));
        typeof(NotificationRequest).GetProperty(nameof(request.State))!
            .SetValue(request, ClassIsland.Core.Enums.Notification.NotificationState.Paused);
        Assert.False(NotificationPresentationLifetime.IsContentPlaying(request));
        typeof(NotificationRequest).GetProperty(nameof(request.State))!
            .SetValue(request, ClassIsland.Core.Enums.Notification.NotificationState.Playing);
        request.Cancel();
        Assert.False(NotificationPresentationLifetime.IsContentPlaying(request));
    }

    [Fact]
    public void DismissCancelsTheCorrespondingHostRequestOnly()
    {
        var request = new NotificationRequest();
        var other = new NotificationRequest();
        var closures = 0;
        using var lifetime = new NotificationPresentationLifetime(request, () => closures++, CancellationToken.None);

        lifetime.Cancel();
        lifetime.Cancel();

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.False(other.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, closures);
    }

    [Fact]
    public void HostCancellationAlsoClosesTheMessageWindow()
    {
        var request = new NotificationRequest();
        var closures = 0;
        using var lifetime = new NotificationPresentationLifetime(request, () => closures++, CancellationToken.None);

        request.Cancel();

        Assert.Equal(1, closures);
    }

    [Fact]
    public void StoppingTheListenerCancelsItsNotification()
    {
        var request = new NotificationRequest();
        var closures = 0;
        using var cancellation = new CancellationTokenSource();
        using var lifetime = new NotificationPresentationLifetime(request, () => closures++, cancellation.Token);

        cancellation.Cancel();

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, closures);
    }

    [Fact]
    public void WindowDisposalCancelsHostAndUnregistersCallbacks()
    {
        var request = new NotificationRequest();
        var closures = 0;
        var lifetime = new NotificationPresentationLifetime(request, () => closures++, CancellationToken.None);

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, closures);
    }
}
