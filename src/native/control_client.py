from dataclasses import dataclass

from src.native.hqp1 import FLAG_WANT_REPLY, Frame, FrameReader, PipeOp, encode_frame
from src.native.pipe_transport import PipeTransport


@dataclass(frozen=True)
class SendReply:
    request_id: int
    error: int
    message: str
    body: bytes


class ControlHookClient:
    """HQP1 control 管道的单请求客户端。

    文件地址解析按消息临时连接，避免与持续运行的 recv 管道互相影响。
    """

    def __init__(self, transport: PipeTransport) -> None:
        self._transport = transport
        self._reader = FrameReader()
        self._pending_frames: list[Frame] = []
        self._closed = False

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._transport.close()

    def _read_frames(self) -> list[Frame]:
        if self._pending_frames:
            frames, self._pending_frames = self._pending_frames, []
            return frames
        while True:
            chunk = self._transport.read(65536)
            if not chunk:
                raise ConnectionError("control pipe closed")
            frames = self._reader.push(chunk)
            if frames:
                return frames

    def _wait_for_hello(self) -> None:
        while True:
            frames = self._read_frames()
            for index, frame in enumerate(frames):
                if frame.op == int(PipeOp.hello):
                    self._pending_frames.extend(frames[index + 1 :])
                    return

    def send(self, cmd: str, body: bytes, request_id: int = 1) -> SendReply:
        self._wait_for_hello()
        self._transport.write(
            encode_frame(
                PipeOp.send_request,
                request_id=request_id,
                flags=FLAG_WANT_REPLY,
                cmd=cmd,
                body=body,
            )
        )

        ack_received = False
        while True:
            for frame in self._read_frames():
                if frame.request_id != request_id:
                    continue
                if frame.op == int(PipeOp.error):
                    raise RuntimeError(frame.msg or f"control request failed: {frame.status}")
                if frame.op == int(PipeOp.send_ack):
                    ack_received = True
                    continue
                if frame.op == int(PipeOp.send_reply):
                    if not ack_received:
                        raise RuntimeError("control reply arrived before ack")
                    return SendReply(
                        request_id=request_id,
                        error=frame.status,
                        message=frame.msg,
                        body=frame.body,
                    )
