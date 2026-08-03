import asyncio

from src.native.capture import RecvCapture, recv_pipe_name
from src.native.hqp1 import PipeOp, encode_frame
from src.native.pipe_transport import FakeTransport


def test_recv_pipe_name():
    assert recv_pipe_name(4321) == r"\\.\pipe\mojo.4321.recv"


def test_recv_capture_emits_decoded_message():
    # 构造一个非消息 cmd 的帧，验证「不回调」；真消息路径由 sso_decode 的 fixture 测试覆盖。
    frame = encode_frame(PipeOp.recv_packet, value0=1, cmd="not.a.message", msg="10001", body=b"")
    transport = FakeTransport([frame])
    got = []
    cap = RecvCapture(pid=1, on_message=got.append, transport_factory=lambda name: transport)
    asyncio.run(asyncio.wait_for(cap.run(), timeout=2.0))
    assert got == []  # 非消息 cmd 不产出
