using QQListener.Oidb;

namespace QQListener.Protocol.Tests;

/// <summary>
/// OIDB 请求/响应线格式。对应 Python 侧的 test_rkey_resolver / test_file_resolver /
/// test_profile_resolver —— 那几份是移植的硬规格。
/// </summary>
public class RKeyTests
{
    private static byte[] RKeyEntry(string rkey, int type, int ttl = 3600, int created = 0) =>
        Pb.LenField(1, Pb.Cat(
            Pb.LenField(1, rkey),
            Pb.VarintField(2, (ulong)ttl),
            Pb.VarintField(4, (ulong)created),
            Pb.VarintField(5, (ulong)type)));

    private static byte[] Envelope(byte[] response) =>
        Pb.Cat(Pb.VarintField(3, 0), Pb.LenField(4, response));

    [Fact]
    public void RequestIsOidb0x9067_202_AskingForImageRKeys()
    {
        var envelope = Wire.DecodeFields(RKey.BuildRequest());

        Assert.Equal(0x9067UL, Wire.FieldInt(envelope, 1));
        Assert.Equal(202UL, Wire.FieldInt(envelope, 2));
        Assert.Equal(1UL, Wire.FieldInt(envelope, 12));

        var body = Wire.DecodeFields(Wire.FieldBytes(envelope, 4));
        var reqHead = Wire.DecodeFields(Wire.FieldBytes(body, 1));
        Assert.Equal(202UL, Wire.FieldInt(Wire.DecodeFields(Wire.FieldBytes(reqHead, 1)), 2));

        var scene = Wire.DecodeFields(Wire.FieldBytes(reqHead, 2));
        Assert.Equal(2UL, Wire.FieldInt(scene, 101));
        Assert.Equal(1UL, Wire.FieldInt(scene, 102));

        var types = Wire.Repeated(Wire.DecodeFields(Wire.FieldBytes(body, 4)), 1)
            .Select(v => (int)Wire.AsInt(v)).ToHashSet();
        Assert.Contains(RKey.TypePrivateImage, types);
        Assert.Contains(RKey.TypeGroupImage, types);
        Assert.Contains(RKey.TypeFallbackImage, types);
    }

    [Fact]
    public void ResponseYieldsRKeysByType()
    {
        var download = Pb.Cat(
            RKeyEntry("&rkey=GROUP", RKey.TypeGroupImage),
            RKeyEntry("&rkey=PRIVATE", RKey.TypePrivateImage));

        var rkeys = RKey.ParseResponse(Envelope(Pb.LenField(4, download)));

        Assert.Equal(
            new Dictionary<int, string>
            {
                [RKey.TypeGroupImage] = "&rkey=GROUP",
                [RKey.TypePrivateImage] = "&rkey=PRIVATE"
            },
            rkeys.ToDictionary(i => i.Type, i => i.RKey));
    }

    [Fact]
    public void ResponseErrorCodeRaises()
    {
        var error = Assert.Throws<IOException>(
            () => RKey.ParseResponse(Pb.VarintField(3, 42)));
        Assert.Contains("42", error.Message);
    }

    /// <summary>回归测试：errorCode 是 proto int32，QQ 真实给的多是负数（这个正是
    /// "invalid rkey" 的错误码）。以前显示前没转回 int，负数会被 Wire.FieldInt
    /// 的 ulong 返回值打印成一个 20 位的天文数字，日志完全没法跟错误码表对上。</summary>
    [Fact]
    public void ResponseErrorCodeIsDisplayedAsSignedNotAsAGiantUnsignedNumber()
    {
        var errorCode = unchecked((ulong)(long)(-5503010));

        var error = Assert.Throws<IOException>(
            () => RKey.ParseResponse(Pb.VarintField(3, errorCode)));

        Assert.Contains("-5503010", error.Message);
        Assert.DoesNotContain("1844", error.Message); // ulong 格式化出来的天文数字都以这个开头
    }

    [Fact]
    public void ResponseHeadRetCodeRaisesWithMessage()
    {
        var head = Pb.Cat(Pb.VarintField(2, 7), Pb.LenField(3, "凭证过期"));

        var error = Assert.Throws<IOException>(
            () => RKey.ParseResponse(Envelope(Pb.LenField(1, head))));
        Assert.Contains("凭证过期", error.Message);
    }

