using QQListener.Models;

namespace QQListener.Core;

/// <summary>Neither engine can safely take over a process retaining the other engine's hooks.</summary>
public sealed class EngineSwitchGuard
{
    private readonly HashSet<int> _blocked = [];
    public void RequireRestart(IEnumerable<int> pids) => _blocked.UnionWith(pids);
    public bool CanStart(IEnumerable<int> livePids)
    {
        _blocked.IntersectWith(livePids);
        return _blocked.Count == 0;
    }
}
