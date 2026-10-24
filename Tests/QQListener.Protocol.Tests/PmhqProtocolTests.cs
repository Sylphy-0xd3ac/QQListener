using System.Text;
using QQListener.Core;
using QQListener.Models;
using QQListener.Oidb;

namespace QQListener.Protocol.Tests;

public sealed class PmhqProtocolTests
{
    [Fact]
    public void StaleUnbindCannotClearOrNotifyForANewerBinding()
    {
        var router = new EngineCommandRouter();
        var old = Guid.NewGuid();
        var current = Guid.NewGuid();
        var api = QqControlApi.OverRequests((_, _, _) => [42]);
        router.Bind(old, api);
        router.Bind(current, api);
        var changes = 0;
        router.SessionChanged += () => changes++;
        Assert.False(router.TryUnbind(old));
        Assert.False(router.TryUnbind(Guid.Empty));
        Assert.True(router.IsCurrent(current));
        Assert.Equal(new byte[] { 42 }, router.ForSession(current).SendRaw(22, "fixture", []));
        Assert.Equal(0, changes);
        Assert.True(router.TryUnbind(current));
        Assert.False(router.IsCurrent(current));
        Assert.Equal(1, changes);
    }

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("{\"qq_pid\":42,\"uin\":12345,\"uid\":\"u_self\"}", true)]
    [InlineData("{\"qq_pid\":42,\"uin\":\"12345\",\"uid\":\"u_self\"}", true)]
    [InlineData("{\"qq_pid\":null,\"uin\":null,\"uid\":null}", false)]
    [InlineData("{\"qq_pid\":\"42\",\"uin\":12345,\"uid\":\"u_self\"}", false)]
    [InlineData("{\"qq_pid\":42,\"uin\":0,\"uid\":\"u_self\"}", false)]
    public void HealthRequiresMatchingProcessAndCompleteLogin(string text, bool ready) =>
        Assert.Equal(ready, PmhqProtocol.ReadHealth(Json(text)).LoggedIn);

    [Theory]
    [InlineData("{\"type\":\"recv\",\"code\":0,\"data\":{\"echo\":\"test\",\"cmd\":\"test.cmd\",\"pb\":\"0001aBff\"}}")]
    [InlineData("{\"type\":\"send\",\"code\":0,\"data\":{\"echo\":\"test\",\"cmd\":\"test.cmd\",\"pb\":\"0001aBff\"}}")]
    public void BothDocumentedReplyKindsAreAccepted(string json) =>
        Assert.Equal(new byte[] { 0, 1, 0xab, 0xff }, PmhqProtocol.ReadReply(Json(json), "test", "test.cmd"));

    [Theory]
    [InlineData("{\"type\":\"recv\",\"code\":-100,\"message\":\"secret=do-not-log\",\"data\":{\"echo\":\"test\",\"cmd\":\"test.cmd\",\"pb\":\"00\"}}")]
    [InlineData("{\"type\":\"recv\",\"data\":{\"echo\":\"test\",\"cmd\":\"test.cmd\",\"pb\":\"00\"}}")]
    [InlineData("{\"type\":\"recv\",\"code\":0,\"data\":{\"echo\":\"wrong\",\"cmd\":\"test.cmd\",\"pb\":\"00\"}}")]
    [InlineData("{\"type\":\"recv\",\"code\":0,\"data\":{\"echo\":\"test\",\"cmd\":\"wrong\",\"pb\":\"00\"}}")]
    [InlineData("{\"type\":\"recv\",\"code\":0,\"data\":{\"echo\":\"test\",\"cmd\":\"test.cmd\",\"pb\":\"0z\"}}")]
    [InlineData("[]")]
    [InlineData("invalid secret=do-not-log")]
    public void InvalidOrFailedRepliesNeverBecomeSuccessfulSends(string json)
    {
        var error = Assert.Throws<IOException>(() => PmhqProtocol.ReadReply(Json(json), "test", "test.cmd"));
        Assert.DoesNotContain("do-not-log", error.Message);
    }

    [Fact]
    public void EchoedResponsesAndOutgoingFramesNeverEnterNotificationPipeline()
    {
        const string push = "trpc.msg.olpush.OlPushService.MsgPush";
        foreach (var json in new[] {
                     """{"type":"recv","code":0,"data":{"echo":"request","cmd":"PUSH","pb":"00"}}""".Replace("PUSH", push),
                     """{"type":"send","data":{"cmd":"PUSH","pb":"00"}}""".Replace("PUSH", push),
                     """{"type":"recv","data":{"cmd":"other","pb":"00"}}""" })
            Assert.Null(PmhqProtocol.ReadPush(Json(json), "10000"));
    }

    [Fact]
    public void OldCommandHandlesCannotBeUsedAfterSwitchingEngineOrAccount()
    {
        var router = new EngineCommandRouter();
        var sends = 0;
        var first = Guid.NewGuid();
        router.Bind(first, QqControlApi.OverRequests((_, _, _) => { sends++; return []; }));
        var old = router.ForSession(first);
        router.Bind(Guid.NewGuid(), QqControlApi.OverRequests((_, _, _) => []));
        Assert.Throws<InvalidOperationException>(() => router.ForSession(first));
        Assert.Throws<InvalidOperationException>(() => old.SendRaw(42, "test", []));
        Assert.Equal(0, sends);
    }

    [Fact]
    public void NotificationSessionValidationIncludesEveryNestedDigestMember()
    {
        var router = new EngineCommandRouter();
        var session = Guid.NewGuid();
        var api = QqControlApi.OverRequests((_, _, _) => []);
        router.Bind(session, api);
        var current = new MessageEvent("qq", "QQ", "Alice", "", "hello", DateTimeOffset.Now,
            MessageImportance.Normal, [], "test") { EngineSession = session };
        var stale = current with { EngineSession = Guid.NewGuid() };
        var good = PendingMessageDigest.Create([current, current])!;
        var mixed = PendingMessageDigest.Create([current, stale])!;
        Assert.True(router.IsCurrent(good));
        Assert.False(router.IsCurrent(current with { EngineSession = Guid.Empty }));
        Assert.False(router.IsCurrent(PendingMessageDigest.Create([current, mixed])!));
        router.Bind(Guid.NewGuid(), api);
        Assert.False(router.IsCurrent(current));
        Assert.False(router.IsCurrent(good));
        router.Bind(session, null);
        Assert.False(router.IsCurrent(current));
    }

    [Fact]
    public void FailingUiObserverDoesNotLeaveANewEngineHalfStarted()
    {
        var router = new EngineCommandRouter();
        router.SessionChanged += () => throw new InvalidOperationException("synthetic observer failure");
        var observed = false;
        router.SessionChanged += () => observed = true;
        var session = Guid.NewGuid();
        router.Bind(session, QqControlApi.OverRequests((_, _, _) => []));
        Assert.True(observed);
        Assert.True(router.IsCurrent(session));
    }

    [Fact]
    public void SwitchingEnginesWaitsForOldQqProcessesWithoutBlockingUnrelatedProcesses()
    {
        var guard = new EngineSwitchGuard();
        guard.RequireRestart([42, 43]);
        Assert.False(guard.CanStart([42, 99]));
        Assert.True(guard.CanStart([99]));
    }

    [Fact]
    public void ResidentHookMarkerKeepsPidAndStartTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "QQListener-PMHQ-marker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = new PmhqBundle(root);
            bundle.RememberResidentHook(42, 123456);
            Assert.True(bundle.HasResidentHook(42, 123456));
            Assert.False(bundle.HasResidentHook(42, 123457));
            Assert.False(bundle.HasResidentHook(43, 123456));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
