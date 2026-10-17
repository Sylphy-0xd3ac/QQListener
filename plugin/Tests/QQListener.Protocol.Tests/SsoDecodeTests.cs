namespace QQListener.Protocol.Tests;

/// <summary>移植自 Python 版 tests/native/test_sso_decode.py 与 test_model.py。</summary>
public class SsoDecodeTests
{
    private const string PushCmd = "trpc.msg.olpush.OlPushService.MsgPush";

    /// <summary>拼一个 PushMsg：message{1} { responseHead{1}, contentHead{2}, body{3} }。</summary>
    private static byte[] PushMsg(byte[] responseHead, byte[] contentHead, byte[] richTextElems)
    {
        var body = Pb.LenField(1, Pb.Rich(richTextElems));   // MessageBody.richText
        var message = Pb.Cat(
            Pb.LenField(1, responseHead),
            Pb.LenField(2, contentHead),
            Pb.LenField(3, body));
        return Pb.LenField(1, message);
    }

    private static RecvPacket Packet(byte[] body, string uin = "10000") =>
        new(Seq: 1, Error: 0, Cmd: PushCmd, Uin: uin, Body: body);

    [Fact]
    public void GroupMessage_DecodesPeerAndSender()
    {
        var group = Pb.Cat(
            Pb.VarintField(1, 123456),        // groupUin
            Pb.LenField(4, "群名片"),          // memberCard
            Pb.LenField(7, "高三2班"));        // groupName
        var responseHead = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, group));
        var contentHead = Pb.Cat(Pb.VarintField(1, 82), Pb.VarintField(5, 6243));

        var msg = SsoDecode.DecodeMessagePush(
            Packet(PushMsg(responseHead, contentHead, Pb.TextElem("明天带作业"))));

        Assert.NotNull(msg);
        Assert.Equal("group", msg!.Scene);
        Assert.Equal("123456", msg.PeerId);
        Assert.Equal("高三2班", msg.PeerName);
        Assert.Equal("1001", msg.SenderId);
        Assert.Equal("群名片", msg.SenderGroupCard);
        Assert.Equal(6243UL, msg.RawSeq);            // 群聊用 ContentHead.sequence
        Assert.Equal("明天带作业", MessageText.Body(msg));
    }

    [Fact]
    public void PrivateMessage_ResolvesPeerFromDirection()
    {
        // 我方发出：fromUin == 自己，对端应取 toUin / toUid
        var responseHead = Pb.Cat(
            Pb.VarintField(1, 10000), Pb.LenField(2, "u_self"),
            Pb.VarintField(5, 20001), Pb.LenField(6, "u_peer"));
        var contentHead = Pb.Cat(Pb.VarintField(1, 166), Pb.VarintField(11, 777));

        var msg = SsoDecode.DecodeMessagePush(
            Packet(PushMsg(responseHead, contentHead, Pb.TextElem("你好")), uin: "10000"));

        Assert.NotNull(msg);
        Assert.Equal("c2c", msg!.Scene);
        Assert.Equal("20001", msg.PeerId);
        Assert.Equal("u_peer", msg.PeerUid);
        Assert.Equal("u_self", msg.AccountUid);
        Assert.Equal(777UL, msg.RawSeq);             // 私聊优先 ntMsgSeq
    }

    [Fact]
    public void PrivateMessage_Incoming_PeerIsSender()
    {
        var responseHead = Pb.Cat(
            Pb.VarintField(1, 20001), Pb.LenField(2, "u_peer"),
            Pb.VarintField(5, 10000), Pb.LenField(6, "u_self"));
        var contentHead = Pb.VarintField(1, 166);

        var msg = SsoDecode.DecodeMessagePush(
            Packet(PushMsg(responseHead, contentHead, Pb.TextElem("在吗")), uin: "10000"));

        Assert.Equal("20001", msg!.PeerId);
        Assert.Equal("u_peer", msg.PeerUid);
        Assert.Equal("u_self", msg.AccountUid);
    }

    [Fact]
    public void MissingProtocolSequence_StaysZeroInsteadOfBorrowingTheTransportFrameCounter()
    {
        // packet.Seq 是 HQP1 帧的传输层计数器，跟 ContentHead 里的协议序列号是完全不同的
        // 命名空间——RawSeq 会被直接当成群回复的 SrcMsg 引用序号发回给 QQ，绝不能拿一个
        // 瞎凑的传输层计数器顶替，否则等于让"回复"悄悄引用到一条不相干的消息。
        var responseHead = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, Pb.VarintField(1, 123456)));
        var contentHead = Pb.VarintField(1, 82); // 没有 sequence(5) / ntMsgSeq(11) 字段
        var packet = new RecvPacket(Seq: 999, Error: 0, Cmd: PushCmd, Uin: "10000",
            Body: PushMsg(responseHead, contentHead, Pb.TextElem("x")));

        var msg = SsoDecode.DecodeMessagePush(packet);

        Assert.Equal(0UL, msg!.RawSeq);
    }

    [Fact]
    public void GroupMessage_AlsoResolvesSelfUin()
    {
        // SelfUin 之前只在私聊分支里算，群聊分支完全没有——"有人 @ 我" 的判定
        // 需要拿它跟 Segment("at").TargetId 比对，群聊消息同样要用到。
        var group = Pb.Cat(Pb.VarintField(1, 123456), Pb.LenField(7, "高三2班"));
        var responseHead = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, group));
        var contentHead = Pb.VarintField(1, 82);

        var msg = SsoDecode.DecodeMessagePush(
            Packet(PushMsg(responseHead, contentHead, Pb.TextElem("x")), uin: "20001"));

        Assert.Equal(20001UL, msg!.SelfUin);
    }

    [Fact]
    public void MissingProtocolSequence_PrivateScene_StaysZero()
    {
        // 上一条测试只覆盖了群聊分支（isGroup ? sequence）；私聊分支
        // （ntSequence != 0 ? ntSequence : sequence）在两个字段都缺失时同样得留 0。
        var responseHead = Pb.Cat(
            Pb.VarintField(1, 10000), Pb.LenField(2, "u_self"),
            Pb.VarintField(5, 20001), Pb.LenField(6, "u_peer"));
        var contentHead = Pb.VarintField(1, 166); // 没有 sequence(5) / ntMsgSeq(11)
        var packet = new RecvPacket(Seq: 999, Error: 0, Cmd: PushCmd, Uin: "10000",
            Body: PushMsg(responseHead, contentHead, Pb.TextElem("x")));

        var msg = SsoDecode.DecodeMessagePush(packet);

        Assert.Equal("c2c", msg!.Scene);
        Assert.Equal(0UL, msg.RawSeq);
    }

    [Theory]
    [InlineData(528, 138)] // Friend recall
    [InlineData(528, 139)] // Self recall
    [InlineData(732, 17)]  // Group recall, even with group routing and text
    [InlineData(732, 20)]
    [InlineData(33, 0)]
    [InlineData(84, 0)]
    [InlineData(0, 0)]
    [InlineData(9999, 0)]
    public void NonChatEventsAreNeverRendered(int type, int subType)
    {
        var response = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, Pb.VarintField(1, 123456)));
        var content = Pb.Cat(Pb.VarintField(1, (ulong)type), Pb.VarintField(2, (ulong)subType));
        Assert.Null(SsoDecode.DecodeMessagePush(Packet(PushMsg(response, content, Pb.TextElem("系统提示")))));
    }

    [Theory]
    [InlineData(1)] [InlineData(73)] [InlineData(75)] [InlineData(129)]
    [InlineData(131)] [InlineData(133)] [InlineData(135)] [InlineData(192)]
    public void PrivateControlPushIsIgnoredEvenWithText(int command)
    {
        var content = Pb.Cat(Pb.VarintField(1, 166), Pb.VarintField(3, (ulong)command));
        Assert.Null(SsoDecode.DecodeMessagePush(Packet(PushMsg(Pb.VarintField(1, 1001), content, Pb.TextElem("x")))));
    }

    [Theory]
    [InlineData(9)] [InlineData(141)] [InlineData(166)] [InlineData(167)]
    [InlineData(208)] [InlineData(529)]
    public void SupportedPrivateMessageKindsRemainMessages(int type)
    {
        var content = Pb.VarintField(1, (ulong)type);
        var result = SsoDecode.DecodeMessagePush(Packet(PushMsg(Pb.VarintField(1, 1001), content, Pb.TextElem("你好"))));
        Assert.NotNull(result);
        Assert.Equal("c2c", result.Scene);
    }

    [Fact]
    public void EmptyAndIdentitylessPushesAreIgnored()
    {
        Assert.Null(SsoDecode.DecodeMessagePush(Packet(PushMsg(Pb.VarintField(1, 1001), Pb.VarintField(1, 166), Pb.TextElem("  ")))));
        Assert.Null(SsoDecode.DecodeMessagePush(Packet(PushMsg([], Pb.VarintField(1, 166), Pb.TextElem("x")))));
    }

    [Fact]
    public void OfflineFileReceiptIsIgnoredEvenWhenItCarriesText()
    {
        var rich = Pb.Cat(Pb.Rich(Pb.TextElem("文件")), Pb.LenField(3, Pb.VarintField(12, 2)));
        var message = Pb.Cat(Pb.LenField(1, Pb.VarintField(1, 1001)),
            Pb.LenField(2, Pb.VarintField(1, 529)), Pb.LenField(3, Pb.LenField(1, rich)));
        Assert.Null(SsoDecode.DecodeMessagePush(Packet(Pb.LenField(1, message))));
    }

    [Fact]
    public void VoiceMessageWithNoTextIsStillAChatMessage()
    {
        var rich = Pb.LenField(4, Pb.Cat(Pb.LenField(5, "语音"), Pb.VarintField(19, 5)));
        var message = Pb.Cat(Pb.LenField(1, Pb.VarintField(1, 1001)),
            Pb.LenField(2, Pb.VarintField(1, 208)), Pb.LenField(3, Pb.LenField(1, rich)));
        var decoded = SsoDecode.DecodeMessagePush(Packet(Pb.LenField(1, message)));
        Assert.NotNull(decoded);
        Assert.Equal("record", Assert.Single(decoded.Segments).Type);
    }

    [Fact]
    public void NonMessageCommand_IsIgnored()
    {
        var packet = new RecvPacket(1, 0, "Heartbeat.Alive", "10000", [0x01]);
        Assert.Null(SsoDecode.DecodeMessagePush(packet));
    }

    [Fact]
    public void EmptyBody_IsIgnored()
    {
        Assert.Null(SsoDecode.DecodeMessagePush(Packet([])));
    }

    [Fact]
    public void Timestamp_IsCarriedForQuoting()
    {
        var responseHead = Pb.Cat(Pb.VarintField(1, 1001),
            Pb.LenField(8, Pb.VarintField(1, 123456)));
        var contentHead = Pb.Cat(Pb.VarintField(1, 82), Pb.VarintField(5, 42), Pb.VarintField(6, 1700000000));

        var msg = SsoDecode.DecodeMessagePush(
            Packet(PushMsg(responseHead, contentHead, Pb.TextElem("x"))));

        Assert.Equal(1700000000, msg!.Timestamp);
    }
}

