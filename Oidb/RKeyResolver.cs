using System.Text.RegularExpressions;
using QQListener.Protocol;

namespace QQListener.Oidb;

public sealed record RKeyInfo(string RKey, int Type, long TtlSeconds = 0, long CreateTime = 0);

/// <summary>
/// NT 图片/媒体下载凭证（rkey）。
///
/// QQ-NT 的 <c>multimedia.nt.qq.com.cn/download?...</c> 地址必须带一枚服务器签发的
/// 短效 rkey，否则 CDN 直接以
/// <c>{"retcode":-5503010,"retmsg":"invalid rkey"}</c> 拒绝——表现就是图片"炸"了。
///
/// 推送里 <c>ExtBizInfo.pic.bytesPbReserveC2c.field30</c> 有时自带（私聊图片常见），
/// 拿不到时按 SnowLuma 的做法走 OIDB 0x9067_202 取一批并按类型缓存。
/// </summary>
public static partial class RKey
{
    public const int TypePrivateImage = 10;
    public const int TypeGroupImage = 20;
    public const int TypeFallbackImage = 2;
    public const int TypePrivateVideo = 12;
    public const int TypeGroupVideo = 22;
    public const int TypePrivatePtt = 14;
    public const int TypeGroupPtt = 24;

    private static readonly int[] RequestTypes =
        [TypePrivateImage, TypeGroupImage, TypeFallbackImage];

    [GeneratedRegex(@"[?&]appid=(\d+)")]
    private static partial Regex AppIdRegex();

    /// <summary>OIDB 0x9067_202 / NTV2RichMediaReq{reqHead, downloadRkey}。</summary>
    public static byte[] BuildRequest()
    {
        var common = Wire.Concat(Wire.EncodeVarintField(1, 1), Wire.EncodeVarintField(2, 202));
        var scene = Wire.Concat(
            Wire.EncodeVarintField(101, 2),
            Wire.EncodeVarintField(102, 1),
            Wire.EncodeVarintField(200, 0));
        var client = Wire.EncodeVarintField(1, 2);
        var reqHead = Wire.Concat(
            Wire.EncodeBytesField(1, common),
            Wire.EncodeBytesField(2, scene),
            Wire.EncodeBytesField(3, client));

        var downloadRkey = Wire.Concat(
            [.. RequestTypes.Select(t => Wire.EncodeVarintField(1, (ulong)t))]);

        return OidbEnvelope.Build(0x9067, 202,
            Wire.Concat(Wire.EncodeBytesField(1, reqHead), Wire.EncodeBytesField(4, downloadRkey)),
            uinForm: true);
    }

    public static List<RKeyInfo> ParseResponse(ReadOnlySpan<byte> data)
    {
        var response = OidbEnvelope.Unwrap(data, "rkey");

        var headData = Wire.FieldBytes(response, 1);
        if (headData.Length > 0)
        {
            var head = Wire.DecodeFields(headData);
            var retCode = (int)Wire.FieldInt(head, 2); // 负数错误码，见 OidbEnvelope.Unwrap 的注释
            if (retCode != 0)
            {
                var msg = Wire.FieldString(head, 3);
                throw new IOException(msg.Length > 0 ? msg : $"rkey request failed: {retCode}");
            }
        }

        var rkeyData = Wire.FieldBytes(response, 4);
        if (rkeyData.Length == 0) return [];

        var result = new List<RKeyInfo>();
        foreach (var item in Wire.Repeated(Wire.DecodeFields(rkeyData), 1))
        {
            var entry = Wire.DecodeFields(Wire.AsBytes(item));
            var rkey = Wire.FieldString(entry, 1);
            var type = (int)Wire.FieldInt(entry, 5);
            if (rkey.Length == 0 || type == 0) continue;
            result.Add(new RKeyInfo(rkey, type,
                (long)Wire.FieldInt(entry, 2), (long)Wire.FieldInt(entry, 4)));
        }
        return result;
    }

    /// <summary>
    /// rkey 的场景跟图片自己的上传上下文走（URL 里的 appid），不是承载它的那条消息。
    ///
    /// 一张私聊图被转发进群，仍然保留 appid=1406；用群 rkey 去签会得到 invalid rkey。
    /// </summary>
    public static int TypeForUrl(string url, bool isGroup)
    {
        var match = AppIdRegex().Match(url ?? "");
        if (match.Success)
        {
            if (match.Groups[1].Value == "1406") return TypePrivateImage;
            if (match.Groups[1].Value == "1407") return TypeGroupImage;
        }
        return isGroup ? TypeGroupImage : TypePrivateImage;
    }

    public static bool UrlNeedsRKey(string url)
    {
        return IsMediaUrl(url) && !RichText.HasRkey(url);
    }

