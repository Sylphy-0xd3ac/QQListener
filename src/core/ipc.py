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
    """同一台机器上多用户各用各的通道。

    用户名会原样拼进 Windows 命名管道 / Unix socket 路径，中文名、空格、点号都
    可能出现，所以只保留 ASCII 字母数字，其余折成下划线；全被折掉就退回不带后缀。
    """
    raw = os.environ.get("USERNAME") or os.environ.get("USER") or ""
    if not raw and hasattr(os, "getuid"):
        raw = str(os.getuid())
    suffix = "".join(ch if ch.isascii() and ch.isalnum() else "_" for ch in raw).strip("_")
    return f"{SERVER_NAME}.{suffix[:32]}" if suffix else SERVER_NAME


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

        # 先探活，再监听。不能用 "listen() 失败" 判断是否已有实例——Qt 文档明确写着：
        # Windows 上两个 QLocalServer 可以同时监听同一个管道名，连接会随机落到其中
        # 一个。也就是说 Windows 上 listen() 永远成功，单实例判断会失效，最后跑起
        # 两份守护进程，指令随机打到其中一份，表现就是"指令时灵时不灵"。
        if is_running():
            self.other_instance = True
            logger.info("已有 QQListener 实例占用控制通道: {}", name)
            return False

        if self._server.listen(name):
            logger.info("控制通道已就绪: {} (pid={})", name, os.getpid())
            return True

        # Unix 上崩溃残留的 socket 文件会让 listen() 报 AddressInUseError；
        # 上面已经确认没有活的实例，可以安全清掉重来。
        QtNetwork.QLocalServer.removeServer(name)
        if self._server.listen(name):
            logger.info("控制通道已就绪（已清理上次残留）: {} (pid={})", name, os.getpid())
            return True

        logger.warning(
            "控制通道监听失败（命令行子命令将不可用）: name={} error={}",
            name,
            self._server.errorString(),
        )
        return False

    def close(self) -> None:
        self._server.close()
        QtNetwork.QLocalServer.removeServer(server_name())

    def _guarded(self, what: str, fn, *args) -> None:
        """Qt 槽里的异常在 PySide6 里会直接终止进程，一条也不能漏出去。

        管道对端随时可能消失（客户端超时退出、进程被杀），write/flush 在
        Windows 上都会抛——那不该带走整个守护进程。
        """
        try:
            fn(*args)
        except Exception:
            logger.exception("控制通道 {} 失败", what)

    def _on_connection(self):
        self._guarded("接受连接", self._accept_connections)

    def _accept_connections(self):
        while self._server.hasPendingConnections():
            socket = self._server.nextPendingConnection()
            if socket is None:
                continue
            self._buffers[socket] = bytearray()
            socket.readyRead.connect(
                lambda s=socket: self._guarded("读取请求", self._read_request, s)
            )
            socket.disconnected.connect(
                lambda s=socket: self._guarded("断开连接", self._drop_socket, s)
            )

    def _drop_socket(self, socket):
        self._buffers.pop(socket, None)
        socket.deleteLater()

    def _read_request(self, socket):
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
        # 对端可能已经走了；写不出去就算了，绝不能让异常冒出槽函数。
        if socket.state() != QtNetwork.QLocalSocket.LocalSocketState.ConnectedState:
            logger.debug("控制通道对端已断开，丢弃响应")
            return
        socket.write(encode_response(payload))
        socket.flush()
        # Windows 命名管道是异步写，不等它落地就断开，客户端可能什么都收不到。
        # 但必须先看还有没有待写数据：缓冲已空时调 waitForBytesWritten() 会被 Qt
        # 拒绝（"not allowed in UnconnectedState"），响应反而发不出去。
        if socket.bytesToWrite():
            socket.waitForBytesWritten(_TIMEOUT_MS)
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
        # 子命令进程没有跑事件循环，flush() 只是"尽量写、不阻塞"。Windows 命名管道
        # 上这一步是异步的，没写完就等它落地，否则请求可能根本没发出去——表现就是
        # 超时后报"守护进程没有在运行"。缓冲已空时不能调，Qt 会拒绝。
        if socket.bytesToWrite() and not socket.waitForBytesWritten(timeout_ms):
            logger.debug("控制指令写入超时: {}", command)
            return None

        buffer = bytearray()
        while b"\n" not in buffer:
            if socket.bytesAvailable():
                buffer.extend(bytes(socket.readAll()))
                continue
            if not socket.waitForReadyRead(timeout_ms):
                # 对端可能已经写完并断开：waitForReadyRead 会返回 False，
                # 但缓冲区里往往还躺着完整的响应。
                buffer.extend(bytes(socket.readAll()))
                break
        if b"\n" not in buffer:
            return None
        return decode_message(bytes(buffer).split(b"\n", 1)[0])
    finally:
        socket.disconnectFromServer()
        del app  # 仅为在阻塞等待期间保活临时的 QCoreApplication


def is_running() -> bool:
    return send_command("ping", timeout_ms=800) is not None
