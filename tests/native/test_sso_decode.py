from src.native.hqp1 import RecvPacket
from src.native.sso_decode import MESSAGE_PUSH_CMDS, decode_message_push


def test_non_message_cmd_returns_none():
    pkt = RecvPacket(seq=1, error=0, cmd="some.other.Cmd", uin="10001", body=b"")
    assert decode_message_push(pkt) is None


def test_message_push_cmd_is_recognized():
    assert "trpc.msg.olpush.OlPushService.MsgPush" in MESSAGE_PUSH_CMDS