    [Fact]
    public void RKeySceneFollowsTheAppIdInTheUrlNotTheConversation()
    {
        const string private_ = "https://multimedia.nt.qq.com.cn/download?appid=1406&fileid=x";
        const string group = "https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=x";

        Assert.Equal(RKey.TypePrivateImage, RKey.TypeForUrl(private_, isGroup: true));
        Assert.Equal(RKey.TypeGroupImage, RKey.TypeForUrl(group, isGroup: false));
        Assert.Equal(RKey.TypeGroupImage, RKey.TypeForUrl("https://x/y", isGroup: true));
    }

    [Fact]
    public void UrlNeedsRKeyOnlyForUnsignedNtAddresses()
    {
        Assert.True(RKey.UrlNeedsRKey("https://multimedia.nt.qq.com.cn/download?appid=1407"));
        Assert.False(RKey.UrlNeedsRKey("https://multimedia.nt.qq.com.cn/download?rkey=K"));
        Assert.False(RKey.UrlNeedsRKey("http://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0"));
        Assert.False(RKey.UrlNeedsRKey(""));
    }
}

public class RKeyCacheTests
{
    /// <summary>可拨动的时钟：过期与冷却都是时间行为，不注入就只能靠 sleep 验。</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public DateTimeOffset Read() => Now;
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void ServesFromMemoryAndOnlyRefetchesAfterExpiry()
    {
        var calls = new List<int>();
        var clock = new FakeClock();
        var cache = new RKeyCache(7, pid =>
        {
            calls.Add(pid);
            return [new RKeyInfo("&rkey=G", RKey.TypeGroupImage, TtlSeconds: 3600)];
        }, clock.Read);

        Assert.Equal("&rkey=G", cache.Get(RKey.TypeGroupImage));
        Assert.Equal("&rkey=G", cache.Get(RKey.TypeGroupImage));
        Assert.Equal(new[] { 7 }, calls);

        clock.Advance(3600);
        Assert.Equal("&rkey=G", cache.Get(RKey.TypeGroupImage));
        Assert.Equal(new[] { 7, 7 }, calls);
    }

    [Fact]
    public void FallsBackToTheGenericImageRKey()
    {
        var cache = new RKeyCache(7,
            _ => [new RKeyInfo("&rkey=F", RKey.TypeFallbackImage, TtlSeconds: 3600)]);

        Assert.Equal("&rkey=F", cache.Get(RKey.TypeGroupImage));
    }

    [Fact]
    public void BacksOffWhenTheFetchKeepsFailing()
    {
        var calls = new List<int>();
        var cache = new RKeyCache(7, pid =>
        {
            calls.Add(pid);
            throw new IOException("管道不可用");
        });

        for (var i = 0; i < 3; i++)
        {
            try { cache.Get(RKey.TypeGroupImage); }
            catch (IOException) { /* 冷却期内不应再次触发 fetch */ }
        }

        Assert.Single(calls);
    }

    [Fact]
    public void ExpiredEntryIsNotServed()
    {
        var clock = new FakeClock();
        var cache = new RKeyCache(7,
            _ => [new RKeyInfo("&rkey=OLD", RKey.TypeGroupImage, TtlSeconds: 1)],
            clock.Read);

        // ttl=1 已在 60 秒的容差之内，取回来的当场就算过期。
        Assert.Equal("", cache.Get(RKey.TypeGroupImage));
    }

    /// <summary>回归测试：TtlSeconds 直接来自 wire 整数，没有边界保证——离谱的 ttl
    /// 会让 AddSeconds 溢出 DateTimeOffset.MaxValue 抛 ArgumentOutOfRangeException。
    /// 一条 rkey 的过期时间算不出来只能跳过它，不能让一条脏数据拖累同一批里
    /// 其他类型也缓存不上。</summary>
    [Fact]
    public void AbsurdTtlOnOneEntryDoesNotBreakTheWholeBatch()
    {
        var cache = new RKeyCache(7, _ =>
        [
            new RKeyInfo("&rkey=BAD", RKey.TypeGroupImage, TtlSeconds: long.MaxValue),
            new RKeyInfo("&rkey=GOOD", RKey.TypePrivateImage, TtlSeconds: 3600),
        ]);

        var exception = Record.Exception(() => cache.Get(RKey.TypeGroupImage));

        Assert.Null(exception);
        Assert.Equal("", cache.Get(RKey.TypeGroupImage)); // 这条没法算过期时间，缓存不上
        Assert.Equal("&rkey=GOOD", cache.Get(RKey.TypePrivateImage)); // 同批里其他条目不受影响
    }

