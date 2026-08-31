from src.native.hqp1 import RecvPacket
from src.native.model import message_text
from src.native.sso_decode import MESSAGE_PUSH_CMDS, decode_message_push


def _uvarint(value: int) -> bytes:
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        out.append(byte | (0x80 if value else 0))
        if not value:
            return bytes(out)


def _len_field(field_no: int, payload: bytes) -> bytes:
    return _uvarint((field_no << 3) | 2) + _uvarint(len(payload)) + payload


def _varint_field(field_no: int, value: int) -> bytes:
    return _uvarint(field_no << 3) + _uvarint(value)


def _packet(
    body: bytes,
    *,
    msg_type: int = 166,
    sender: int = 10001,
    recipient: int = 20002,
    response_extra: bytes = b"",
) -> RecvPacket:
    response = (
        _varint_field(1, sender)
        + _len_field(2, b"uid-sender")
        + _varint_field(3, 1001)
        + _varint_field(5, recipient)
        + _len_field(6, b"uid-recipient")
        + response_extra
    )
    content = (
        _varint_field(1, msg_type)
        + _varint_field(4, 987654)
        + _varint_field(5, 41)
        + _varint_field(6, 1_700_000_000)
        + _varint_field(11, 42)
    )
    message = _len_field(1, response) + _len_field(2, content) + _len_field(3, body)
    return RecvPacket(
        seq=900,
        error=0,
        cmd="trpc.msg.olpush.OlPushService.MsgPush",
        uin=str(recipient),
        body=_len_field(1, message),
    )


def test_non_message_cmd_returns_none():
    pkt = RecvPacket(seq=1, error=0, cmd="some.other.Cmd", uin="10001", body=b"")
    assert decode_message_push(pkt) is None


def test_message_push_cmd_is_recognized():
    assert "trpc.msg.olpush.OlPushService.MsgPush" in MESSAGE_PUSH_CMDS


def test_decode_private_text_from_real_ntqq_shape():
    text = _len_field(1, "私聊测试".encode())
    rich_text = _len_field(2, _len_field(1, text))
    msg = decode_message_push(_packet(_len_field(1, rich_text)))

    assert msg is not None
    assert msg.scene == "c2c"
    assert msg.peer_id == "10001"
    assert msg.sender_id == "10001"
    assert msg.account_uid == "uid-recipient"
    assert msg.raw_seq == 42
    assert message_text(msg) == "私聊测试"


def test_decode_group_text_uses_group_and_member_fields():
    group = (
        _varint_field(1, 30003)
        + _len_field(2, "基础昵称".encode())
        + _len_field(4, "群名片".encode())
        + _len_field(7, "测试群".encode())
    )
    text = _len_field(1, "群聊测试".encode())
    rich_text = _len_field(2, _len_field(1, text))
    msg = decode_message_push(
        _packet(
            _len_field(1, rich_text),
            msg_type=82,
            response_extra=_len_field(8, group),
        )
    )

    assert msg is not None
    assert msg.scene == "group"
    assert msg.peer_id == "30003"
    assert msg.peer_name == "测试群"
    assert msg.sender_id == "10001"
    assert msg.sender_name == "群名片"
    assert msg.sender_nickname == "基础昵称"
    assert msg.sender_group_card == "群名片"
    assert msg.account_uid == ""
    assert msg.raw_seq == 41
    assert message_text(msg) == "群聊测试"


def test_decode_private_forward_name_as_nickname():
    text = _len_field(1, "私聊测试".encode())
    rich_text = _len_field(2, _len_field(1, text))
    forward = _len_field(6, "好友昵称".encode())

    msg = decode_message_push(
        _packet(_len_field(1, rich_text), response_extra=_len_field(7, forward))
    )

    assert msg is not None
    assert msg.sender_name == "好友昵称"
    assert msg.sender_nickname == "好友昵称"


def test_decode_nt_common_image_from_private_push():
    file_type = _varint_field(2, 1000)
    file_info = (
        _varint_field(1, 4096)
        + _len_field(2, b"0123456789abcdef0123456789abcdef")
        + _len_field(3, b"0123456789abcdef0123456789abcdef01234567")
        + _len_field(4, b"image.png")
        + _len_field(5, file_type)
        + _varint_field(6, 640)
        + _varint_field(7, 480)
    )
    index = _len_field(1, file_info) + _len_field(2, b"fake-file-uuid")
    picture_ext = _len_field(1, b"&spec=0")
    picture = (
        _len_field(1, b"/download?appid=1406&fileid=fake")
        + _len_field(2, picture_ext)
        + _len_field(3, b"multimedia.nt.qq.com.cn")
    )
    msg_info_body = _len_field(1, index) + _len_field(2, picture)
    msg_info = _len_field(1, msg_info_body)
    common = _varint_field(1, 48) + _len_field(2, msg_info) + _varint_field(3, 10)
    rich_text = _len_field(2, _len_field(53, common))

    msg = decode_message_push(_packet(_len_field(1, rich_text)))

    assert msg is not None
    assert [segment.type for segment in msg.segments] == ["image"]
    image = msg.segments[0]
    assert image.url == ("https://multimedia.nt.qq.com.cn/download?appid=1406&fileid=fake&spec=0")
    assert image.name == "image.png"
    assert image.md5 == "0123456789abcdef0123456789abcdef"
    assert image.extra["size"] == 4096
    assert image.extra["width"] == 640
    assert image.extra["height"] == 480


def test_decode_private_file_from_message_content():
    file_info = (
        _len_field(3, b"fake-file-uuid")
        + _len_field(5, "资料.zip".encode())
        + _varint_field(6, 126251)
        + _len_field(57, b"fake-file-hash")
    )
    file_extra = _len_field(1, file_info)
    msg = decode_message_push(_packet(_len_field(2, file_extra), msg_type=529))

    assert msg is not None
    assert [segment.type for segment in msg.segments] == ["file"]
    file_segment = msg.segments[0]
    assert file_segment.name == "资料.zip"
    assert file_segment.extra == {
        "size": 126251,
        "file_id": "fake-file-uuid",
        "file_hash": "fake-file-hash",
    }
