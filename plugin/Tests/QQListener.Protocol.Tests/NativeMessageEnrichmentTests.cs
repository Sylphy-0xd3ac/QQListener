using QQListener.Oidb;
using QQListener.Pipes;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

/// <summary>
/// <see cref="NativeMessageEnrichment"/>：发送者备注、图片 rkey、文件下载地址的补全。
/// 之前这整条链路完全没有被生产代码调用——图片签不到 rkey 会一直显示
/// "invalid rkey"，文件消息永远没有下载地址，"备注优先"永远用不上。
/// </summary>
public class NativeMessageEnrichmentTests
{
    private static byte[] AckAndReply(byte[] body) => Pb.Cat(
        Hqp1.EncodeFrame(PipeOp.Hello),
        Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
        Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1, body: body));

    /// <summary>每次 QqControlApi.Request 都会新连一次管道；按调用顺序发不同的响应。</summary>
    private static QqControlApi ApiWithResponses(params byte[][] responseBodies)
    {
        var queue = new Queue<byte[]>(responseBodies);
        return new QqControlApi(_ => new FakeTransport([AckAndReply(queue.Dequeue())]));
    }

    private static CapturedMessage GroupMessage(params Segment[] segments) => new()
    {
        Scene = "group",
        PeerId = "123456",
        SenderId = "1001",
        SourcePid = 7,
        Segments = segments,
    };

    // ---------- 发送者备注 ----------

    private static byte[] ProfileResponse(string nickname, string remark)
    {
        var nicknameProp = Pb.Cat(Pb.VarintField(1, 20002), Pb.LenField(2, nickname));
        var remarkProp = Pb.Cat(Pb.VarintField(1, 103), Pb.LenField(2, remark));
        var properties = Pb.Cat(Pb.LenField(2, nicknameProp), Pb.LenField(2, remarkProp));
        return Pb.Cat(
            Pb.VarintField(3, 0),
            Pb.LenField(4, Pb.LenField(1, Pb.LenField(2, properties))));
    }

    [Fact]
    public void EnrichSenderProfileFillsRemarkWhenPushDidNotCarryOne()
    {
        var enrichment = new NativeMessageEnrichment(ApiWithResponses(ProfileResponse("昵称", "备注名")));
        var message = GroupMessage();

        enrichment.EnrichSenderProfile(message);

        Assert.Equal("备注名", message.SenderRemark);
    }

    [Fact]
    public void EnrichSenderProfileSkipsTheQueryWhenRemarkAlreadyKnown()
    {
        var enrichment = new NativeMessageEnrichment(
            new QqControlApi(_ => throw new InvalidOperationException("不该查资料")));
        var message = new CapturedMessage
        {
            Scene = "group", PeerId = "1", SenderId = "1001", SourcePid = 7, SenderRemark = "已经有了",
        };

        enrichment.EnrichSenderProfile(message);

        Assert.Equal("已经有了", message.SenderRemark);
    }

    [Fact]
    public void EnrichSenderProfileDegradesGracefullyWhenTheQueryFails()
    {
        var enrichment = new NativeMessageEnrichment(new QqControlApi(_ => throw new IOException("管道炸了")));
        var message = GroupMessage();

        enrichment.EnrichSenderProfile(message); // 不应该抛异常

        Assert.Equal("", message.SenderRemark);
    }

    // ---------- 图片 rkey ----------

    private static byte[] RKeyEntry(string rkey, int type) => Pb.LenField(1, Pb.Cat(
        Pb.LenField(1, rkey), Pb.VarintField(2, 3600), Pb.VarintField(4, 0), Pb.VarintField(5, (ulong)type)));

    private static byte[] RKeyResponse(string rkey, int type) => Pb.Cat(
        Pb.VarintField(3, 0), Pb.LenField(4, Pb.LenField(4, RKeyEntry(rkey, type))));

    [Fact]
    public void EnrichSegmentsAppendsRKeyToAnUnsignedNtImageUrl()
    {
        var enrichment = new NativeMessageEnrichment(
            ApiWithResponses(RKeyResponse("&rkey=SIGNED", QQListener.Oidb.RKey.TypeGroupImage)));
        var image = new Segment { Type = "image", Url = "https://multimedia.nt.qq.com.cn/download?appid=1407" };
        var message = GroupMessage(image);

        enrichment.EnrichSegments(message);

        Assert.Contains("rkey=SIGNED", image.Url);
    }

    [Fact]
    public void EnrichSegmentsLeavesAnAlreadySignedUrlAlone()
    {
        var enrichment = new NativeMessageEnrichment(
            new QqControlApi(_ => throw new InvalidOperationException("已经带 rkey 不该再查")));
        var image = new Segment { Type = "image", Url = "https://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0" };
        var message = GroupMessage(image);

        enrichment.EnrichSegments(message);

        Assert.Equal("https://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0", image.Url);
    }

    // ---------- 文件下载地址 ----------

    [Fact]
    public void EnrichSegmentsResolvesGroupFileUrl()
    {
        var downloadResponse = Pb.Cat(
            Pb.VarintField(1, 0), Pb.LenField(5, "file.qq.com"), Pb.LenField(6, new byte[] { 0x01, 0xAB }));
        var body = Pb.Cat(Pb.VarintField(3, 0), Pb.LenField(4, Pb.LenField(3, downloadResponse)));
        var enrichment = new NativeMessageEnrichment(ApiWithResponses(body));

        var file = new Segment
        {
            Type = "file",
            Extra = { ["file_id"] = "file-id", ["bus_id"] = 102UL },
        };
        var message = GroupMessage(file);

        enrichment.EnrichSegments(message);

        Assert.Equal("https://file.qq.com/ftn_handler/01AB/?fname=file-id", file.Url);
    }

    [Fact]
    public void EnrichSegmentsResolvesPrivateFileUrl()
    {
        var result = Pb.Cat(
            Pb.LenField(20, "127.0.0.1"), Pb.VarintField(40, 8080), Pb.LenField(50, "/download?id=1"));
        var body = Pb.Cat(Pb.VarintField(3, 0), Pb.LenField(4, Pb.LenField(14, Pb.LenField(30, result))));
        var enrichment = new NativeMessageEnrichment(ApiWithResponses(body));

        var file = new Segment
        {
            Type = "file",
            Extra = { ["file_id"] = "file-id", ["file_hash"] = "hash" },
        };
        var message = new CapturedMessage
        {
            Scene = "c2c", PeerId = "10001", SenderId = "10001", SourcePid = 7,
            AccountUid = "u_self", Segments = [file],
        };

        enrichment.EnrichSegments(message);

        Assert.Equal("http://127.0.0.1:8080/download?id=1&isthumb=0", file.Url);
    }

    [Fact]
    public void EnrichSegmentsLeavesFileUrlEmptyWhenResolutionFails()
    {
        var enrichment = new NativeMessageEnrichment(new QqControlApi(_ => throw new IOException("管道炸了")));
        var file = new Segment { Type = "file", Extra = { ["file_id"] = "file-id" } };
        var message = GroupMessage(file);

        enrichment.EnrichSegments(message); // 不应该抛异常

        Assert.Equal("", file.Url);
    }

    [Fact]
    public void ForgetPidDropsTheCachedRKeysForThatProcess()
    {
        // Windows 会复用 pid，同一个 QQ 进程也可能换账号登录；不清理的话新进程/
        // 新账号会直接沿用上一个账号缓存下来的 rkey。ForgetPid 之后同一个 pid
        // 应该触发全新的 fetch，而不是继续吐出旧账号的密钥。
        var enrichment = new NativeMessageEnrichment(
            ApiWithResponses(
                RKeyResponse("&rkey=OLD_ACCOUNT", QQListener.Oidb.RKey.TypeGroupImage),
                RKeyResponse("&rkey=NEW_ACCOUNT", QQListener.Oidb.RKey.TypeGroupImage)));

        var firstImage = new Segment { Type = "image", Url = "https://multimedia.nt.qq.com.cn/download?appid=1407" };
        enrichment.EnrichSegments(GroupMessage(firstImage));
        Assert.Contains("rkey=OLD_ACCOUNT", firstImage.Url);

        enrichment.ForgetPid(7);

        var secondImage = new Segment { Type = "image", Url = "https://multimedia.nt.qq.com.cn/download?appid=1407" };
        enrichment.EnrichSegments(GroupMessage(secondImage));
        Assert.Contains("rkey=NEW_ACCOUNT", secondImage.Url);
    }

    [Fact]
    public void EnrichSegmentsSkipsSourcePidZero()
    {
        var enrichment = new NativeMessageEnrichment(
            new QqControlApi(_ => throw new InvalidOperationException("没有 pid 不该发请求")));
        var image = new Segment { Type = "image", Url = "https://multimedia.nt.qq.com.cn/download?appid=1407" };
        var message = new CapturedMessage { Scene = "group", PeerId = "1", SenderId = "1", Segments = [image] };

        enrichment.EnrichSegments(message);

        Assert.Equal("https://multimedia.nt.qq.com.cn/download?appid=1407", image.Url);
    }
}
