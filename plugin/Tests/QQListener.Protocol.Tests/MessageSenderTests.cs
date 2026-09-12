using QQListener.Oidb;
using QQListener.Pipes;

namespace QQListener.Protocol.Tests;

/// <summary>发送线格式。对应 Python 侧 test_message_sender.py。</summary>
public class MessageSenderTests
{
    /// <summary>取出 RichText 里的元素列表。</summary>
    private static List<Dictionary<int, List<WireValue>>> Elems(byte[] request)
    {
        var top = Wire.DecodeFields(request);
        var body = Wire.DecodeFields(Wire.FieldBytes(top, 3));
        var rich = Wire.DecodeFields(Wire.FieldBytes(body, 1));
        return [.. Wire.Repeated(rich, 2).Select(v => Wire.DecodeFields(Wire.AsBytes(v)))];
    }

    [Fact]
    public void GroupRequestRoutesByGroupCodeAndCarriesText()
    {
        var request = MessageSender.BuildSendTextRequest("group", 123456, "收到");
        var top = Wire.DecodeFields(request);

        var routing = Wire.DecodeFields(Wire.FieldBytes(top, 1));
        Assert.Equal(123456UL,
            Wire.FieldInt(Wire.DecodeFields(Wire.FieldBytes(routing, 2)), 1));
        Assert.Equal(1UL, Wire.FieldInt(Wire.DecodeFields(Wire.FieldBytes(top, 2)), 1));

        var elems = Elems(request);
        var only = Assert.Single(elems);
        Assert.Equal("收到",
            Wire.FieldString(Wire.DecodeFields(Wire.FieldBytes(only, 1)), 1));
    }

    [Fact]
    public void GroupRequestWithoutAQuoteSequenceSendsPlainText()
    {
        // 这条路径是 SsoDecode 不再借用传输层计数器顶替 RawSeq 之后才变得常见的：
        // quoteSeq 现在可能合法为 0（协议没给到真实序列号），此时不该带 SrcMsg——
        // 这也是 MessageSender 那道 `quoteSeq != 0 && quoteSenderUin != 0` 守卫的
        // 回归保护。
        var request = MessageSender.BuildSendTextRequest(
            "group", 123456, "收到", quoteSeq: 0, quoteSenderUin: 1001, quoteTime: 1700000000);

        var elems = Elems(request);
        var only = Assert.Single(elems);
        Assert.True(only.ContainsKey(1), "quoteSeq 为 0 时不该带 SrcMsg，只发正文");
    }

    [Fact]
    public void GroupReplyPrependsASrcMsgQuoteElement()
    {
        var request = MessageSender.BuildSendTextRequest(
            "group", 123456, "收到",
            quoteSeq: 42, quoteSenderUin: 1001, quoteTime: 1700000000);

        var elems = Elems(request);
        Assert.Equal(2, elems.Count);
        Assert.True(elems[0].ContainsKey(45), "引用元素必须排在正文之前");
        Assert.True(elems[1].ContainsKey(1));

        var src = Wire.DecodeFields(Wire.FieldBytes(elems[0], 45));
        Assert.Equal(42UL, Wire.FieldInt(src, 1));
        Assert.Equal(1001UL, Wire.FieldInt(src, 2));
        Assert.Equal(1700000000UL, Wire.FieldInt(src, 3));
    }

    [Fact]
    public void PrivateRequestUsesC2cRoutingAndC2cCmd11()
    {
        var request = MessageSender.BuildSendTextRequest(
            "c2c", 10001, "收到", peerUid: "u_abc", clientSequence: 5);
        var top = Wire.DecodeFields(request);

        var routing = Wire.DecodeFields(Wire.FieldBytes(top, 1));
        var c2c = Wire.DecodeFields(Wire.FieldBytes(routing, 1));
        Assert.Equal(10001UL, Wire.FieldInt(c2c, 1));
        Assert.Equal("u_abc", Wire.FieldString(c2c, 2));

        var content = Wire.DecodeFields(Wire.FieldBytes(top, 2));
        Assert.Equal(11UL, Wire.FieldInt(content, 3));
        Assert.Equal(5UL, Wire.FieldInt(top, 4));
    }

