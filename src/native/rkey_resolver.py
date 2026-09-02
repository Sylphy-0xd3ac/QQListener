"""NT 图片/媒体下载凭证（rkey）。

QQ-NT 的 `multimedia.nt.qq.com.cn/download?...` 地址必须带一枚服务器签发的
短效 `rkey`，否则 CDN 直接以 `{"retcode":-5503010,"retmsg":"invalid rkey"}` 拒绝——
表现就是通知里图片"炸"了。

推送里 `ExtBizInfo.pic.bytesPbReserveC2c.field30` 有时自带 rkey（私聊图片常见），
拿不到时按 SnowLuma 的做法走 OIDB 0x9067_202 取一批 rkey 并按类型缓存。
"""

from __future__ import annotations

import asyncio
import contextlib
import re
import threading
import time
from dataclasses import dataclass

from src.native.capture import control_pipe_name
from src.native.control_client import ControlHookClient
from src.native.pipe_transport import Win32NamedPipeTransport
from src.native.proto.wire import (
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)

RKEY_TYPE_PRIVATE_IMAGE = 10
RKEY_TYPE_GROUP_IMAGE = 20
RKEY_TYPE_FALLBACK_IMAGE = 2
RKEY_TYPE_PRIVATE_VIDEO = 12
RKEY_TYPE_GROUP_VIDEO = 22
RKEY_TYPE_PRIVATE_PTT = 14
RKEY_TYPE_GROUP_PTT = 24

_REQUEST_TYPES = (
    RKEY_TYPE_PRIVATE_IMAGE,
    RKEY_TYPE_GROUP_IMAGE,
    RKEY_TYPE_FALLBACK_IMAGE,
    RKEY_TYPE_PRIVATE_VIDEO,
    RKEY_TYPE_GROUP_VIDEO,
)

# 快到期就当作没有：拿一把过期 rkey 去下载和不带是一个结果。
_EXPIRY_SKEW_S = 60
_REFRESH_COOLDOWN_S = 30
_EMPTY_COOLDOWN_S = 10
_REFRESH_TIMEOUT_S = 5.0

_APPID_RE = re.compile(r"[?&]appid=(\d+)")
_APPID_TO_TYPE = {"1406": RKEY_TYPE_PRIVATE_IMAGE, "1407": RKEY_TYPE_GROUP_IMAGE}


@dataclass(frozen=True)
class RKeyInfo:
    rkey: str
    type: int
    ttl_seconds: int = 0
    create_time: int = 0


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


def _oidb_envelope(body: bytes) -> bytes:
    return b"".join(
        (
            encode_varint_field(1, 0x9067),
            encode_varint_field(2, 202),
            encode_varint_field(3, 0),
            encode_bytes_field(4, body),
            encode_bytes_field(5, b""),
            encode_varint_field(12, 1),
        )
    )


def build_download_rkey_request() -> bytes:
    """OIDB 0x9067_202 / NTV2RichMediaReq{reqHead, downloadRkey}。"""
    common = encode_varint_field(1, 1) + encode_varint_field(2, 202)
    scene = encode_varint_field(101, 2) + encode_varint_field(102, 1) + encode_varint_field(200, 0)
    client = encode_varint_field(1, 2)
    req_head = (
        encode_bytes_field(1, common) + encode_bytes_field(2, scene) + encode_bytes_field(3, client)
    )
    download_rkey = b"".join(encode_varint_field(1, t) for t in _REQUEST_TYPES)
    return _oidb_envelope(encode_bytes_field(1, req_head) + encode_bytes_field(4, download_rkey))


def parse_download_rkey_response(data: bytes) -> list[RKeyInfo]:
    envelope = decode_fields(data)
    error_code = _field_int(envelope, 3)
    if error_code:
        raise RuntimeError(f"rkey OIDB error: {error_code}")

    response = decode_fields(_field_bytes(envelope, 4))
    head_data = _field_bytes(response, 1)
    if head_data:
        head = decode_fields(head_data)
        ret_code = _field_int(head, 2)
        if ret_code:
            raise RuntimeError(_field_str(head, 3) or f"rkey request failed: {ret_code}")

    rkey_data = _field_bytes(response, 4)
    if not rkey_data:
        return []
    rkeys: list[RKeyInfo] = []
    for item in decode_fields(rkey_data).get(1, []):
        entry = decode_fields(as_bytes(item))
        rkey = _field_str(entry, 1)
        rkey_type = _field_int(entry, 5)
        if not rkey or not rkey_type:
            continue
        rkeys.append(
            RKeyInfo(
                rkey=rkey,
                type=rkey_type,
                ttl_seconds=_field_int(entry, 2),
                create_time=_field_int(entry, 4),
            )
        )
    return rkeys


