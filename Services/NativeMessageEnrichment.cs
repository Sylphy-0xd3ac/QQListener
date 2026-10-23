using QQListener.Oidb;
using QQListener.Protocol;

namespace QQListener.Services;

/// <summary>
/// 消息落地前的补全：发送者备注、图片 rkey 签名、群/私聊文件下载地址。
///
/// 这几步都要走 control 管道（进程间 IO）。刻意做成同步调用——每个 pid 有自己
/// 专属的捕获线程（<see cref="Native.RecvCapture"/>），同步补全只会拖慢这一个
/// pid 的消息节奏，不影响其他 pid，也天然保证了同一个会话里消息落地的顺序
/// 跟到达顺序一致，不用额外处理异步竞态。<see cref="Oidb.QqControlApi"/> 的每次
/// 请求本身已经有读超时兜底（见 QqControlApi.ControlReadTimeoutMs），这里只需要
/// 在拿不到结果时优雅降级：保留原始信息，不阻塞消息投递本身。
/// </summary>
public sealed class NativeMessageEnrichment(QqControlApi api)
{
    private readonly Dictionary<int, RKeyCache> _rkeyCaches = [];
    private readonly object _cacheGate = new();

    public static NativeMessageEnrichment ForCoreService() => new(QqControlApi.OverNamedPipes());

    /// <summary>备注优先于群名片/昵称——只有推送本身没带备注时才需要额外查一次资料。</summary>
    public void EnrichSenderProfile(CapturedMessage message)
    {
        if (message.SourcePid == 0 || message.SenderRemark.Length > 0 || message.SenderId.Length == 0)
            return;
        try
        {
            var profile = api.ResolveProfile(message.SourcePid, message.SenderId);
            if (profile.Remark.Length > 0) message.SenderRemark = profile.Remark;
            if (message.SenderNickname.Length == 0 && profile.Nickname.Length > 0)
                message.SenderNickname = profile.Nickname;
        }
        catch
        {
            // 拿不到资料就用推送自带的群名片/昵称，不阻塞消息投递。
        }
    }

    public void EnrichSegments(CapturedMessage message)
    {
        if (message.SourcePid == 0) return;
        var remaining = 512;
        EnrichSegments(message, message.Segments, 0, ref remaining);
    }

    private void EnrichSegments(
        CapturedMessage message, IReadOnlyList<Segment> segments, int depth, ref int remaining)
    {
        if (depth >= 8) return;
        foreach (var segment in segments)
        {
            if (remaining-- <= 0) return;
            EnrichSegment(message, segment);
            if (segment.Type == "reply" && segment.Get<IReadOnlyList<Segment>>("segments") is { } quoted)
                EnrichSegments(message, quoted, depth + 1, ref remaining);
        }
    }

    private void EnrichSegment(CapturedMessage message, Segment segment)
    {
        try
        {
            if (segment.Type == "image")
                EnrichImageRKey(message, segment);
            else if (segment.Type == "file" && segment.Url.Length == 0)
                EnrichFileUrl(message, segment);
        }
        catch
        {
            // 签不到 rkey / 拿不到下载地址就保留原样，不阻塞消息投递；
            // 用户看到的是没法下载的占位符，而不是整条消息消失或者宿主崩溃。
        }
    }

    private void EnrichImageRKey(CapturedMessage message, Segment segment)
    {
        segment.Url = Elements.MakeImageUrl(segment.Url);
        if (!RKey.UrlNeedsRKey(segment.Url)) return;

        var rkeyType = RKey.TypeForUrl(segment.Url, message.IsGroup);
        var cache = GetOrCreateCache(message.SourcePid);
        var rkey = cache.Get(rkeyType);
        if (rkey.Length > 0) segment.Url = RichText.AppendRkey(segment.Url, rkey);
    }

    private void EnrichFileUrl(CapturedMessage message, Segment segment)
    {
        var fileId = segment.Get<string>("file_id") ?? "";
        if (fileId.Length == 0) return;

        segment.Url = message.IsGroup
            ? ResolveGroupFileUrl(message, segment, fileId)
            : ResolvePrivateFileUrl(message, fileId);
    }

    private string ResolveGroupFileUrl(CapturedMessage message, Segment segment, string fileId)
    {
        if (!long.TryParse(message.PeerId, out var groupUin)) return "";
        var busId = (long)segment.Get("bus_id", 102UL);
        return api.ResolveGroupFileUrl(message.SourcePid, groupUin, fileId, busId);
    }

    private string ResolvePrivateFileUrl(CapturedMessage message, string fileId)
    {
        if (message.AccountUid.Length == 0) return "";
        var fileHash = segmentFileHash(message, fileId);
        return api.ResolvePrivateFileUrl(message.SourcePid, message.AccountUid, fileId, fileHash);

        static string segmentFileHash(CapturedMessage m, string id) =>
            m.Segments.FirstOrDefault(s => s.Get<string>("file_id") == id)?.Get<string>("file_hash") ?? "";
    }

    /// <summary>rkey 按 pid 分开缓存——不同 QQ 进程互不相干，各自的 rkey 也不能混用。</summary>
    private RKeyCache GetOrCreateCache(int pid)
    {
        lock (_cacheGate)
        {
            if (_rkeyCaches.TryGetValue(pid, out var existing)) return existing;
            var created = new RKeyCache(pid, api.FetchRKeys);
            _rkeyCaches[pid] = created;
            return created;
        }
    }

    /// <summary>
    /// pid 对应的 QQ 进程已经不在了（退出/被系统回收给别的进程）：忘掉它的 rkey 缓存。
    ///
    /// Windows 会复用 pid，不清理的话新进程会直接沿用上一个账号（甚至上一个完全
    /// 无关的进程）的 rkey，供出去的图片地址要么签给了错误的账号，要么根本不该
    /// 存在这条缓存。同一个 QQ 进程内退出登录换账号（pid 不变）这种情况目前没有
    /// 覆盖——需要监听 <c>PipeOp.LoginState</c> 推送，这条链路目前还没接线。
    /// </summary>
    public void ForgetPid(int pid)
    {
        lock (_cacheGate) _rkeyCaches.Remove(pid);
    }
}
