using QQListener.Native;

namespace QQListener.Protocol.Tests;

public sealed class RemoteImageCleanupTests
{
    [Fact]
    public void TimeoutDuringRollbackDoesNotFreeCodeStillExecutingInQq()
    {
        var freed = false;
        var timeout = new RemoteCallTimeoutException("synthetic");
        Assert.Same(timeout, Assert.Throws<RemoteCallTimeoutException>(() => RemoteImageCleanup.Run(
            [() => throw timeout, () => freed = true])));
        Assert.False(freed);
    }

    [Fact]
    public void FailedExceptionTableUnregisterAlsoPreventsFreeingItsMemory()
    {
        var freed = false;
        Assert.Throws<RemoteImageRetentionException>(() => RemoteImageCleanup.Run(
            [() => throw new InjectionError("synthetic"), () => freed = true]));
        Assert.False(freed);
    }
}
