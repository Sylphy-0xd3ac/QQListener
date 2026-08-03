import asyncio
from collections.abc import Callable
from dataclasses import dataclass

from src.native.hqp1 import (
    FLAG_LOGGED_IN,
    FrameReader,
    PipeOp,
    RecvPacket,
    packet_from_frame,
)
from src.native.pipe_transport import PipeTransport


@dataclass
class LoginState:
    logged_in: bool
    uin: str


class RecvHookClient:
    def __init__(self, transport: PipeTransport) -> None:
        self._transport = transport
        self._stopped = False

    def stop(self) -> None:
        self._stopped = True
        self._transport.close()

    async def run(
        self,
        on_packet: Callable[[RecvPacket], None],
        on_login: Callable[[LoginState], None] | None = None,
    ) -> None:
        loop = asyncio.get_running_loop()
        reader = FrameReader()
        while not self._stopped:
            chunk = await loop.run_in_executor(None, self._transport.read, 65536)
            if not chunk:
                return
            for frame in reader.push(chunk):
                if frame.op == int(PipeOp.login_state):
                    if on_login is not None:
                        logged_in = bool(frame.flags & FLAG_LOGGED_IN) or frame.status != 0
                        on_login(
                            LoginState(logged_in=logged_in, uin=frame.msg or str(frame.value0))
                        )
                    continue
                pkt = packet_from_frame(frame)
                if pkt is not None:
                    on_packet(pkt)
