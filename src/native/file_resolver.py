from __future__ import annotations

import asyncio
import contextlib
import re
from urllib.parse import quote

from src.native.capture import control_pipe_name
from src.native.control_client import ControlHookClient
from src.native.model import CapturedMessage, Segment
from src.native.pipe_transport import Win32NamedPipeTransport
from src.native.proto.wire import (
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)

_HOST_RE = re.compile(r"^[A-Za-z0-9.-]+$")


def _first(fields, field_no):
    values = fields.get(field_no)
    return values[0] if values else None


def _field_bytes(fields, field_no: int) -> bytes:
    value = _first(fields, field_no)
    return as_bytes(value) if value is not None else b""


def _field_int(fields, field_no: int) -> int:
    value = _first(fields, field_no)
    return as_int(value) if value is not None else 0


def _field_str(fields, field_no: int) -> str:
    value = _first(fields, field_no)
    return as_str(value) if value is not None else ""


def _oidb_envelope(command: int, sub_command: int, body: bytes, *, uin_form: bool) -> bytes:
    return b"".join(
        (
            encode_varint_field(1, command),
            encode_varint_field(2, sub_command),
            encode_varint_field(3, 0),
            encode_bytes_field(4, body),
            encode_bytes_field(5, b""),
            encode_varint_field(12, int(uin_form)),
        )
    )


def build_group_file_url_request(group_uin: int, file_id: str, bus_id: int = 102) -> bytes:
    download = b"".join(
        (
            encode_varint_field(1, group_uin),
            encode_varint_field(2, 7),
            encode_varint_field(3, bus_id),
            encode_bytes_field(4, file_id),
        )
    )
    return _oidb_envelope(0x6D6, 2, encode_bytes_field(3, download), uin_form=True)


def parse_group_file_url_response(data: bytes, file_id: str) -> str:
    envelope = decode_fields(data)
    error_code = _field_int(envelope, 3)
    if error_code:
        raise RuntimeError(f"group file OIDB error: {error_code}")
    response = decode_fields(_field_bytes(envelope, 4))
    download = decode_fields(_field_bytes(response, 3))
    ret_code = _field_int(download, 1)
    if ret_code:
        raise RuntimeError(f"group file URL error: {ret_code}")
    host = _field_str(download, 5) or _field_str(download, 4)
    download_key = _field_bytes(download, 6).hex().upper()
    if not host or not _HOST_RE.fullmatch(host) or not download_key:
        raise ValueError("invalid group file URL response")
    return f"https://{host}/ftn_handler/{download_key}/?fname={quote(file_id, safe='')}"


def build_private_file_url_request(account_uid: str, file_id: str, file_hash: str) -> bytes:
    request_body = b"".join(
        (
            encode_bytes_field(10, account_uid),
            encode_bytes_field(20, file_id),
            encode_varint_field(30, 2),
            encode_bytes_field(60, file_hash),
            encode_varint_field(601, 0),
        )
    )
    request = b"".join(
        (
            encode_varint_field(1, 1200),
            encode_varint_field(2, 1),
            encode_bytes_field(14, request_body),
            encode_varint_field(101, 3),
            encode_varint_field(102, 103),
            encode_varint_field(200, 1),
            encode_bytes_field(99999, bytes((0xC0, 0x85, 0x2C, 0x01))),
        )
    )
    return _oidb_envelope(0xE37, 1200, request, uin_form=False)


def parse_private_file_url_response(data: bytes) -> str:
    envelope = decode_fields(data)
    error_code = _field_int(envelope, 3)
    if error_code:
        raise RuntimeError(f"private file OIDB error: {error_code}")
    response = decode_fields(_field_bytes(envelope, 4))
    body = decode_fields(_field_bytes(response, 14))
    result = decode_fields(_field_bytes(body, 30))
    host = _field_str(result, 20)
    port = _field_int(result, 40)
    path = _field_str(result, 50)
    if not host or not _HOST_RE.fullmatch(host) or not 0 < port <= 65535:
        raise ValueError("invalid private file URL host")
    if not path.startswith("/") or path.startswith("//"):
        raise ValueError("invalid private file URL path")
    separator = "&" if "?" in path else "?"
    return f"http://{host}:{port}{path}{separator}isthumb=0"


def _resolve_file_url_sync(
    client: ControlHookClient, message: CapturedMessage, segment: Segment
) -> str:
    file_id = str(segment.extra.get("file_id", "") or "")
    if message.scene == "group":
        request = build_group_file_url_request(
            int(message.peer_id),
            file_id,
            int(segment.extra.get("bus_id", 102) or 102),
        )
        reply = client.send("OidbSvcTrpcTcp.0x6d6_2", request)
        if reply.error:
            raise RuntimeError(reply.message or f"QQ request error: {reply.error}")
        return parse_group_file_url_response(reply.body, file_id)

    request = build_private_file_url_request(
        message.account_uid,
        file_id,
        str(segment.extra.get("file_hash", "") or ""),
    )
    reply = client.send("OidbSvcTrpcTcp.0xe37_1200", request)
    if reply.error:
        raise RuntimeError(reply.message or f"QQ request error: {reply.error}")
    return parse_private_file_url_response(reply.body)


async def resolve_file_url(message: CapturedMessage, segment: Segment) -> str:
    file_id = str(segment.extra.get("file_id", "") or "")
    if not message.source_pid or not file_id:
        return ""
    if message.scene != "group" and not message.account_uid:
        return ""

    transport = await asyncio.to_thread(
        Win32NamedPipeTransport, control_pipe_name(message.source_pid)
    )
    client = ControlHookClient(transport)
    task = asyncio.create_task(asyncio.to_thread(_resolve_file_url_sync, client, message, segment))
    try:
        return await asyncio.wait_for(asyncio.shield(task), timeout=8.0)
    except TimeoutError:
        client.close()
        with contextlib.suppress(Exception):
            await asyncio.wait_for(task, timeout=1.0)
        return ""
    finally:
        client.close()
