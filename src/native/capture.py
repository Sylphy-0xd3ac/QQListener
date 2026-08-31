from collections.abc import Callable

from src.native.hook_client import RecvHookClient
from src.native.model import CapturedMessage
from src.native.pipe_transport import Win32NamedPipeTransport
from src.native.sso_decode import decode_message_push


def _is_main_qq_process(name: str, cmdline: list[str] | None) -> bool:
    """Electron 子进程都带 ``--type``；只允许命令行可读的 QQ 主进程。"""
    if name.lower() != "qq.exe" or not cmdline:
        return False
    return not any(arg == "--type" or arg.startswith("--type=") for arg in cmdline[1:])


def enumerate_qq_pids() -> list[int]:
    import psutil

    pids: list[int] = []
    for proc in psutil.process_iter(["name", "cmdline"], ad_value=None):
        name = proc.info.get("name") or ""
        cmdline = proc.info.get("cmdline")
        if _is_main_qq_process(name, cmdline):
            pids.append(proc.pid)
    return sorted(pids)


def recv_pipe_name(pid: int) -> str:
    return rf"\\.\pipe\mojo.{pid}.recv"


class RecvCapture:
    def __init__(
        self,
        pid: int,
        on_message: Callable[[CapturedMessage], None],
        transport_factory: Callable[[str], object] = Win32NamedPipeTransport,
    ) -> None:
        self._pid = pid
        self._on_message = on_message
        self._transport_factory = transport_factory
        self._client: RecvHookClient | None = None

    def stop(self) -> None:
        if self._client is not None:
            self._client.stop()

    async def run(self) -> None:
        transport = self._transport_factory(recv_pipe_name(self._pid))
        self._client = RecvHookClient(transport)

        def handle(packet):
            msg = decode_message_push(packet)
            if msg is not None:
                self._on_message(msg)

        await self._client.run(on_packet=handle)
