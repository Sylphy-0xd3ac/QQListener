namespace QQListener.Core;

/// <summary>Owns one QQ process's PMHQ runtime and connection.</summary>
public interface IPmhqHost : IDisposable
{
    IPmhqClient? Client { get; }
    bool HasExited { get; }
    bool NativeInitialized { get; }
    Task PrepareAsync(string? proxy, CancellationToken ct) => Task.CompletedTask;
    void Start(int qqPid);
}
