using QQListener.Protocol;

namespace QQListener.Core;

public interface IPmhqClient : IDisposable
{
    bool IsConnected { get; }
    Task<PmhqHealth> GetHealthAsync(CancellationToken token);
    byte[] Send(int pid, string command, byte[] body);
    Task ReceiveAsync(Action<CapturedMessage> receive, Action<PmhqHealth> onHealth, CancellationToken token);
}
