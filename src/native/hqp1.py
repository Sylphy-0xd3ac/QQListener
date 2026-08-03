import struct
from dataclasses import dataclass
from enum import IntEnum

PIPE_MAGIC = 0x31504851
PIPE_VERSION = 1
HEADER_SIZE = 40

FLAG_WANT_REPLY = 1 << 0
FLAG_LOGGED_IN = 1 << 2


class PipeOp(IntEnum):
    hello = 1
    send_request = 2
    send_ack = 3
    send_reply = 4
    error = 5
    recv_packet = 6
    login_state = 7


@dataclass
class Frame:
    op: int
    request_id: int
    status: int
    flags: int
    value0: int
    cmd: str
    msg: str
    body: bytes


@dataclass
class RecvPacket:
    seq: int
    error: int
    cmd: str
    uin: str
    body: bytes


def encode_frame(
    op: int,
    *,
    request_id: int = 0,
    status: int = 0,
    flags: int = 0,
    value0: int = 0,
    cmd: str = "",
    msg: str = "",
    body: bytes = b"",
) -> bytes:
    cmd_b = cmd.encode("utf-8")
    msg_b = msg.encode("utf-8")
    header = struct.pack(
        "<IHHIiIIIIQ",
        PIPE_MAGIC,
        PIPE_VERSION,
        int(op),
        request_id & 0xFFFFFFFF,
        status,
        flags & 0xFFFFFFFF,
        len(cmd_b),
        len(msg_b),
        len(body),
        value0 & 0xFFFFFFFFFFFFFFFF,
    )
    return header + cmd_b + msg_b + bytes(body)


class FrameReader:
    def __init__(self) -> None:
        self._buf = bytearray()

    def push(self, chunk: bytes) -> list[Frame]:
        self._buf.extend(chunk)
        frames: list[Frame] = []
        while len(self._buf) >= HEADER_SIZE:
            magic, version = struct.unpack_from("<IH", self._buf, 0)
            if magic != PIPE_MAGIC or version != PIPE_VERSION:
                raise ValueError(f"bad frame header magic=0x{magic:x} version={version}")
            op, request_id, status, flags = struct.unpack_from("<HIiI", self._buf, 6)
            cmd_len, msg_len, body_len = struct.unpack_from("<III", self._buf, 20)
            (value0,) = struct.unpack_from("<Q", self._buf, 32)
            total = HEADER_SIZE + cmd_len + msg_len + body_len
            if len(self._buf) < total:
                break
            off = HEADER_SIZE
            cmd = self._buf[off : off + cmd_len].decode("utf-8", "replace")
            off += cmd_len
            msg = self._buf[off : off + msg_len].decode("utf-8", "replace")
            off += msg_len
            body = bytes(self._buf[off : off + body_len])
            del self._buf[:total]
            frames.append(
                Frame(
                    op=op,
                    request_id=request_id,
                    status=status,
                    flags=flags,
                    value0=value0,
                    cmd=cmd,
                    msg=msg,
                    body=body,
                )
            )
        return frames


def packet_from_frame(frame: Frame) -> RecvPacket | None:
    if frame.op != int(PipeOp.recv_packet):
        return None
    return RecvPacket(
        seq=frame.value0, error=frame.status, cmd=frame.cmd, uin=frame.msg, body=frame.body
    )
