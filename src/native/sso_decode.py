from src.native.hqp1 import RecvPacket
from src.native.model import CapturedMessage
from src.native.proto.message import parse_message_body
from src.native.proto.wire import as_bytes, as_int, as_str, decode_fields

MESSAGE_PUSH_CMDS: set[str] = {
    "trpc.msg.olpush.OlPushService.MsgPush",
}

# PushMsg / PushMsgBody（已由真实 NTQQ OlPush 帧校准）。
_F_MESSAGE = 1
_F_RESPONSE_HEAD = 1
_F_CONTENT_HEAD = 2
_F_BODY = 3

# ResponseHead
_F_FROM_UIN = 1
_F_FROM_UID = 2
_F_TO_UIN = 5
_F_TO_UID = 6
_F_FORWARD = 7
_F_GROUP = 8

# ResponseForward / ResponseGrp
_F_FRIEND_NAME = 6
_F_GROUP_UIN = 1
_F_MEMBER_NAME = 2
_F_MEMBER_CARD = 4
_F_GROUP_NAME = 7

# ContentHead
_F_MSG_TYPE = 1
_F_SEQUENCE = 5
_F_TIMESTAMP = 6
_F_NT_MSG_SEQUENCE = 11


def _first(fields, field_no):
    values = fields.get(field_no)
    return values[0] if values else None


def _field_bytes(fields, field_no: int) -> bytes:
    value = _first(fields, field_no)
    return as_bytes(value) if value is not None else b""


def _field_int(fields, field_no: int) -> int:
    value = _first(fields, field_no)
    return as_int(value) if value is not None else 0


def _field_str(fields, field_no: int) -> str:
    value = _first(fields, field_no)
    return as_str(value) if value is not None else ""


def decode_message_push(packet: RecvPacket) -> CapturedMessage | None:
    if packet.cmd not in MESSAGE_PUSH_CMDS:
        return None
    top = decode_fields(packet.body)
    message_data = _field_bytes(top, _F_MESSAGE)
    if not message_data:
        return None
    message = decode_fields(message_data)
    body_data = _field_bytes(message, _F_BODY)
    if not body_data:
        return None

    response_data = _field_bytes(message, _F_RESPONSE_HEAD)
    content_data = _field_bytes(message, _F_CONTENT_HEAD)
    response = decode_fields(response_data) if response_data else {}
    content = decode_fields(content_data) if content_data else {}

    from_uin = _field_int(response, _F_FROM_UIN)
    from_uid = _field_str(response, _F_FROM_UID)
    to_uin = _field_int(response, _F_TO_UIN)
    to_uid = _field_str(response, _F_TO_UID)

    group_data = _field_bytes(response, _F_GROUP)
    group = decode_fields(group_data) if group_data else {}
    group_uin = _field_int(group, _F_GROUP_UIN)
    msg_type = _field_int(content, _F_MSG_TYPE)
    is_group = bool(group_uin) or msg_type == 82
    segments = parse_message_body(body_data, is_group=is_group)

    sender_id = str(from_uin) if from_uin else from_uid
    peer_uid = ""
    sender_name = ""
    sender_nickname = ""
    sender_group_card = ""
    peer_name = ""
    account_uid = ""
    if is_group:
        peer_id = str(group_uin) if group_uin else ""
        peer_name = _field_str(group, _F_GROUP_NAME)
        sender_nickname = _field_str(group, _F_MEMBER_NAME)
        sender_group_card = _field_str(group, _F_MEMBER_CARD)
        sender_name = sender_group_card or sender_nickname
    else:
        try:
            self_uin = int(packet.uin)
        except (TypeError, ValueError):
            self_uin = 0
        peer_uin = to_uin if self_uin and from_uin == self_uin else from_uin
        peer_id = str(peer_uin) if peer_uin else from_uid
        is_outgoing = bool(self_uin) and from_uin == self_uin
        account_uid = from_uid if is_outgoing else to_uid
        peer_uid = to_uid if is_outgoing else from_uid
        forward_data = _field_bytes(response, _F_FORWARD)
        if forward_data:
            sender_name = _field_str(decode_fields(forward_data), _F_FRIEND_NAME)
            sender_nickname = sender_name

    sequence = _field_int(content, _F_SEQUENCE)
    nt_sequence = _field_int(content, _F_NT_MSG_SEQUENCE)
    raw_seq = sequence if is_group else nt_sequence or sequence
    timestamp = _field_int(content, _F_TIMESTAMP)

    return CapturedMessage(
        scene="group" if is_group else "c2c",
        peer_id=peer_id,
        peer_name=peer_name,
        sender_id=sender_id,
        sender_name=sender_name,
        segments=segments,
        raw_seq=raw_seq or packet.seq,
        account_uid=account_uid,
        sender_nickname=sender_nickname,
        sender_group_card=sender_group_card,
        peer_uid=peer_uid,
        timestamp=timestamp,
    )
