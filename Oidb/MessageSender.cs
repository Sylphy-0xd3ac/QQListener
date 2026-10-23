using QQListener.Protocol;

namespace QQListener.Oidb;

public sealed record SendResult(int Result, string ErrorMessage, long Sequence);

/// <summary>回哪里去：把一条捕获消息压成纯数据，供 UI 线程稍后发送。</summary>
public sealed record ReplyRoute(
    int Pid,
    string Scene,
    string PeerId,
    string PeerUid = "",
    string PeerName = "",
    long QuoteSeq = 0,
    string QuoteSenderId = "",
    long QuoteTime = 0);

/// <summary>
/// 往会话里回一条文本消息（MessageSvc.PbSendMsg）。
///
/// 线格式对齐 SnowLuma packages/proto-defs/src/action.ts 的 SendMessageRequest 与
/// packages/core/src/bridge/apis/message.ts 的 sendGroup / sendPrivate。
/// 群聊回复会带上 SrcMsg(45) 元素，出现为「引用回复」。
/// </summary>
public static class MessageSender
{
    public const string SendMsgCmd = "MessageSvc.PbSendMsg";

    private const int ElemText = 1;
    private const int ElemSrcMsg = 45;
    private const int RichTextElems = 2;

    private static byte[] TextElem(string text) =>
        Wire.EncodeBytesField(ElemText, Wire.EncodeBytesField(1, text));

    /// <summary>SrcMsg：origSeqs=1, senderUin=2, time=3（对齐 element-builder.makeReplyElem）。</summary>
    private static byte[] SrcMsgElem(long seq, long senderUin, long timestamp)
    {
        var src = Wire.Concat(
            Wire.EncodeVarintField(1, (ulong)seq),
            Wire.EncodeVarintField(2, (ulong)senderUin));
        if (timestamp != 0)
            src = Wire.Concat(src, Wire.EncodeVarintField(3, (ulong)timestamp));
        return Wire.EncodeBytesField(ElemSrcMsg, src);
    }

    public static byte[] BuildSendTextRequest(
        string scene,
        long peerUin,
        string text,
        string peerUid = "",
        long clientSequence = 0,
        long messageRandom = 0,
        long quoteSeq = 0,
        long quoteSenderUin = 0,
        long quoteTime = 0,
        long now = 0)
    {
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("message is empty", nameof(text));
        if (peerUin <= 0)
            throw new ArgumentException("peer uin is invalid", nameof(peerUin));

        var elems = Array.Empty<byte>();
        // 用 > 0 而不是 != 0：QuoteSenderId 走 long.TryParse 解析，一个 "-1" 之类
        // 的坏值会通过 != 0 这道门槛，编码出一个天文数字大小的 varint。跟上面
        // peerUin 的校验保持同一个标准。
        if (quoteSeq > 0 && quoteSenderUin > 0)
            elems = Wire.EncodeBytesField(RichTextElems,
                SrcMsgElem(quoteSeq, quoteSenderUin, quoteTime));
        elems = Wire.Concat(elems, Wire.EncodeBytesField(RichTextElems, TextElem(text)));
        var messageBody = Wire.EncodeBytesField(3, Wire.EncodeBytesField(1, elems));

        byte[] routing;
        byte[] contentHead;
        var isGroup = scene == "group";
        if (isGroup)
        {
            routing = Wire.EncodeBytesField(2, Wire.EncodeVarintField(1, (ulong)peerUin));
            contentHead = Wire.EncodeVarintField(1, 1);
            clientSequence = 0;
        }
        else
        {
            var c2c = Wire.EncodeVarintField(1, (ulong)peerUin);
            if (peerUid.Length > 0)
                c2c = Wire.Concat(c2c, Wire.EncodeBytesField(2, peerUid));
            routing = Wire.EncodeBytesField(1, c2c);
            contentHead = Wire.Concat(
                Wire.EncodeVarintField(1, 1),
                Wire.EncodeVarintField(2, 0),
                Wire.EncodeVarintField(3, 11));
        }

        var request = Wire.Concat(
            Wire.EncodeBytesField(1, routing),
            Wire.EncodeBytesField(2, contentHead),
            messageBody,
            Wire.EncodeVarintField(4, (ulong)clientSequence),
            Wire.EncodeVarintField(5, (ulong)messageRandom),
            Wire.EncodeBytesField(6, []),
            Wire.EncodeVarintField(8, 0),
            Wire.EncodeVarintField(9, 0));

        if (!isGroup)
        {
            var stamp = now != 0 ? now : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            request = Wire.Concat(request,
                Wire.EncodeBytesField(12, Wire.EncodeVarintField(1, (ulong)stamp)));
        }

        return Wire.Concat(request, Wire.EncodeVarintField(14, 0));
    }

    public static SendResult ParseResponse(ReadOnlySpan<byte> data)
    {
        var fields = Wire.DecodeFields(data);

        long sequence = 0;
        if (Wire.First(fields, 11) is { } groupSeq)
            sequence = (long)Wire.AsInt(groupSeq);
        else if (Wire.First(fields, 14) is { } privateSeq)
            sequence = (long)Wire.AsInt(privateSeq);

        return new SendResult(
            (int)Wire.FieldInt(fields, 1),
            Wire.FieldString(fields, 2),
            sequence);
    }
}

/// <summary>客户端序列号 / 消息 random 的来源，形状照抄 SnowLuma Bridge。</summary>
public sealed class SequenceSource
{
    private const uint Golden = 0x9E3779B9;

    private readonly object _gate = new();
    private uint _clientSeq;
    private uint _random;

    public SequenceSource()
    {
        // 种子只取毫秒时钟的低位时，周期约 11.57 天——在回绕点附近重启，新种子会
        // 比同一个登录会话里已经用过的值更小，导致 clientSequence 在这个会话内
        // 重复。这正是这个字段本身用来防止的问题（QQ 拿它去重，重复值被服务端
        // 静默丢弃，本地却显示"已发送"）。混入随机量，让相邻两次启动的种子
        // 不再有任何可预测的大小关系。
        _clientSeq = (uint)(100000000 + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000000000)
                     ^ (uint)Random.Shared.NextInt64(uint.MaxValue + 1L);
        _random = (uint)Random.Shared.NextInt64(uint.MaxValue + 1L);
    }

    public long NextClientSequence()
    {
        lock (_gate) return _clientSeq += 1;
    }

    public long NextRandom()
    {
        lock (_gate)
        {
            _random += Golden;
            return _random & 0x7FFFFFFF;
        }
    }
}
