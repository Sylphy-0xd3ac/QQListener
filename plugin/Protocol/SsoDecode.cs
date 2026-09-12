using System.Collections.Frozen;

namespace QQListener.Protocol;

/// <summary>
/// SSO 包 → <see cref="CapturedMessage"/>。
///
/// 字段号已由真实 NTQQ OlPush 帧校准，结构对照 SnowLuma
/// <c>packages/proto-defs/src/message.ts</c> 的 PushMsg / PushMsgBody。
/// </summary>
public static class SsoDecode
{
    // 每个 pid 一条捕获线程并发 Contains 这个集合（NativeQqMessageSource）；
    // 可写的 public HashSet 只在"确保没人写入"时才允许并发读，之前的类型完全
    // 没有这个保证——FrozenSet 从类型上直接排除了运行期被外部 .Add() 的可能。
    public static readonly FrozenSet<string> MessagePushCmds =
        new[] { "trpc.msg.olpush.OlPushService.MsgPush" }.ToFrozenSet();

    // PushMsg / PushMsgBody
    private const int FMessage = 1;
    private const int FResponseHead = 1;
    private const int FContentHead = 2;
    private const int FBody = 3;

    // ResponseHead
    private const int FFromUin = 1;
    private const int FFromUid = 2;
    private const int FToUin = 5;
    private const int FToUid = 6;
    private const int FForward = 7;
    private const int FGroup = 8;

    // ResponseForward / ResponseGrp
    private const int FFriendName = 6;
    private const int FGroupUin = 1;
    private const int FMemberName = 2;
    private const int FMemberCard = 4;
    private const int FGroupName = 7;

    // ContentHead
    private const int FMsgType = 1;
    private const int FSequence = 5;
    private const int FTimestamp = 6;
    private const int FNtMsgSequence = 11;

    public static CapturedMessage? DecodeMessagePush(RecvPacket packet)
    {
        if (!MessagePushCmds.Contains(packet.Cmd)) return null;

        var top = Wire.DecodeFields(packet.Body);
        var messageData = Wire.FieldBytes(top, FMessage);
        if (messageData.Length == 0) return null;

        var message = Wire.DecodeFields(messageData);
        var bodyData = Wire.FieldBytes(message, FBody);
        if (bodyData.Length == 0) return null;

        var responseData = Wire.FieldBytes(message, FResponseHead);
        var contentData = Wire.FieldBytes(message, FContentHead);
        var response = responseData.Length > 0 ? Wire.DecodeFields(responseData) : [];
        var content = contentData.Length > 0 ? Wire.DecodeFields(contentData) : [];

        var fromUin = Wire.FieldInt(response, FFromUin);
        var fromUid = Wire.FieldString(response, FFromUid);
        var toUin = Wire.FieldInt(response, FToUin);
        var toUid = Wire.FieldString(response, FToUid);

        var groupData = Wire.FieldBytes(response, FGroup);
        var group = groupData.Length > 0 ? Wire.DecodeFields(groupData) : [];
        var groupUin = Wire.FieldInt(group, FGroupUin);
        var msgType = Wire.FieldInt(content, FMsgType);
        var isGroup = groupUin != 0 || msgType == 82;

        var segments = RichText.ParseMessageBody(bodyData, isGroup);

        // 自己的数字账号——群聊分支之前没算这个，"有人 @ 我" 的判定得靠它跟
        // Segment("at").TargetId 比对，不是只有私聊方向判断需要。
        _ = ulong.TryParse(packet.Uin, out var selfUin);

        var senderId = fromUin != 0 ? fromUin.ToString() : fromUid;
        var senderName = "";
        var senderNickname = "";
        var senderGroupCard = "";
        var peerName = "";
        var accountUid = "";
        var peerUid = "";
        string peerId;

        if (isGroup)
        {
            peerId = groupUin != 0 ? groupUin.ToString() : "";
            peerName = Wire.FieldString(group, FGroupName);
            senderNickname = Wire.FieldString(group, FMemberName);
            senderGroupCard = Wire.FieldString(group, FMemberCard);
            senderName = senderGroupCard.Length > 0 ? senderGroupCard : senderNickname;
        }
        else
        {
            var isOutgoing = selfUin != 0 && fromUin == selfUin;

            var peerUin = isOutgoing ? toUin : fromUin;
            peerId = peerUin != 0 ? peerUin.ToString() : fromUid;
            accountUid = isOutgoing ? fromUid : toUid;
            peerUid = isOutgoing ? toUid : fromUid;

            var forwardData = Wire.FieldBytes(response, FForward);
            if (forwardData.Length > 0)
            {
                senderName = Wire.FieldString(Wire.DecodeFields(forwardData), FFriendName);
                senderNickname = senderName;
            }
        }

        var sequence = Wire.FieldInt(content, FSequence);
        var ntSequence = Wire.FieldInt(content, FNtMsgSequence);
        // 这里只能是 ContentHead 里的协议序列号，绝不能拿 packet.Seq（HQP1 帧的传输层
        // 计数器，跟 QQ 的消息序列号是完全不同的命名空间）来顶替——RawSeq 会被直接当成
        // group 回复的 SrcMsg 引用序列号发回给 QQ（见 ReplyRoute.QuoteSeq），塞一个瞎凑的
        // 值等于让"回复"悄悄引用到一条不相干的消息。协议确实没给序列号时（少见）就老实
        // 留 0；NativeMessageEventFactory 的去重键另外拿 Timestamp 兜底，不靠这里凑数。
        var rawSeq = isGroup ? sequence : (ntSequence != 0 ? ntSequence : sequence);
        var timestamp = Wire.ClampToInt32(Wire.FieldInt(content, FTimestamp));

        return new CapturedMessage
        {
            Scene = isGroup ? "group" : "c2c",
            PeerId = peerId,
            PeerName = peerName,
            PeerUid = peerUid,
            SenderId = senderId,
            SenderName = senderName,
            SenderNickname = senderNickname,
            SenderGroupCard = senderGroupCard,
            AccountUid = accountUid,
            Segments = segments,
            RawSeq = rawSeq,
            Timestamp = timestamp,
            SelfUin = selfUin,
        };
    }
}
