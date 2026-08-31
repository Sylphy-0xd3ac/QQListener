from src.native.control_client import ControlHookClient
from src.native.hqp1 import FrameReader, PipeOp, encode_frame
from src.native.pipe_transport import FakeTransport


def test_control_client_sends_request_after_hello_and_returns_reply():
    transport = FakeTransport(
        [
            encode_frame(PipeOp.hello, value0=1234, msg="control"),
            encode_frame(PipeOp.send_ack, request_id=1)
            + encode_frame(PipeOp.send_reply, request_id=1, body=b"response"),
        ]
    )
    client = ControlHookClient(transport)

    reply = client.send("OidbSvcTrpcTcp.0x6d6_2", b"request")

    assert reply.body == b"response"
    sent = FrameReader().push(transport.writes[0])[0]
    assert sent.op == int(PipeOp.send_request)
    assert sent.cmd == "OidbSvcTrpcTcp.0x6d6_2"
    assert sent.body == b"request"


def test_control_client_surfaces_pipe_error():
    transport = FakeTransport(
        [
            encode_frame(PipeOp.hello),
            encode_frame(PipeOp.error, request_id=1, status=7, msg="rejected"),
        ]
    )

    try:
        ControlHookClient(transport).send("svc", b"")
    except RuntimeError as exc:
        assert "rejected" in str(exc)
    else:
        raise AssertionError("expected RuntimeError")
