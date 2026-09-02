"""守护进程控制通道。

QQListener 默认以 daemon 模式在后台跑（托盘 + 悬浮球，不弹窗）。本模块在
QLocalServer 上开一条本机控制通道，让第二个实例——或任何脚本——用一行 JSON
指挥它：查状态、开始/暂停监听、卸载核心、打开设置、退出。

协议：一行 UTF-8 JSON 请求，一行 UTF-8 JSON 响应，然后断开。
    ->  {"command": "status"}
    <-  {"ok": true, "data": {...}}

传输是 Windows 命名管道 / Unix domain socket，只有本机同用户可达；不监听 TCP。
"""

from __future__ import annotations

import json
import os
from collections.abc import Callable

from loguru import logger

from src.ui.qt_compat import QCoreApplication, QObject, QtNetwork

SERVER_NAME = "QQListener.control"
_TIMEOUT_MS = 3000
_MAX_REQUEST_BYTES = 64 * 1024

COMMANDS = (
    "ping",
    "status",
    "start",
    "pause",
    "toggle",
    "unload",
    "show",
    "reload",
    "quit",
)


def server_name() -> str:
    """同一台机器上多用户各用各的通道。"""
    suffix = (
        os.environ.get("USERNAME")
        or os.environ.get("USER")
        or str(os.getuid() if hasattr(os, "getuid") else "")
    )
    return f"{SERVER_NAME}.{suffix}" if suffix else SERVER_NAME


def encode_request(command: str, **params) -> bytes:
    return (json.dumps({"command": command, **params}, ensure_ascii=False) + "\n").encode("utf-8")


def encode_response(payload: dict) -> bytes:
    return (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")


def decode_message(raw: bytes) -> dict:
    text = raw.decode("utf-8", "replace").strip()
    if not text:
        return {}
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError:
        return {}
    return parsed if isinstance(parsed, dict) else {}


class ControlServer(QObject):
    """接收控制指令；每个请求交给 handler 处理，返回可 JSON 化的结果。"""

    def __init__(self, handler: Callable[[str, dict], dict], parent=None):
        super().__init__(parent)
        self._handler = handler
        self._server = QtNetwork.QLocalServer(self)
        self._server.newConnection.connect(self._on_connection)
        self._buffers: dict[object, bytearray] = {}
        self.other_instance = False

    def listen(self) -> bool:
        name = server_name()
        if self._server.listen(name):
            logger.info("控制通道已就绪: {}", name)
            return True

        # 名字被占。先探一探对面是不是活的：活的就说明已有实例，
        # 死的（上次崩溃留下的 socket 文件）才能清掉重来。
        if is_running():
            self.other_instance = True
            return False

        QtNetwork.QLocalServer.removeServer(name)
        if self._server.listen(name):
            logger.info("控制通道已就绪（已清理上次残留）: {}", name)
            return True
        logger.warning("控制通道监听失败: {}", self._server.errorString())
        return False

    def close(self) -> None:
        self._server.close()
        QtNetwork.QLocalServer.removeServer(server_name())

    def _on_connection(self):
        while self._server.hasPendingConnections():
            socket = self._server.nextPendingConnection()
            if socket is None:
                continue
            self._buffers[socket] = bytearray()
            socket.readyRead.connect(lambda s=socket: self._on_ready_read(s))
            socket.disconnected.connect(lambda s=socket: self._on_disconnected(s))

    def _on_disconnected(self, socket):
        self._buffers.pop(socket, None)
        socket.deleteLater()

    def _on_ready_read(self, socket):
        buffer = self._buffers.get(socket)
        if buffer is None:
            return
        buffer.extend(bytes(socket.readAll()))
        if len(buffer) > _MAX_REQUEST_BYTES:
            self._respond(socket, {"ok": False, "error": "请求过大"})
            return
        if b"\n" not in buffer:
            return

        line, _, rest = bytes(buffer).partition(b"\n")
        buffer.clear()
        buffer.extend(rest)
        self._respond(socket, self._dispatch(decode_message(line)))

    def _dispatch(self, request: dict) -> dict:
        command = str(request.get("command") or "").strip().lower()
        if not command:
            return {"ok": False, "error": "缺少 command"}
        if command not in COMMANDS:
            return {"ok": False, "error": f"未知指令: {command}", "commands": list(COMMANDS)}
        logger.info("收到控制指令：{}", command)
        try:
            data = self._handler(command, request)
        except Exception as exc:
            logger.exception("控制指令执行失败: {}", command)
            return {"ok": False, "error": f"{type(exc).__name__}: {exc}"}
        return {"ok": True, "command": command, "data": data or {}}

    @staticmethod
    def _respond(socket, payload: dict):
        socket.write(encode_response(payload))
        socket.flush()
        socket.disconnectFromServer()


def send_command(command: str, timeout_ms: int = _TIMEOUT_MS, **params) -> dict | None:
    """给正在运行的实例发一条指令；没人监听时返回 None。"""
    # 命令行子命令没有 QApplication，QLocalSocket 需要一个事件派发器才能阻塞等待。
    app = QCoreApplication([]) if QCoreApplication.instance() is None else None
    socket = QtNetwork.QLocalSocket()
    socket.connectToServer(server_name())
    if not socket.waitForConnected(timeout_ms):
        return None
    try:
        socket.write(encode_request(command, **params))
        socket.flush()
        buffer = bytearray()
        while b"\n" not in buffer:
            if not socket.waitForReadyRead(timeout_ms):
                return None
            buffer.extend(bytes(socket.readAll()))
        return decode_message(bytes(buffer).split(b"\n", 1)[0])
    finally:
        socket.disconnectFromServer()
        del app  # 仅为在阻塞等待期间保活临时的 QCoreApplication


def is_running() -> bool:
    return send_command("ping", timeout_ms=800) is not None
