import asyncio
import sys
from types import SimpleNamespace

from src.native.capture import (
    RecvCapture,
    _is_main_qq_process,
    control_pipe_name,
    enumerate_qq_pids,
    recv_pipe_name,
)
from src.native.hqp1 import PipeOp, encode_frame
from src.native.pipe_transport import FakeTransport


def test_recv_pipe_name():
    assert recv_pipe_name(4321) == r"\\.\pipe\mojo.4321.recv"
    assert control_pipe_name(4321) == r"\\.\pipe\mojo.4321.control"


def test_main_qq_process_filter_skips_electron_children():
    assert _is_main_qq_process("QQ.exe", [r"C:\QQ\QQ.exe"]) is True
    assert _is_main_qq_process("qq.EXE", [r"C:\QQ\QQ.exe", "--no-sandbox"]) is True
    assert _is_main_qq_process("QQ.exe", [r"C:\QQ\QQ.exe", "--type=renderer"]) is False
    assert _is_main_qq_process("QQ.exe", [r"C:\QQ\QQ.exe", "--type", "utility"]) is False
    assert _is_main_qq_process("QQ.exe", None) is False
    assert _is_main_qq_process("QQMusic.exe", [r"C:\QQMusic.exe"]) is False


def test_enumerate_qq_pids_returns_only_sorted_main_processes(monkeypatch):
    processes = [
        SimpleNamespace(pid=30, info={"name": "QQ.exe", "cmdline": ["QQ.exe"]}),
        SimpleNamespace(
            pid=20,
            info={"name": "QQ.exe", "cmdline": ["QQ.exe", "--type=renderer"]},
        ),
        SimpleNamespace(pid=10, info={"name": "qq.EXE", "cmdline": ["QQ.exe"]}),
    ]
    fake_psutil = SimpleNamespace(process_iter=lambda attrs, ad_value=None: processes)
    monkeypatch.setitem(sys.modules, "psutil", fake_psutil)

    assert enumerate_qq_pids() == [10, 30]


def test_recv_capture_emits_decoded_message():
    # 构造一个非消息 cmd 的帧，验证「不回调」；真消息路径由 sso_decode 的 fixture 测试覆盖。
    frame = encode_frame(PipeOp.recv_packet, value0=1, cmd="not.a.message", msg="10001", body=b"")
    transport = FakeTransport([frame])
    got = []
    cap = RecvCapture(pid=1, on_message=got.append, transport_factory=lambda name: transport)
    asyncio.run(asyncio.wait_for(cap.run(), timeout=2.0))
    assert got == []  # 非消息 cmd 不产出


def test_recv_capture_reports_pipe_connection_lifecycle():
    transport = FakeTransport([encode_frame(PipeOp.hello)])
    lifecycle = []
    cap = RecvCapture(
        pid=1,
        on_message=lambda _msg: None,
        transport_factory=lambda _name: transport,
        on_connected=lambda: lifecycle.append("connected"),
        on_disconnected=lambda: lifecycle.append("disconnected"),
    )

    asyncio.run(asyncio.wait_for(cap.run(), timeout=2.0))

    assert lifecycle == ["connected", "disconnected"]