    [Fact]
    public void EmptyTextAndBadPeerAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => MessageSender.BuildSendTextRequest("group", 1, ""));
        Assert.Throws<ArgumentException>(
            () => MessageSender.BuildSendTextRequest("group", 0, "收到"));
    }

    [Fact]
    public void ResponseReportsRejectionAndSequence()
    {
        var ok = MessageSender.ParseResponse(
            Pb.Cat(Pb.VarintField(1, 0), Pb.VarintField(11, 88)));
        Assert.Equal(new SendResult(0, "", 88), ok);

        var rejected = MessageSender.ParseResponse(
            Pb.Cat(Pb.VarintField(1, 79), Pb.LenField(2, "发送失败")));
        Assert.Equal(79, rejected.Result);
        Assert.Equal("发送失败", rejected.ErrorMessage);
    }
}

/// <summary>发送走 control 管道的那一段：用假传输喂回一帧应答。</summary>
public class SendOverControlPipeTests
{
    private static byte[] AckAndReply(byte[] body, int status = 0, string msg = "") =>
        Pb.Cat(
            Hqp1.EncodeFrame(PipeOp.Hello),
            Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
            Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1, status: status, msg: msg, body: body));

    private static (QqControlApi Api, List<FakeTransport> Opened) Api(params byte[][] replies)
    {
        var opened = new List<FakeTransport>();
        var queue = new Queue<byte[]>(replies);
        return (new QqControlApi(_ =>
        {
            var transport = new FakeTransport([queue.Dequeue()]);
            opened.Add(transport);
            return transport;
        }), opened);
    }

    [Fact]
    public void SendWithoutAQqProcessFailsFast()
    {
        var (api, _) = Api();
        var error = Assert.Throws<InvalidOperationException>(
            () => api.SendTextReply(new ReplyRoute(0, "group", "1"), "收到"));
        Assert.Contains("尚未连接到 QQ 进程", error.Message);
    }

    [Fact]
    public void PrivateSceneNeverSendsAQuote()
    {
        var (api, opened) = Api(AckAndReply(Pb.VarintField(1, 0)));

        api.SendTextReply(
            new ReplyRoute(7, "c2c", "10001", QuoteSeq: 42, QuoteSenderId: "1001"),
            "收到");

        var request = Wire.DecodeFields(RequestBodyOf(opened[0]));
        var rich = Wire.DecodeFields(
            Wire.FieldBytes(Wire.DecodeFields(Wire.FieldBytes(request, 3)), 1));
        // 只有正文，没有 SrcMsg：私聊的 origSeqs 语义不同，引用会发歪。
        var elem = Assert.Single(Wire.Repeated(rich, 2));
        Assert.Equal(new[] { 1 }, Wire.DecodeFields(Wire.AsBytes(elem)).Keys.ToArray());
    }

    [Fact]
    public void GroupSceneKeepsTheQuote()
    {
        var (api, opened) = Api(AckAndReply(Pb.VarintField(1, 0)));

        api.SendTextReply(
            new ReplyRoute(7, "group", "123456", QuoteSeq: 42, QuoteSenderId: "1001"),
            "收到");

        var request = Wire.DecodeFields(RequestBodyOf(opened[0]));
        var rich = Wire.DecodeFields(
            Wire.FieldBytes(Wire.DecodeFields(Wire.FieldBytes(request, 3)), 1));
        Assert.Equal(2, Wire.Repeated(rich, 2).Count);
    }

    [Fact]
    public void RejectedSendSurfacesTheServerMessage()
    {
        var (api, _) = Api(AckAndReply(
            Pb.Cat(Pb.VarintField(1, 79), Pb.LenField(2, "你已被禁言"))));

        var error = Assert.Throws<IOException>(
            () => api.SendTextReply(new ReplyRoute(7, "group", "123456"), "收到"));
        Assert.Contains("你已被禁言", error.Message);
    }

    [Fact]
    public void PipeLevelErrorSurfacesToo()
    {
        var (api, _) = Api(AckAndReply([], status: 5, msg: "hook not ready"));

        var error = Assert.Throws<IOException>(
            () => api.SendTextReply(new ReplyRoute(7, "group", "123456"), "收到"));
        Assert.Contains("hook not ready", error.Message);
    }

    /// <summary>假传输记下写出去的那一帧，把 body 拆出来。</summary>
    private static byte[] RequestBodyOf(FakeTransport transport)
    {
        var frames = new FrameReader().Push(Pb.Cat([.. transport.Writes]));
        var request = Assert.Single(frames);
        Assert.Equal((int)PipeOp.SendRequest, request.Op);
        Assert.Equal(MessageSender.SendMsgCmd, request.Cmd);
        return request.Body;
    }
}