    [Fact]
    public void NegativeTtlIsRejectedInsteadOfProducingAnAlreadyExpiredEntry()
    {
        // 之前用 "!= 0" 当判空条件，负数会原样通过、AddSeconds 得到一个已经在过去
        // 的时间点——这条 rkey 存进去也立刻被判定过期，之后每次冷却结束都重新
        // 请求同一条坏数据，陷入死循环。
        var calls = 0;
        var cache = new RKeyCache(7, _ =>
        {
            calls++;
            return [new RKeyInfo("&rkey=BAD", RKey.TypeGroupImage, TtlSeconds: -1)];
        });

        Assert.Equal("", cache.Get(RKey.TypeGroupImage));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void BackwardClockJumpDoesNotPermanentlyStallRefresh()
    {
        // 时钟回拨（迟到的 NTP 校正）之前会让 now - last 恒为负数，永久卡住冷却
        // 判断——这里模拟一次请求失败后，挂钟突然往回跳，确认刷新仍然可以立刻
        // 重试，而不是要等到挂钟重新追上原来的时间点。
        var calls = 0;
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch.AddSeconds(10_000) };
        var cache = new RKeyCache(7, _ =>
        {
            calls++;
            throw new IOException("管道不可用");
        }, clock.Read);

        Assert.Throws<IOException>(() => cache.Get(RKey.TypeGroupImage));
        Assert.Equal(1, calls);

        clock.Now = DateTimeOffset.UnixEpoch.AddSeconds(1); // 挂钟向后跳了将近 3 小时

        Assert.Throws<IOException>(() => cache.Get(RKey.TypeGroupImage));
        Assert.Equal(2, calls); // 没有被回拨卡住，正常发起了第二次尝试
    }

    /// <summary>回归测试：过期判断以前拿服务器的 CreateTime 起算、却用本地 now 去比——
    /// 教室电脑没接 NTP、本地时钟比服务器快一截并不罕见，一快过 60 秒的容差，
    /// 刚拿到手的 rkey 当场就被判定过期，图片永远显示 invalid rkey。现在过期
    /// 时间统一从本地时钟起算，两头基准一致，本地时钟快慢都不影响这个判断。</summary>
    [Fact]
    public void ServerCreateTimeDoesNotAffectExpiryEvenWhenClocksDisagree()
    {
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch.AddSeconds(10_000) };
        // 服务器说这条 rkey 是纪元附近签的：拿它当基准算过期时间的话，
        // 加上 3600 秒 ttl 也早就该过期了（本地时钟已经走到 10000 秒）。
        var cache = new RKeyCache(7,
            _ => [new RKeyInfo("&rkey=FRESH", RKey.TypeGroupImage, TtlSeconds: 3600, CreateTime: 1)],
            clock.Read);

        // 只要过期时间是从本地时钟起算而不是服务器的 CreateTime，这条就该是新鲜的。
        Assert.Equal("&rkey=FRESH", cache.Get(RKey.TypeGroupImage));
    }
}

public class FileResolverTests
{
    [Fact]
    public void GroupFileUrlRequestAndResponse()
    {
        var request = Wire.DecodeFields(FileResolver.BuildGroupRequest(123456, "file-id"));
        Assert.Equal(0x6D6UL, Wire.FieldInt(request, 1));
        Assert.Equal(2UL, Wire.FieldInt(request, 2));
        Assert.Equal(1UL, Wire.FieldInt(request, 12));

        var body = Wire.DecodeFields(Wire.FieldBytes(request, 4));
        var download = Wire.DecodeFields(Wire.FieldBytes(body, 3));
        Assert.Equal(123456UL, Wire.FieldInt(download, 1));
        Assert.Equal(7UL, Wire.FieldInt(download, 2));
        Assert.Equal(102UL, Wire.FieldInt(download, 3));
        Assert.Equal("file-id", Wire.FieldString(download, 4));

        var downloadResponse = Pb.Cat(
            Pb.VarintField(1, 0),
            Pb.LenField(5, "file.qq.com"),
            Pb.LenField(6, new byte[] { 0x01, 0xAB }));
        var envelope = Pb.Cat(
            Pb.VarintField(3, 0),
            Pb.LenField(4, Pb.LenField(3, downloadResponse)));

        Assert.Equal(
            "https://file.qq.com/ftn_handler/01AB/?fname=file%20id",
            FileResolver.ParseGroupResponse(envelope, "file id"));
    }

