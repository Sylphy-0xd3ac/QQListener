from __future__ import annotations

import asyncio
import contextlib
from dataclasses import dataclass

from src.native.capture import control_pipe_name
from src.native.control_client import ControlHookClient
from src.native.model import CapturedMessage
from src.native.pipe_transport import Win32NamedPipeTransport
from src.native.proto.wire import (
    as_bytes,
    as_int,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)

_PROFILE_PROPERTY_NICKNAME = 20002
_PROFILE_PROPERTY_REMARK = 103
_PROFILE_KEYS = (_PROFILE_PROPERTY_NICKNAME, _PROFILE_PROPERTY_REMARK)


@dataclass(frozen=True)
class UserProfileNames:
    nickname: str = ""
    remark: str = ""


def _first(fields, field_no):
    values = fields.get(field_no)
    return values[0] if values else None


def _field_bytes(fields, field_no: int) -> bytes:
    value = _first(fields, field_no)
    return as_bytes(value) if value is not None else b""


def _field_int(fields, field_no: int) -> int:
    value = _first(fields, field_no)
    return as_int(value) if value is not None else 0


def _oidb_envelope(body: bytes, *, uin_form: bool) -> bytes:
    return b"".join(
        (
            encode_varint_field(1, 0xFE1),
            encode_varint_field(2, 2),
            encode_varint_field(3, 0),
            encode_bytes_field(4, body),
            encode_bytes_field(5, b""),
            encode_varint_field(12, int(uin_form)),
        )
    )


def build_user_profile_request(user_id: str) -> bytes:
    """构造 SnowLuma 同款 OIDB 0xFE1_2 资料查询。"""
    normalized = str(user_id or "").strip()
    if not normalized:
        raise ValueError("user id is empty")

    if normalized.isdecimal() and int(normalized) > 0:
        body = encode_varint_field(1, int(normalized))
        uin_form = True
    else:
        body = encode_bytes_field(1, normalized)
        uin_form = False

    for key in _PROFILE_KEYS:
        body += encode_bytes_field(3, encode_varint_field(1, key))
    return _oidb_envelope(body, uin_form=uin_form)


def parse_user_profile_response(data: bytes) -> UserProfileNames:
    envelope = decode_fields(data)
    error_code = _field_int(envelope, 3)
    if error_code:
        raise RuntimeError(f"profile OIDB error: {error_code}")

    response = decode_fields(_field_bytes(envelope, 4))
    body_data = _field_bytes(response, 1)
    if not body_data:
        raise ValueError("profile response body missing")
    body = decode_fields(body_data)
    properties_data = _field_bytes(body, 2)
    properties = decode_fields(properties_data) if properties_data else {}

    values: dict[int, str] = {}
    for item in properties.get(2, []):
        prop = decode_fields(as_bytes(item))
        code = _field_int(prop, 1)
        value = _field_bytes(prop, 2)
        if code in _PROFILE_KEYS and value:
            values[code] = value.decode("utf-8", "replace").strip()

    return UserProfileNames(
        nickname=values.get(_PROFILE_PROPERTY_NICKNAME, ""),
        remark=values.get(_PROFILE_PROPERTY_REMARK, ""),
    )


def _resolve_user_profile_sync(client: ControlHookClient, user_id: str) -> UserProfileNames:
    reply = client.send(
        "OidbSvcTrpcTcp.0xfe1_2",
        build_user_profile_request(user_id),
    )
    if reply.error:
        raise RuntimeError(reply.message or f"QQ request error: {reply.error}")
    return parse_user_profile_response(reply.body)


async def resolve_user_profile(message: CapturedMessage) -> UserProfileNames:
    if not message.source_pid or not message.sender_id:
        return UserProfileNames()

    transport = await asyncio.to_thread(
        Win32NamedPipeTransport, control_pipe_name(message.source_pid)
    )
    client = ControlHookClient(transport)
    task = asyncio.create_task(
        asyncio.to_thread(_resolve_user_profile_sync, client, message.sender_id)
    )
    try:
        return await asyncio.wait_for(asyncio.shield(task), timeout=3.0)
    except TimeoutError:
        client.close()
        with contextlib.suppress(Exception):
            await asyncio.wait_for(task, timeout=1.0)
        return UserProfileNames()
    finally:
        client.close()
