import pytest

from src.native.message_sender import (
    build_reply_route,
    build_send_text_request,
    parse_send_message_response,
    send_text_reply_sync,
)
from src.native.model import CapturedMessage
from src.native.proto.wire import (
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)


def _first(fields, field_no):
    return fields[field_no][0]


def _elems(request: bytes) -> list[dict]:
    top = decode_fields(request)
    body = decode_fields(as_bytes(_first(top, 3)))
    rich = decode_fields(as_bytes(_first(body, 1)))
    return [decode_fields(as_bytes(v)) for v in rich[2]]


def test_group_request_routes_by_group_code_and_carries_text():
    request = build_send_text_request(scene="group", peer_uin=123456, text="收到")
    top = decode_fields(request)

    routing = decode_fields(as_bytes(_first(top, 1)))
    assert as_int(_first(decode_fields(as_bytes(_first(routing, 2))), 1)) == 123456
    assert as_int(_first(decode_fields(as_bytes(_first(top, 2))), 1)) == 1

    elems = _elems(request)
    assert len(elems) == 1
    assert as_str(_first(decode_fields(as_bytes(_first(elems[0], 1))), 1)) == "收到"


def test_group_reply_prepends_a_src_msg_quote_element():
    request = build_send_text_request(
        scene="group",
        peer_uin=123456,
        text="收到",
        quote_seq=42,
        quote_sender_uin=1001,
        quote_time=1700000000,
    )

    elems = _elems(request)
    assert [sorted(e) for e in elems] == [[45], [1]]
    src = decode_fields(as_bytes(_first(elems[0], 45)))
    assert as_int(_first(src, 1)) == 42
    assert as_int(_first(src, 2)) == 1001
    assert as_int(_first(src, 3)) == 1700000000


def test_private_request_uses_c2c_routing_and_c2c_cmd_11():
    request = build_send_text_request(
        scene="c2c", peer_uin=10001, peer_uid="u_abc", text="收到", client_sequence=5
    )
    top = decode_fields(request)

    c2c = decode_fields(as_bytes(_first(decode_fields(as_bytes(_first(top, 1))), 1)))
    assert as_int(_first(c2c, 1)) == 10001
    assert as_str(_first(c2c, 2)) == "u_abc"

    content = decode_fields(as_bytes(_first(top, 2)))
    assert as_int(_first(content, 3)) == 11
    assert as_int(_first(top, 4)) == 5


def test_empty_text_and_bad_peer_are_rejected():
    with pytest.raises(ValueError):
        build_send_text_request(scene="group", peer_uin=1, text="")
    with pytest.raises(ValueError):
        build_send_text_request(scene="group", peer_uin=0, text="收到")


def test_response_reports_rejection_and_sequence():
    ok = parse_send_message_response(encode_varint_field(1, 0) + encode_varint_field(11, 88))
    assert (ok.result, ok.error_message, ok.sequence) == (0, "", 88)

    rejected = encode_varint_field(1, 79) + encode_bytes_field(2, "发送失败")
    result = parse_send_message_response(rejected)
    assert result.result == 79 and result.error_message == "发送失败"


def test_reply_route_is_plain_data_for_cross_thread_use():
    msg = CapturedMessage(
        scene="group",
        peer_id="123456",
        peer_name="高三2班",
        sender_id="1001",
        sender_name="张三",
        segments=[],
        raw_seq=42,
        source_pid=999,
    )

    route = build_reply_route(msg)

    assert route["pid"] == 999
    assert route["scene"] == "group"
    assert route["peer_id"] == "123456"
    assert route["quote_seq"] == 42
    assert route["quote_sender_id"] == "1001"
    assert all(isinstance(v, (str, int)) for v in route.values())


def test_send_without_a_qq_process_fails_fast():
    with pytest.raises(RuntimeError, match="尚未连接到 QQ 进程"):
        send_text_reply_sync({"pid": 0, "scene": "group", "peer_id": "1"}, "收到")


def test_private_scene_never_sends_a_quote(monkeypatch):
    captured = {}

    def fake_send(pid, **kwargs):
        captured.update(kwargs)
        return None

    monkeypatch.setattr("src.native.message_sender._send_text_sync", fake_send)
    send_text_reply_sync(
        {"pid": 7, "scene": "c2c", "peer_id": "10001", "quote_seq": 42, "quote_sender_id": "1001"},
        "收到",
    )

    assert captured["quote_seq"] == 0
    assert captured["quote_sender_uin"] == 0