    [Fact]
    public void PrivateFileUrlRequestAndResponse()
    {
        var request = Wire.DecodeFields(
            FileResolver.BuildPrivateRequest("uid-self", "uuid", "hash"));
        Assert.Equal(0xE37UL, Wire.FieldInt(request, 1));
        Assert.Equal(1200UL, Wire.FieldInt(request, 2));

        var body = Wire.DecodeFields(Wire.FieldBytes(request, 4));
        var inner = Wire.DecodeFields(Wire.FieldBytes(body, 14));
        Assert.Equal("uid-self", Wire.FieldString(inner, 10));
        Assert.Equal("uuid", Wire.FieldString(inner, 20));
        Assert.Equal("hash", Wire.FieldString(inner, 60));

        var result = Pb.Cat(
            Pb.LenField(20, "127.0.0.1"),
            Pb.VarintField(40, 8080),
            Pb.LenField(50, "/download?id=1"));
        var envelope = Pb.Cat(
            Pb.VarintField(3, 0),
            Pb.LenField(4, Pb.LenField(14, Pb.LenField(30, result))));

        Assert.Equal(
            "http://127.0.0.1:8080/download?id=1&isthumb=0",
            FileResolver.ParsePrivateResponse(envelope));
    }

    [Fact]
    public void FileUrlResponsesRejectUnsafeHosts()
    {
        var groupDownload = Pb.Cat(Pb.LenField(5, "host/path"), Pb.LenField(6, "x"));
        var groupEnvelope = Pb.LenField(4, Pb.LenField(3, groupDownload));
        Assert.Throws<InvalidDataException>(
            () => FileResolver.ParseGroupResponse(groupEnvelope, "id"));

        var privateResult = Pb.Cat(
            Pb.LenField(20, "host@evil"),
            Pb.VarintField(40, 80),
            Pb.LenField(50, "/x"));
        var privateEnvelope = Pb.LenField(4, Pb.LenField(14, Pb.LenField(30, privateResult)));
        Assert.Throws<InvalidDataException>(
            () => FileResolver.ParsePrivateResponse(privateEnvelope));
    }
}

public class ProfileResolverTests
{
    [Fact]
    public void RequestUsesUinFormAndAsksForRemarkAndNickname()
    {
        var envelope = Wire.DecodeFields(ProfileResolver.BuildRequest("123456"));

        Assert.Equal(0xFE1UL, Wire.FieldInt(envelope, 1));
        Assert.Equal(2UL, Wire.FieldInt(envelope, 2));
        Assert.Equal(1UL, Wire.FieldInt(envelope, 12));

        var body = Wire.DecodeFields(Wire.FieldBytes(envelope, 4));
        Assert.Equal(123456UL, Wire.FieldInt(body, 1));

        var keys = Wire.Repeated(body, 3)
            .Select(item => (int)Wire.FieldInt(Wire.DecodeFields(Wire.AsBytes(item)), 1))
            .ToHashSet();
        Assert.Equal(new HashSet<int> { 103, 20002 }, keys);
    }

    [Fact]
    public void RequestUsesUidFormWhenUinIsUnavailable()
    {
        var envelope = Wire.DecodeFields(ProfileResolver.BuildRequest("u_example"));
        var body = Wire.DecodeFields(Wire.FieldBytes(envelope, 4));

        Assert.Equal(0UL, Wire.FieldInt(envelope, 12));
        Assert.Equal("u_example", Wire.FieldString(body, 1));
    }

    [Fact]
    public void ResponseExtractsRemarkAndNickname()
    {
        var nickname = Pb.Cat(Pb.VarintField(1, 20002), Pb.LenField(2, "基础昵称"));
        var remark = Pb.Cat(Pb.VarintField(1, 103), Pb.LenField(2, "好友备注"));
        var properties = Pb.Cat(Pb.LenField(2, nickname), Pb.LenField(2, remark));
        var envelope = Pb.Cat(
            Pb.VarintField(3, 0),
            Pb.LenField(4, Pb.LenField(1, Pb.LenField(2, properties))));

        var names = ProfileResolver.ParseResponse(envelope);

        Assert.Equal("基础昵称", names.Nickname);
        Assert.Equal("好友备注", names.Remark);
    }
}
