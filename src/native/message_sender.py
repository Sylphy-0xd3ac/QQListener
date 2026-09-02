"""通过 control 管道往会话里回一条文本消息（MessageSvc.PbSendMsg）。

线格式对齐 SnowLuma packages/proto-defs/src/action.ts 的 SendMessageRequest 与
packages/core/src/bridge/apis/message.ts 的 sendGroup / sendPrivate。
群聊回复会带上 SrcMsg(45) 元素，出现为"引用回复"。
"""

from __future__ import annotations

import asyncio
import contextlib
import random
import threading
import time
from dataclasses import dataclass

from loguru import logger

from src.native.capture import control_pipe_name
from src.native.control_client import ControlHookClient
from src.native.model import CapturedMessage
from src.native.pipe_transport import Win32NamedPipeTransport
from src.native.proto.wire import (
    as_int,
    as_str,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)

SEND_MSG_CMD = "MessageSvc.PbSendMsg"

_ELEM_TEXT = 1
_ELEM_SRC_MSG = 45
_RICHTEXT_ELEMS = 2

_UINT32_MASK = 0xFFFFFFFF


@dataclass(frozen=True)
class SendResult:
    result: int
    error_message: str
    sequence: int


class _SequenceSource:
    """客户端序列号 / 消息 random 的来源，形状照抄 SnowLuma Bridge。"""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._client_seq = 100000000 + int(time.time() * 1000) % 1000000000
        self._random = random.getrandbits(32)

    def next_client_sequence(self) -> int:
        with self._lock:
            self._client_seq = (self._client_seq + 1) & _UINT32_MASK
            return self._client_seq

    def next_random(self) -> int:
        with self._lock:
            self._random = (self._random + 0x9E3779B9) & _UINT32_MASK
            return self._random & 0x7FFFFFFF


_sequences = _SequenceSource()


def _text_elem(text: str) -> bytes:
    return encode_bytes_field(_ELEM_TEXT, encode_bytes_field(1, text))


def _src_msg_elem(*, seq: int, sender_uin: int, timestamp: int) -> bytes:
    """SrcMsg：origSeqs=1, senderUin=2, time=3（对齐 element-builder.makeReplyElem）。"""
    src = encode_varint_field(1, seq) + encode_varint_field(2, sender_uin)
    if timestamp:
        src += encode_varint_field(3, timestamp)
    return encode_bytes_field(_ELEM_SRC_MSG, src)


def build_send_text_request(
    *,
    scene: str,
    peer_uin: int,
    text: str,
    peer_uid: str = "",
    client_sequence: int = 0,
    message_random: int = 0,
    quote_seq: int = 0,
    quote_sender_uin: int = 0,
    quote_time: int = 0,
) -> bytes:
    if not text:
        raise ValueError("message is empty")
    if peer_uin <= 0:
        raise ValueError("peer uin is invalid")

    elems = b""
    if quote_seq and quote_sender_uin:
        elems += encode_bytes_field(
            _RICHTEXT_ELEMS,
            _src_msg_elem(seq=quote_seq, sender_uin=quote_sender_uin, timestamp=quote_time),
        )
    elems += encode_bytes_field(_RICHTEXT_ELEMS, _text_elem(text))
    message_body = encode_bytes_field(3, encode_bytes_field(1, elems))

    if scene == "group":
        routing = encode_bytes_field(2, encode_varint_field(1, peer_uin))
        content_head = encode_varint_field(1, 1)
        client_sequence = 0
    else:
        c2c = encode_varint_field(1, peer_uin)
        if peer_uid:
            c2c += encode_bytes_field(2, peer_uid)
        routing = encode_bytes_field(1, c2c)
        content_head = (
            encode_varint_field(1, 1) + encode_varint_field(2, 0) + encode_varint_field(3, 11)
        )

    request = (
        encode_bytes_field(1, routing)
        + encode_bytes_field(2, content_head)
        + message_body
        + encode_varint_field(4, client_sequence)
        + encode_varint_field(5, message_random)
        + encode_bytes_field(6, b"")
        + encode_varint_field(8, 0)
        + encode_varint_field(9, 0)
    )
    if scene != "group":
        request += encode_bytes_field(12, encode_varint_field(1, int(time.time())))
    return request + encode_varint_field(14, 0)


