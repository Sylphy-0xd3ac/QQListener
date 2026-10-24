using QQListener.Oidb;
using QQListener.Models;

namespace QQListener.Core;

/// <summary>Messages retain their session, so old windows cannot send through a new engine/account.</summary>
public sealed class EngineCommandRouter
{
    private readonly object _gate = new();
    private Guid _session;
    private QqControlApi? _api;

    public event Action? SessionChanged;

    public void Bind(Guid session, QqControlApi? api)
    {
        lock (_gate) { _session = session; _api = api; }
        var callbacks = SessionChanged;
        if (callbacks is null) return;
        foreach (Action callback in callbacks.GetInvocationList())
        {
            try { callback(); }
            catch (Exception) { System.Diagnostics.Debug.WriteLine("QQListener: session observer failed"); }
        }
    }

    public QqControlApi ForSession(Guid session)
    {
        lock (_gate)
        {
            Validate(session);
            // Validate on every wire request, not just when a caller obtains the handle.
            return QqControlApi.OverRequests((pid, command, body) =>
            {
                lock (_gate)
                {
                    Validate(session);
                    return _api!.SendRaw(pid, command, body);
                }
            });
        }
    }

    private void Validate(Guid session)
    {
        if (session == Guid.Empty || session != _session || _api is null)
            throw new InvalidOperationException("消息所属的引擎或账号已切换，请等待新消息后重试");
    }

    public bool IsCurrent(Guid session)
    {
        lock (_gate) return session != Guid.Empty && session == _session && _api is not null;
    }

    /// <summary>Digest wrappers have no session; every member must belong to the current session.</summary>
    public bool IsCurrent(MessageEvent message)
    {
        lock (_gate) return Matches(message);

        bool Matches(MessageEvent item) => item.IsPendingDigest
            ? item.PendingMessages.All(Matches)
            : item.EngineSession != Guid.Empty && item.EngineSession == _session && _api is not null;
    }
}