    public static bool IsMediaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")) return false;
        return uri.Host.Equals("multimedia.nt.qq.com.cn", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".nt.qq.com.cn", StringComparison.OrdinalIgnoreCase)
            || (uri.Host.Equals("gchat.qpic.cn", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/download", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 按类型缓存 rkey，带过期与失败退避；线程安全。
///
/// 每个实例专属一个 <paramref name="pid"/>：<see cref="Get"/>/<see cref="Refresh"/>
/// 之前接受调用方传入的 pid，签名上完全没有约束"这个缓存本来就是给哪个 pid 用的"——
/// 一旦把一个 <see cref="RKeyCache"/> 共享给多个 pid，A 账号的 rkey 就可能被发给 B 账号，表现是 invalid rkey 且
/// 极难定位。绑死在构造函数里，"共享一个实例给多个 pid"从类型上就不成立。
///
/// <paramref name="clock"/> 只为测试而存在：过期与冷却都是时间行为，
/// 不注入时钟就只能靠 sleep 去验。
/// </summary>
public sealed class RKeyCache(
    int pid,
    Func<int, IReadOnlyList<RKeyInfo>> fetch,
    Func<DateTimeOffset>? clock = null)
{
    // 快到期就当作没有：拿一把过期 rkey 去下载和不带是一个结果。
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EmptyCooldown = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly Dictionary<int, (string Value, DateTimeOffset ExpiresAt)> _entries = [];
    private DateTimeOffset? _lastAttempt;

    public string Get(int rkeyType)
    {
        var cached = Lookup(rkeyType, _clock());
        if (cached.Length > 0) return cached;

        Refresh();
        return Lookup(rkeyType, _clock());
    }

    public void Refresh()
    {
        var now = _clock();
        // 先记时间再取：取失败也进入冷却，否则管道不可用时会每条消息都重试一遍。
        if (!ShouldRefresh(now)) return;
        var rkeys = fetch(pid);
        // 过期时间用 fetch 完成之后的时间点起算，不用发起请求前的 now——管道往返
        // 本身要花时间，用请求前的时间点会让每条 rkey 的有效期系统性地缩短一个
        // RTT。方向是保守的（不会用错认为还没过期的 key），但跟"过期时间统一从
        // 本地时钟起算"这个设计意图对不上。
        Store(rkeys, _clock());
    }

    private string Lookup(int rkeyType, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var candidate in new[] { rkeyType, RKey.TypeFallbackImage })
            {
                if (!_entries.TryGetValue(candidate, out var entry)) continue;
                if (entry.Value.Length == 0) continue;
                if (now + ExpirySkew >= entry.ExpiresAt) continue;
                return entry.Value;
            }
        }
        return "";
    }

    private void Store(IReadOnlyList<RKeyInfo> rkeys, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var info in rkeys)
            {
                if (!TryComputeExpiry(info, now, out var expiresAt)) continue;
                _entries[info.Type] = (info.RKey, expiresAt);
            }
        }
    }

    /// <summary>
    /// 过期时间统一从本地时钟起算，不用服务器给的 CreateTime——判断有没有过期
    /// （<see cref="Lookup"/>）本来就是拿本地 <c>now</c> 去比，两头时钟基准不一致的话，
    /// 只要本地时钟比服务器快出一点（教室电脑没接 NTP 的情况并不罕见），刚拿到手
    /// 的 rkey 就会被判定为已经过期：陷入"每 10 秒重新请求一次、每次都判过期"的
    /// 死循环，图片却一直显示 invalid rkey，日志里看不出是时钟的问题。
    ///
    /// TtlSeconds 直接来自 wire 整数，没有边界保证——离谱的 ttl 会让 AddSeconds
    /// 溢出 DateTimeOffset.MaxValue 抛 ArgumentOutOfRangeException。一条 rkey 的
    /// 时间算不出来就跳过它，不能让一条脏数据拖累这一批全部缓存失败：那样每次
    /// 冷却结束后 <see cref="Get"/> 都会重新抛同一个异常。
    /// </summary>
    private static bool TryComputeExpiry(RKeyInfo info, DateTimeOffset now, out DateTimeOffset expiresAt)
    {
        // 0 之前用 "!= 0" 当判空条件，负数会原样通过、AddSeconds 得到一个已经在
        // 过去的时间点——这条 rkey 存进去也立刻被 Lookup 判定过期，之后每次冷却
        // 结束都重新请求同一条坏数据，陷入跟"过期时间算错"一样的死循环，只是
        // 成因换成了 ttl 本身而不是时钟基准。ttl 小于容差窗口时同理：存了也用不上。
        long ttl;
        if (info.TtlSeconds == 0) ttl = 3600;
        else if (info.TtlSeconds < 0) { expiresAt = default; return false; }
        else ttl = info.TtlSeconds;

        if (ttl <= ExpirySkew.TotalSeconds)
        {
            expiresAt = default;
            return false;
        }

        try
        {
            expiresAt = now.AddSeconds(ttl);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            expiresAt = default;
            return false;
        }
    }

    private bool ShouldRefresh(DateTimeOffset now)
    {
        lock (_gate)
        {
            var hasUsable = Lookup(RKey.TypeGroupImage, now).Length > 0
                            || Lookup(RKey.TypePrivateImage, now).Length > 0;
            var cooldown = hasUsable ? RefreshCooldown : EmptyCooldown;
            // 时钟可能回拨（教室电脑没接 NTP、一次迟到的校正就是常见的几分钟到几
            // 小时向后阶跃）：now 比上次尝试还早时，now - last 是负数，恒小于任何
            // 正的冷却时长，会把刷新永久卡死到挂钟重新追上为止——这段时间里所有
            // 图片都是 invalid rkey，日志里什么都看不出来。检测到回拨就直接当作
            // 冷却已经结束，不在这个负值上纠结。
            if (_lastAttempt is { } last && now >= last && now - last < cooldown) return false;
            _lastAttempt = now;
            return true;
        }
    }
}
