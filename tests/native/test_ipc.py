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