def rkey_type_for_url(url: str, *, is_group: bool) -> int:
    """rkey 的场景跟图片自己的上传上下文走（URL 里的 appid），不是承载它的消息。"""
    match = _APPID_RE.search(url or "")
    if match:
        mapped = _APPID_TO_TYPE.get(match.group(1))
        if mapped:
            return mapped
    return RKEY_TYPE_GROUP_IMAGE if is_group else RKEY_TYPE_PRIVATE_IMAGE


def url_needs_rkey(url: str) -> bool:
    if not url or "rkey=" in url:
        return False
    if "gchat.qpic.cn" in url:
        return False
    return ".nt.qq.com.cn" in url or "/download" in url


def _resolve_rkeys_sync(pid: int) -> list[RKeyInfo]:
    transport = Win32NamedPipeTransport(control_pipe_name(pid))
    client = ControlHookClient(transport)
    try:
        reply = client.send("OidbSvcTrpcTcp.0x9067_202", build_download_rkey_request())
        if reply.error:
            raise RuntimeError(reply.message or f"QQ request error: {reply.error}")
        return parse_download_rkey_response(reply.body)
    finally:
        client.close()


class RKeyCache:
    """按类型缓存 rkey，带过期与失败退避；线程安全。"""

    def __init__(self, fetch=_resolve_rkeys_sync) -> None:
        self._fetch = fetch
        self._lock = threading.RLock()
        self._entries: dict[int, tuple[str, float]] = {}
        self._last_attempt = 0.0

    def _lookup(self, rkey_type: int, now: float) -> str:
        with self._lock:
            for candidate in (rkey_type, RKEY_TYPE_FALLBACK_IMAGE):
                entry = self._entries.get(candidate)
                if entry is None:
                    continue
                value, expires_at = entry
                if value and (expires_at == 0 or now + _EXPIRY_SKEW_S < expires_at):
                    return value
        return ""

    def _store(self, rkeys: list[RKeyInfo], now: float) -> None:
        with self._lock:
            for info in rkeys:
                base = info.create_time or now
                ttl = info.ttl_seconds or 3600
                self._entries[info.type] = (info.rkey, base + ttl)

    def _has_usable(self, now: float) -> bool:
        return bool(
            self._lookup(RKEY_TYPE_GROUP_IMAGE, now) or self._lookup(RKEY_TYPE_PRIVATE_IMAGE, now)
        )

    def _should_refresh(self, now: float) -> bool:
        with self._lock:
            cooldown = _REFRESH_COOLDOWN_S if self._has_usable(now) else _EMPTY_COOLDOWN_S
            if now - self._last_attempt < cooldown:
                return False
            self._last_attempt = now
            return True

    def refresh(self, pid: int) -> None:
        now = time.time()
        if not self._should_refresh(now):
            return
        self._store(self._fetch(pid), now)

    def get(self, pid: int, rkey_type: int) -> str:
        now = time.time()
        cached = self._lookup(rkey_type, now)
        if cached:
            return cached
        self.refresh(pid)
        return self._lookup(rkey_type, time.time())


_cache = RKeyCache()


async def resolve_rkey(pid: int, url: str, *, is_group: bool) -> str:
    """给一条 NT 下载地址取 rkey；取不到返回空串（调用方原样用裸地址）。"""
    if not pid or not url_needs_rkey(url):
        return ""
    rkey_type = rkey_type_for_url(url, is_group=is_group)
    task = asyncio.create_task(asyncio.to_thread(_cache.get, pid, rkey_type))
    try:
        return await asyncio.wait_for(asyncio.shield(task), timeout=_REFRESH_TIMEOUT_S)
    except Exception:
        with contextlib.suppress(Exception):
            await asyncio.wait_for(task, timeout=0.1)
        return ""