/// <summary>移植自 Python 版 tests/native/test_model.py。</summary>
public class MessageTextTests
{
    private static CapturedMessage Msg(params Segment[] segments) =>
        new() { Scene = "group", PeerId = "123", SenderId = "1001", Segments = segments };

    [Fact]
    public void RendersEachSegmentKind()
    {
        var msg = Msg(
            new Segment { Type = "text", Text = "明天带这个" },
            new Segment { Type = "image", Url = "http://x" },
            new Segment { Type = "file", Name = "作业.docx" },
            new Segment { Type = "at", Text = "李四", TargetId = "1002" });

        Assert.Equal("明天带这个[图片][文件] 作业.docx[@李四]", MessageText.Body(msg));
    }

    [Fact]
    public void WireStyleMention_IsNotDoublePrefixed()
    {
        // 线上的 @ 文本自带 "@" 和尾随空格
        var msg = Msg(new Segment { Type = "at", Text = "@李四 ", TargetId = "1002" });
        Assert.Equal("[@李四]", MessageText.Body(msg));
    }

    [Fact]
    public void ReplySegment_ExcludedFromBodyAndExposedSeparately()
    {
        List<Segment> quoted = [new() { Type = "text", Text = "昨天的作业交了吗" }];
        var msg = Msg(
            new Segment
            {
                Type = "reply",
                Extra =
                {
                    ["seq"] = 42UL, ["time"] = 1700000000,
                    ["sender_id"] = "1002", ["sender_name"] = "李四",
                    ["segments"] = quoted
                }
            },
            new Segment { Type = "text", Text = "交了" });

        Assert.Equal("交了", MessageText.Body(msg));

        var quote = MessageText.Quoted(msg);
        Assert.NotNull(quote);
        Assert.Equal("1002", quote!.SenderId);
        Assert.Equal("李四", quote.SenderName);
        Assert.Equal(42UL, quote.Seq);
        Assert.Equal(quoted, quote.Segments);
    }

    [Fact]
    public void NoReply_YieldsNullQuote() =>
        Assert.Null(MessageText.Quoted(Msg(new Segment { Type = "text", Text = "你好" })));

    [Fact]
    public void UnknownSegmentKind_RendersAsBracketedType() =>
        Assert.Equal("[poke]", MessageText.Body(Msg(new Segment { Type = "poke" })));
}