def parse_send_message_response(data: bytes) -> SendResult:
    fields = decode_fields(data)
    result_value = fields.get(1)
    error_value = fields.get(2)
    group_seq = fields.get(11)
    private_seq = fields.get(14)
    sequence = 0
    if group_seq:
        sequence = as_int(group_seq[0])
    elif private_seq:
        sequence = as_int(private_seq[0])
    return SendResult(
        result=as_int(result_value[0]) if result_value else 0,
        error_message=as_str(error_value[0]) if error_value else "",
        sequence=sequence,
    )


def _send_text_sync(
    pid: int,
    *,
    scene: str,
    peer_uin: int,
    text: str,
    peer_uid: str,
    quote_seq: int,
    quote_sender_uin: int,
    quote_time: int,
) -> SendResult:
    request = build_send_text_request(
        scene=scene,
        peer_uin=peer_uin,
        text=text,
        peer_uid=peer_uid,
        client_sequence=0 if scene == "group" else _sequences.next_client_sequence(),
        message_random=_sequences.next_random(),
        quote_seq=quote_seq,
        quote_sender_uin=quote_sender_uin,
        quote_time=quote_time,
    )
    logger.info("发送回复 scene={} peer={} 引用seq={}", scene, peer_uin, quote_seq)
    transport = Win32NamedPipeTransport(control_pipe_name(pid))
    client = ControlHookClient(transport)
    try:
        reply = client.send(SEND_MSG_CMD, request)
        if reply.error:
            raise RuntimeError(reply.message or f"QQ request error: {reply.error}")
        result = parse_send_message_response(reply.body)
        if result.result:
            raise RuntimeError(
                result.error_message or f"send message rejected: result={result.result}"
            )
        logger.info("回复已发出 sequence={}", result.sequence)
        return result
    finally:
        client.close()


def _int_or_zero(value: object) -> int:
    try:
        return int(str(value).strip())
    except (TypeError, ValueError):
        return 0


def send_text_reply_sync(route: dict, text: str) -> SendResult:
    """`route` 由 build_reply_route 产出，可跨线程传递（纯 dict）。"""
    pid = _int_or_zero(route.get("pid"))
    peer_uin = _int_or_zero(route.get("peer_id"))
    scene = str(route.get("scene") or "c2c")
    if not pid:
        raise RuntimeError("尚未连接到 QQ 进程")
    quote_seq = _int_or_zero(route.get("quote_seq"))
    quote_sender = _int_or_zero(route.get("quote_sender_id"))
    # 只有群聊支持按序列号引用；私聊的 origSeqs 语义不同，直接发普通消息。
    if scene != "group":
        quote_seq = 0
        quote_sender = 0
    return _send_text_sync(
        pid,
        scene=scene,
        peer_uin=peer_uin,
        text=text,
        peer_uid=str(route.get("peer_uid") or ""),
        quote_seq=quote_seq,
        quote_sender_uin=quote_sender,
        quote_time=_int_or_zero(route.get("quote_time")),
    )


def build_reply_route(msg: CapturedMessage) -> dict:
    """把一条捕获消息压成"回哪里"的纯数据，供 UI 线程稍后发送。"""
    return {
        "pid": msg.source_pid,
        "scene": msg.scene,
        "peer_id": msg.peer_id,
        "peer_uid": msg.peer_uid,
        "peer_name": msg.peer_name,
        "quote_seq": msg.raw_seq,
        "quote_sender_id": msg.sender_id,
        "quote_time": msg.timestamp,
    }


async def send_text_reply(route: dict, text: str) -> SendResult:
    task = asyncio.create_task(asyncio.to_thread(send_text_reply_sync, route, text))
    try:
        return await asyncio.wait_for(asyncio.shield(task), timeout=10.0)
    except TimeoutError:
        with contextlib.suppress(Exception):
            await asyncio.wait_for(task, timeout=1.0)
        raise
