from src.native.hqp1 import RecvPacket
from src.native.model import CapturedMessage
from src.native.proto.message import parse_elems
from src.native.proto.wire import as_bytes, as_int, decode_fields

MESSAGE_PUSH_CMDS: set[str] = {
    "trpc.msg.olpush.OlPushService.MsgPush",
}

# —— PushMsg 外层字段号：占位，Phase 0 真帧校准（Task 6 Step 5） ——
_F_MSG = 1  # PushMsg.message
_F_ROUTING = 2  # message.routingHead（群号/好友号）
_F_CONTENT = 3  # message.contentHead（seq 等）
_F_BODY = 4  # message.messageBody
_F_RICHTEXT = 1  # messageBody.richText
_F_GROUP = 1  # routingHead.group -> groupCode
_F_SENDER_UIN = 1  # 发送者 uin


def decode_message_push(packet: RecvPacket) -> CapturedMessage | None:
    if packet.cmd not in MESSAGE_PUSH_CMDS:
        return None
    top = decode_fields(packet.body)
    msg_f = top.get(_F_MSG)
    if not msg_f:
        return None
    message = decode_fields(as_bytes(msg_f[0]))
    body_f = message.get(_F_BODY)
    if not body_f:
        return None
    body = decode_fields(as_bytes(body_f[0]))
    rich_f = body.get(_F_RICHTEXT)
    segments = parse_elems(as_bytes(rich_f[0])) if rich_f else []

    # peer / sender（占位路径；真帧校准后替换字段号）
    peer_id = ""
    sender_id = packet.uin
    routing_f = message.get(_F_ROUTING)
    if routing_f:
        routing = decode_fields(as_bytes(routing_f[0]))
        grp = routing.get(_F_GROUP)
        if grp:
            peer_id = str(as_int(grp[0]))

    raw_seq = 0
    content_f = message.get(_F_CONTENT)
    if content_f:
        content = decode_fields(as_bytes(content_f[0]))
        seq_f = content.get(_F_SENDER_UIN)
        if seq_f:
            raw_seq = as_int(seq_f[0])

    return CapturedMessage(
        scene="group" if peer_id else "c2c",
        peer_id=peer_id,
        peer_name="",
        sender_id=sender_id,
        sender_name="",
        segments=segments,
        raw_seq=raw_seq or packet.seq,
    )
