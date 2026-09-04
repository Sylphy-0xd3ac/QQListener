from src.core.ipc import COMMANDS, ControlServer, decode_message, encode_request, encode_response


class _Server(ControlServer):
    """只测分发逻辑，不真的开监听。"""

    def __init__(self, handler):
        self._handler = handler


def test_request_and_response_are_newline_terminated_json():
    raw = encode_request("start", reason="测试")
    assert raw.endswith(b"\n")
    assert decode_message(raw) == {"command": "start", "reason": "测试"}
    assert decode_message(encode_response({"ok": True})) == {"ok": True}


def test_garbage_decodes_to_an_empty_dict():
    assert decode_message(b"not json\n") == {}
    assert decode_message(b"") == {}
    assert decode_message(b"[1,2]\n") == {}


def test_dispatch_rejects_unknown_and_missing_commands():
    server = _Server(lambda command, request: {})

    missing = server._dispatch({})
    assert missing["ok"] is False and "command" in missing["error"]

    unknown = server._dispatch({"command": "rm -rf"})
    assert unknown["ok"] is False
    assert unknown["commands"] == list(COMMANDS)


def test_dispatch_passes_the_command_through_and_wraps_the_result():
    seen = {}

    def handler(command, request):
        seen["command"] = command
        seen["request"] = request
        return {"core_state": "running"}

    server = _Server(handler)
    response = server._dispatch({"command": "STATUS", "extra": 1})

    assert seen["command"] == "status"
    assert seen["request"]["extra"] == 1
    assert response == {"ok": True, "command": "status", "data": {"core_state": "running"}}


def test_handler_errors_are_reported_not_raised():
    def handler(command, request):
        raise RuntimeError("核心未装载")

    response = _Server(handler)._dispatch({"command": "unload"})

    assert response["ok"] is False
    assert "核心未装载" in response["error"]


class _Probe(ControlServer):
    """只驱动 listen()，不真的开管道。"""

    def __init__(self, listen_ok=True):
        self.listen_calls = []
        self.other_instance = False
        self._listen_ok = listen_ok

        class _Srv:
            def __init__(self, outer):
                self._outer = outer

            def listen(self, name):
                self._outer.listen_calls.append(name)
                return self._outer._listen_ok

            def errorString(self):
                return "AddressInUseError"

        self._server = _Srv(self)


def test_single_instance_probes_before_listening(monkeypatch):
    """Qt 文档：Windows 上两个 QLocalServer 能同时监听同一个管道名，连接随机落到
    其中一个。所以绝不能用 "listen() 失败" 判断是否已有实例——那样 Windows 上会
    跑起两份守护进程，指令随机打到其中一份。"""
    monkeypatch.setattr("src.core.ipc.is_running", lambda: True)

    server = _Probe(listen_ok=True)  # 即使 listen 会成功
    ok = server.listen()

    assert ok is False
    assert server.other_instance is True
    assert server.listen_calls == [], "探到活实例后不该再去监听"


def test_listens_when_nothing_answers(monkeypatch):
    monkeypatch.setattr("src.core.ipc.is_running", lambda: False)

    server = _Probe(listen_ok=True)

    assert server.listen() is True
    assert server.other_instance is False
    assert len(server.listen_calls) == 1


def test_stale_unix_socket_is_cleaned_up_and_retried(monkeypatch):
    """Unix 上崩溃残留的 socket 文件会让 listen() 报 AddressInUseError。"""
    monkeypatch.setattr("src.core.ipc.is_running", lambda: False)
    removed = []
    monkeypatch.setattr(
        "src.core.ipc.QtNetwork.QLocalServer.removeServer", lambda name: removed.append(name)
    )

    server = _Probe(listen_ok=False)
    ok = server.listen()

    assert ok is False  # 两次都失败
    assert removed, "应先清理残留再重试"
    assert len(server.listen_calls) == 2


# ---------- 槽函数不能让异常逃逸 ----------


class _Guard(ControlServer):
    def __init__(self):
        self._buffers = {}
        self.other_instance = False


def test_slot_exceptions_never_escape():
    """PySide6 里 Qt 槽函数的未捕获异常会直接终止进程——守护进程不能因为
    一个断掉的管道就整个没了。"""
    server = _Guard()

    def boom():
        raise OSError("管道对端已消失")

    server._guarded("测试", boom)  # 不抛就是通过


def test_guarded_logs_the_failure():
    from loguru import logger

    server = _Guard()
    messages = []
    sink = logger.add(lambda m: messages.append(str(m)), level="ERROR")
    try:
        server._guarded("读取请求", lambda: (_ for _ in ()).throw(RuntimeError("炸了")))
    finally:
        logger.remove(sink)

    joined = "".join(messages)
    assert "读取请求" in joined and "炸了" in joined


def test_response_to_a_disconnected_peer_is_dropped_not_raised():
    from src.ui.qt_compat import QtNetwork

    class _Dead:
        def state(self):
            return QtNetwork.QLocalSocket.LocalSocketState.UnconnectedState

        def write(self, _data):  # pragma: no cover - 不该被调用
            raise AssertionError("对端已断开，不该再写")

    ControlServer._respond(_Dead(), {"ok": True})
