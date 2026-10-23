using QQListener.Models;
using QQListener.Oidb;
using QQListener.Pipes;
using QQListener.Protocol;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public sealed class MessageReactionTests
{
    private static MessageEvent Message(string scene = "group") => new(
        "qq.native", "QQ", "发送者", "群", "图片", DateTimeOffset.UtcNow,
        MessageImportance.Normal, [], "test")
    {
        SourcePid = 7, Scene = scene, ConversationId = "123456", RawSeq = 42
    };

    [Fact]
    public void OkReactionMatchesOfficialGroupReactionWireInsteadOfSendingAnEmojiMessage()
    {
        var envelope = Wire.DecodeFields(MessageReaction.BuildRequest(123456, 42));
        Assert.Equal(0x9082UL, Wire.FieldInt(envelope, 1));
        Assert.Equal(1UL, Wire.FieldInt(envelope, 2));
        Assert.Equal(0UL, Wire.FieldInt(envelope, 12));
        var body = Wire.DecodeFields(Wire.FieldBytes(envelope, 4));
        Assert.Equal(123456UL, Wire.FieldInt(body, 2));
        Assert.Equal(42UL, Wire.FieldInt(body, 3));
        Assert.Equal("124", Wire.FieldString(body, 4));
        Assert.Equal(1UL, Wire.FieldInt(body, 5));
        Assert.Equal(0UL, Wire.FieldInt(body, 6));
        Assert.Equal(0UL, Wire.FieldInt(body, 7));
    }

    [Fact]
    public void OnlyNativeGroupMessagesWithValidSequenceCanReact()
    {
        Assert.True(Message().CanReact);
        Assert.False(Message("c2c").CanReact);
        Assert.False((Message() with { SourcePid = 0 }).CanReact);
        Assert.False((Message() with { RawSeq = 0 }).CanReact);
        Assert.False((Message() with { RawSeq = (long)uint.MaxValue + 1 }).CanReact);
        Assert.False((Message() with { ConversationId = "u_peer" }).CanReact);
    }

    [Theory]
    [InlineData(0, 42)]
    [InlineData(1, 0)]
    [InlineData(1, 4294967296)]
    public void InvalidRoutesAreRejectedBeforeSending(long peer, long sequence) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageReaction.BuildRequest(peer, sequence));

    [Fact]
    public void EmptySuccessPayloadAndOmittedZeroErrorCodeAreAllowedButMalformedResponsesAreNot()
    {
        MessageReaction.ValidateResponse(Pb.VarintField(3, 0));
        MessageReaction.ValidateResponse(Pb.Cat(Pb.VarintField(1, 0x9082), Pb.VarintField(2, 1)));
        Assert.Throws<InvalidDataException>(() => MessageReaction.ValidateResponse([]));
        Assert.Throws<InvalidDataException>(() => MessageReaction.ValidateResponse(Wire.EncodeBytesField(3, [])));
        Assert.Throws<InvalidDataException>(() => MessageReaction.ValidateResponse(Pb.VarintField(1, 1)));
        Assert.Throws<IOException>(() => MessageReaction.ValidateResponse(Pb.VarintField(3, 120)));
    }

    [Fact]
    public async Task ServiceUsesTheOriginalGroupAndSequenceAndChecksServerRejection()
    {
        var transport = Transport(Pb.VarintField(3, 0));
        var service = new MessageReplyService(pid => { Assert.Equal(7, pid); return transport; });
        await service.SendOkReactionAsync(Message());
        var request = Assert.Single(new FrameReader().Push(Assert.Single(transport.Writes)));
        Assert.Equal(MessageReaction.Command, request.Cmd);
        var body = Wire.DecodeFields(Wire.FieldBytes(Wire.DecodeFields(request.Body), 4));
        Assert.Equal(42UL, Wire.FieldInt(body, 3));
        var rejected = new MessageReplyService(_ => Transport(Pb.VarintField(3, 120)));
        await Assert.ThrowsAsync<IOException>(() => rejected.SendOkReactionAsync(Message()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendOkReactionAsync(Message("c2c")));
    }

    private static FakeTransport Transport(byte[] body) => new([Pb.Cat(
        Hqp1.EncodeFrame(PipeOp.Hello),
        Hqp1.EncodeFrame(PipeOp.SendAck, requestId: 1),
        Hqp1.EncodeFrame(PipeOp.SendReply, requestId: 1, body: body))]);
}
