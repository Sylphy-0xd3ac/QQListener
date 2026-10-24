namespace QQListener.Native;

public sealed class RemoteImageRetentionException(string message) : InjectionError(message);

/// <summary>A failed detach/unregister step must prevent the later free step.</summary>
internal static class RemoteImageCleanup
{
    public static void Run(IEnumerable<Action> stages)
    {
        foreach (var stage in stages)
        {
            try { stage(); }
            catch (RemoteCallTimeoutException) { throw; }
            catch (Exception)
            {
                throw new RemoteImageRetentionException("远程映像清理未完成，已保留映像以避免 QQ 崩溃；请重启 QQ");
            }
        }
    }
}
