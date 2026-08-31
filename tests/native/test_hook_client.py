import asyncio

from src.native.hook_client import LoginState, RecvHookClient
from src.native.hqp1 import FLAG_LOGGED_IN, PipeOp, encode_frame
from src.native.pipe_transport import FakeTransport


def test_recv_client_dispatches_packet_and_login():
    login_frame = encode_frame(PipeOp.login_state, flags=FLAG_LOGGED_IN, value0=10001, msg="10001")
    pkt_frame = encode_frame(
        PipeOp.recv_packet, status=0, value0=5, cmd="svc", msg="10001", body=b"pb"
    )
    transport = FakeTransport([login_frame + pkt_frame])

    packets = []
    logins = []
    client = RecvHookClient(transport)

    async def go():
        await asyncio.wait_for(
            client.run(on_packet=packets.append, on_login=logins.append),
            timeout=2.0,
        )

    asyncio.run(go())

    assert logins and isinstance(logins[0], LoginState)
    assert logins[0].logged_in is True and logins[0].uin == "10001"
    assert len(packets) == 1
    assert packets[0].cmd == "svc" and packets[0].seq == 5 and packets[0].body == b"pb"
