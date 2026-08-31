import struct

from src.native.hqp1 import (
    FLAG_LOGGED_IN,
    HEADER_SIZE,
    PIPE_MAGIC,
    PIPE_VERSION,
    Frame,
    FrameReader,
    PipeOp,
    RecvPacket,
    encode_frame,
    packet_from_frame,
)


def test_encode_frame_header_layout():
    data = encode_frame(
        PipeOp.recv_packet, status=3, flags=5, value0=42, cmd="ab", msg="c", body=b"xy"
    )
    assert len(data) == HEADER_SIZE + 2 + 1 + 2
    assert struct.unpack_from("<I", data, 0)[0] == PIPE_MAGIC
    assert struct.unpack_from("<H", data, 4)[0] == PIPE_VERSION
    assert struct.unpack_from("<H", data, 6)[0] == int(PipeOp.recv_packet)
    assert struct.unpack_from("<i", data, 12)[0] == 3
    assert struct.unpack_from("<I", data, 16)[0] == 5
    assert struct.unpack_from("<I", data, 20)[0] == 2  # cmdLen
    assert struct.unpack_from("<I", data, 24)[0] == 1  # msgLen
    assert struct.unpack_from("<I", data, 28)[0] == 2  # bodyLen
    assert struct.unpack_from("<Q", data, 32)[0] == 42
    assert data[HEADER_SIZE:] == b"abcxy"


def test_frame_reader_roundtrip_and_split():
    frame_bytes = encode_frame(PipeOp.recv_packet, value0=7, cmd="cmd", msg="123", body=b"\x01\x02")
    reader = FrameReader()
    # 分两半喂入，验证跨块缓冲
    assert reader.push(frame_bytes[:10]) == []
    frames = reader.push(frame_bytes[10:])
    assert len(frames) == 1
    f = frames[0]
    assert f.op == int(PipeOp.recv_packet)
    assert f.value0 == 7 and f.cmd == "cmd" and f.msg == "123" and f.body == b"\x01\x02"


def test_frame_reader_bad_magic_raises():
    reader = FrameReader()
    bad = bytes(HEADER_SIZE)  # 全 0，magic 错
    try:
        reader.push(bad)
    except ValueError as exc:
        assert "magic" in str(exc)
    else:
        raise AssertionError("expected ValueError")


def test_packet_from_frame_maps_op6():
    f = Frame(
        op=int(PipeOp.recv_packet),
        request_id=0,
        status=-1,
        flags=0,
        value0=99,
        cmd="svc",
        msg="10001",
        body=b"pb",
    )
    pkt = packet_from_frame(f)
    assert pkt == RecvPacket(seq=99, error=-1, cmd="svc", uin="10001", body=b"pb")


def test_packet_from_frame_ignores_non_recv():
    f = Frame(
        op=int(PipeOp.login_state),
        request_id=0,
        status=0,
        flags=FLAG_LOGGED_IN,
        value0=1,
        cmd="",
        msg="",
        body=b"",
    )
    assert packet_from_frame(f) is None
