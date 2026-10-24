using QQListener.Models;
using QQListener.Services;
using Xunit;
using QQListener.Core;
using QQListener.Oidb;

namespace QQListener.Tests.Services;

public class ClassIslandMessageNotifierTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OldPollResultsCannotShowAfterTheSessionChanges(int kind)
    {
        var router = new EngineCommandRouter();
        var session = Guid.NewGuid();
        var api = QqControlApi.OverRequests((_, _, _) => []);
        router.Bind(session, api);
        using var notifier = new ClassIslandMessageNotifier(new QqListenerSettings(), engines: router);
        var old = new MessageEvent("qq", "QQ", "Alice", "", "old", DateTimeOffset.Now,
            MessageImportance.Normal, [], "test") { EngineSession = session };
        var next = Guid.NewGuid();
        router.Bind(next, api);
        var message = kind switch
        {
            1 => PendingMessageDigest.Create([old, old])!,
            2 => PendingMessageDigest.Create([old with { EngineSession = next }, old])!,
            _ => old
        };
        var callbacks = 0;
        await notifier.ShowAsync(message, _ => callbacks++, default);
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SoundAndSpeechCanBeSelectedIndependently(bool sound, bool speech)
    {
        var settings = new QqListenerSettings { NotificationSoundEnabled = sound, SpeechEnabled = speech };
        var message = new MessageEvent("qq", "QQ", "Alice", "", "hello", DateTimeOffset.Now,
            MessageImportance.Normal, [], "test");
        var request = new ClassIslandMessageNotifier(settings).BuildRequest(message);
        Assert.Equal(sound, request.RequestNotificationSettings.IsNotificationSoundEnabled);
        Assert.Equal(speech, request.RequestNotificationSettings.IsSpeechEnabled);
        Assert.Equal(speech ? message.DisplayBody : "", request.OverlayContent!.SpeechContent);
        Assert.True(string.IsNullOrEmpty(request.MaskContent!.SpeechContent));
    }

    [Theory]
    [InlineData(MessageImportance.Normal, "notify_sound.mp3")]
    [InlineData(MessageImportance.Important, "important_sound.mp3")]
    [InlineData(MessageImportance.Calling, "important_sound.mp3")]
    public void UsesOriginalQqlSoundFromThePluginDirectory(MessageImportance importance, string name)
    {
        var message = new MessageEvent("qq", "QQ", "Alice", "", "hello", DateTimeOffset.Now,
            importance, [], "test");
        var request = new ClassIslandMessageNotifier(new QqListenerSettings()).BuildRequest(message);
        var path = request.RequestNotificationSettings.NotificationSoundPath;
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(name, Path.GetFileName(path));
        Assert.True(File.Exists(path), path);
        Assert.False(request.RequestNotificationSettings.IsNotificationSoundEnabled);
        Assert.True(request.RequestNotificationSettings.IsSpeechEnabled);
    }

    [Theory]
    [InlineData(MessageImportance.Normal, true, false, false, true)]
    [InlineData(MessageImportance.Important, false, true, false, true)]
    [InlineData(MessageImportance.Calling, false, false, true, true)]
    [InlineData(MessageImportance.Calling, false, false, false, false)]
    [InlineData(MessageImportance.Ignored, true, false, false, false)]
    public void TopmostUsesImportanceSetting(
        MessageImportance importance, bool normal, bool important, bool calling, bool expected)
    {
        var notifier = new ClassIslandMessageNotifier(new QqListenerSettings
        {
            NormalTopmost = normal,
            ImportantTopmost = important,
            CallingTopmost = calling
        });

        Assert.Equal(expected, notifier.GetTopmost(importance));
    }

    [Fact]
    public void DurationIsClampedAtConsumptionPoint()
    {
        var settings = new QqListenerSettings
        {
            NormalDurationSeconds = -100,
            ImportantDurationSeconds = 99999,
            CallingDurationSeconds = 999999
        };
        var notifier = new ClassIslandMessageNotifier(settings);

        Assert.Equal(TimeSpan.FromSeconds(1), notifier.GetDuration(MessageImportance.Normal));
        Assert.Equal(TimeSpan.FromSeconds(3600), notifier.GetDuration(MessageImportance.Important));
        Assert.Equal(TimeSpan.FromSeconds(86400), notifier.GetDuration(MessageImportance.Calling));
    }

    [Fact]
    public async Task ShowAsyncDoesNotInvokeCallbackWhenAlreadyCancelled()
    {
        var notifier = new ClassIslandMessageNotifier(new QqListenerSettings());
        var message = new MessageEvent(
            "qq.toast", "QQ", "Alice", "", "private", DateTimeOffset.Now,
            MessageImportance.Normal, ["Alice", "private"], "key");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var callbackCount = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            notifier.ShowAsync(message, _ => callbackCount++, cts.Token));

        Assert.Equal(0, callbackCount);
    }
}
