using QQListener.Models;
using QQListener.Pipes;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public class MessageReplyServiceTests
{
    private static MessageEvent NativeMessage(string scene = "group", int pid = 7) => new(
        "qq.native", "QQ", "张三", "高三2班", "收到", DateTimeOffset.FromUnixTimeSeconds(1700000000),
        MessageImportance.Normal, ["收到"], "dedup")
    {
        Scene = scene,
        SourcePid = pid,
        SenderId = "1001",
        ConversationId = "123456",
        PeerUid = "u_group",
        RawSeq = 42,
    };

    private static MessageEvent ToastMessage() => new(
        "qq.toast", "QQ", "张三", "张三", "收到", DateTimeOffset.UtcNow,
        MessageImportance.Normal, ["收到"], "dedup");

    [Fact]
    public void NativeMessagesCanReplyToastMessagesCannot()
    {
        Assert.True(NativeMessage().CanReply);
        Assert.False(ToastMessage().CanReply);
    }

    [Fact]
    public void UidOnlyPrivateMessagesCannotReply()
    {
        // SsoDecode 在解不出对方数字账号时会退化用 uid 字符串顶上 ConversationId，
        // 但 MessageSender 硬性要求 peerUin > 0——纯 uid 发不出去，CanReply 得说 false，
        // 不能让 UI 显示一个点了必然失败的"回复"入口。
        var uidOnly = NativeMessage(scene: "c2c") with { ConversationId = "u_peer_only" };

        Assert.False(uidOnly.CanReply);
    }

    [Fact]
    public void BuildRouteCarriesQuoteInfoFromTheMessage()
    {
        var route = MessageReplyService.BuildRoute(NativeMessage());

        Assert.Equal(7, route.Pid);
        Assert.Equal("group", route.Scene);
        Assert.Equal("123456", route.PeerId);
        Assert.Equal("u_group", route.PeerUid);
        Assert.Equal(42, route.QuoteSeq);
        Assert.Equal("1001", route.QuoteSenderId);
        Assert.Equal(1700000000, route.QuoteTime);
    }

    [Fact]
    public async Task SendingToAToastMessageFailsFast()
    {
        var service = new MessageReplyService(_ => throw new InvalidOperationException("不该连管道"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendAsync(ToastMessage(), "收到"));
    }

    [Fact]
    public async Task EmptyReplyTextIsRejected()
    {
        var service = new MessageReplyService(_ => throw new InvalidOperationException("不该连管道"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(NativeMessage(), "  "));
    }

    [Fact]
    public async Task SendsThroughTheControlPipeAndReturnsTheResult()
    {
        var frames = Pb.Cat(
            Hqp1.EncodeFrame(PipeOp.Hello),
            Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
            Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1, body: Pb.Cat(Pb.VarintField(1, 0), Pb.VarintField(11, 88))));
        var transport = new FakeTransport([frames]);
        var service = new MessageReplyService(_ => transport);

        var result = await service.SendAsync(NativeMessage(), "收到");

        Assert.Equal(0, result.Result);
        Assert.Equal(88, result.Sequence);
    }
}
