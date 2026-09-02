from urllib.parse import quote

from src.native.model import Segment
from src.native.proto.element import (
    MENTION_TYPE_REPLY,
    MENTION_TYPE_USER,
    ImageElem,
    image_url_from_md5,
    make_image_url,
    parse_custom_face,
    parse_group_file_elem,
    parse_mention_extra,
    parse_not_online_image,
    parse_src_msg,
)
from src.native.proto.wire import as_bytes, as_int, as_str, decode_fields

# Elem 子字段号（来自 SnowLuma proto-defs element.ts 的 Elem 接口，权威）
ELEM_TEXT = 1
ELEM_FACE = 2
ELEM_NOT_ONLINE_IMAGE = 4
ELEM_TRANS = 5
ELEM_CUSTOM_FACE = 8  # NT QQ 群图片多走此元素
ELEM_GROUP_FILE = 13
ELEM_VIDEO_FILE = 19
ELEM_SRC_MSG = 45  # 回复/引用
ELEM_COMMON = 53

# RichText.elems 的字段号（已由真实 NTQQ OlPush 帧校准）
RICHTEXT_ELEMS = 2

# TextElem 子字段：str=1, attr6Buf=3, pbReserve=12
_TEXT_STR = 1
_TEXT_ATTR6 = 3
_TEXT_PB_RESERVE = 12

# @全体成员在 wire 上 uin=0，只能靠文本认。
_AT_ALL_TEXTS = {"@全体成员", "@all", "@everyone"}

# ExtBizInfo.pic.bytesPbReserveC2c(11).field30 里带着这条消息自己的 rkey。
# 少了它 CDN 会以 `invalid rkey` 拒绝下载——图片就是这样"炸"成占位符的。
_EXT_BIZ_INFO = 2
_EXT_BIZ_PIC = 1
_PIC_PB_RESERVE_C2C = 11
_PIC_RESERVE_RKEY = 30


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


def _image_segment(img: ImageElem) -> Segment:
    url = (
        make_image_url(img.orig_url) or make_image_url(img.big_url) or make_image_url(img.thumb_url)
    )
    if not url:
        url = image_url_from_md5(img.md5)
    return Segment(
        type="image",
        url=url,
        md5=img.md5,
        name=img.name,
        extra={"size": img.file_size} if img.file_size else {},
    )


def _pic_rkey(msg_info: dict) -> str:
    """MsgInfo.extBizInfo.pic.bytesPbReserveC2c.field30 → `&rkey=...`。"""
    ext_biz = _field_bytes(msg_info, _EXT_BIZ_INFO)
    if not ext_biz:
        return ""
    pic = _field_bytes(decode_fields(ext_biz), _EXT_BIZ_PIC)
    if not pic:
        return ""
    reserve = _field_bytes(decode_fields(pic), _PIC_PB_RESERVE_C2C)
    if not reserve:
        return ""
    return _field_str(decode_fields(reserve), _PIC_RESERVE_RKEY)


def append_rkey(url: str, rkey: str) -> str:
    """把 rkey 拼到下载地址上；已经带 rkey 或缺任一方时原样返回。"""
    if not url or not rkey or "rkey=" in url:
        return url
    token = rkey
    for prefix in ("&rkey=", "?rkey="):
        if token.startswith(prefix):
            token = token[len(prefix) :]
            break
    if not token:
        return url
    separator = "&" if "?" in url else "?"
    return f"{url}{separator}rkey={quote(token, safe='')}"


def _parse_nt_common_elem(body: bytes, *, is_group: bool) -> Segment | None:
    """解析 NTQQ CommonElem(service=48) 的图片槽位。"""
    common = decode_fields(body)
    service_type = _field_int(common, 1)
    business_type = _field_int(common, 3)
    payload = _field_bytes(common, 2)
    if not payload or not (service_type == 48 or business_type in {10, 20, 11, 21, 12, 22}):
        return None
    if business_type not in {10, 20}:
        return None

    msg_info = decode_fields(payload)
    body_value = _first(msg_info, 1)
    if body_value is None:
        return None
    info_body = decode_fields(as_bytes(body_value))
    index_data = _field_bytes(info_body, 1)
    if not index_data:
        return None
    index = decode_fields(index_data)
    file_info_data = _field_bytes(index, 1)
    if not file_info_data:
        return None
    file_info = decode_fields(file_info_data)

    file_uuid = _field_str(index, 2)
    file_name = _field_str(file_info, 4)
    url = ""
    picture_data = _field_bytes(info_body, 2)
    if picture_data:
        picture = decode_fields(picture_data)
        path = _field_str(picture, 1)
        domain = _field_str(picture, 3) or "multimedia.nt.qq.com.cn"
        if path.startswith(("http://", "https://")):
            url = path
        elif path.startswith("/") and "/" not in domain:
            url = f"https://{domain}{path}"
        extension_data = _field_bytes(picture, 2)
        if url and extension_data:
            url += _field_str(decode_fields(extension_data), 1)
    if not url and file_uuid:
        app_id = 1407 if is_group else 1406
        url = (
            f"https://multimedia.nt.qq.com.cn/download?appid={app_id}"
            f"&fileid={quote(file_uuid, safe='')}"
        )
    url = append_rkey(url, _pic_rkey(msg_info))

    return Segment(
        type="image",
        url=url,
        name=file_name or file_uuid,
        md5=_field_str(file_info, 2),
        extra={
            "size": _field_int(file_info, 1),
            "width": _field_int(file_info, 6),
            "height": _field_int(file_info, 7),
            "file_id": file_uuid,
            "sha1": _field_str(file_info, 3),
            "nt": True,
        },
    )


def _parse_group_file_trans_elem(body: bytes) -> Segment | None:
    """解析 NTQQ 群文件常见的 TransElem(type=24) 结构。"""
    trans = decode_fields(body)
    if _field_int(trans, 1) != 24:
        return None
    value = _field_bytes(trans, 2)
    if len(value) < 3:
        return None
    payload_size = int.from_bytes(value[1:3], "big")
    if payload_size <= 0 or len(value) < 3 + payload_size:
        return None

    extra = decode_fields(value[3 : 3 + payload_size])
    inner_data = _field_bytes(extra, 7)
    inner = decode_fields(inner_data) if inner_data else {}
    info_data = _field_bytes(inner, 2)
    info = decode_fields(info_data) if info_data else {}
    file_id = _field_str(info, 2)
    if not file_id:
        return None
    file_name = _field_str(info, 4) or _field_str(extra, 2)
    return Segment(
        type="file",
        name=file_name,
        md5=_field_bytes(info, 8).hex(),
        extra={
            "bus_id": _field_int(info, 1) or 102,
            "size": _field_int(info, 3),
            "file_id": file_id,
            "sha1": _field_bytes(info, 6).hex(),
        },
    )


def _text_segment(text_body: bytes, *, saw_reply: bool) -> tuple[Segment | None, bool]:
    """把 TextElem 变成 text/at 段。

    第二个返回值为 True 表示这是回复消息自带的结构性 @（不是用户内容），
    调用方应连同紧随其后的空白文本一起丢掉。
    """
    t = decode_fields(text_body)
    text = _field_str(t, _TEXT_STR)
    attr6 = _field_bytes(t, _TEXT_ATTR6)
    reserve = _field_bytes(t, _TEXT_PB_RESERVE)
    mention = parse_mention_extra(reserve) if reserve else None

    if (
        saw_reply
        and mention is not None
        and mention.type == MENTION_TYPE_REPLY
        and mention.uin == 0
    ):
        return None, True

    has_attr6 = len(attr6) > 11
    has_mention = mention is not None and mention.type in (MENTION_TYPE_USER, MENTION_TYPE_REPLY)
    if has_attr6 or has_mention:
        uin = int.from_bytes(attr6[7:11], "big") if has_attr6 else 0
        if not uin and mention is not None:
            uin = mention.uin
        target = str(uin) if uin else (mention.uid if mention is not None else "")
        if not target and text.strip() in _AT_ALL_TEXTS:
            target = "all"
        return Segment(type="at", text=text, target_id=target), False

    return (Segment(type="text", text=text) if text else None), False


def _reply_segment(src_body: bytes, *, is_group: bool) -> Segment | None:
    """SrcMsg(45) → reply 段；被引用消息的元素一并解析进 extra['segments']。"""
    src = parse_src_msg(src_body)
    if not src.orig_seq and not src.elems and not src.sender_uin:
        return None
    quoted = _convert_elems(src.elems, is_group=is_group) if src.elems else []
    return Segment(
        type="reply",
        target_id=str(src.sender_uin) if src.sender_uin else "",
        extra={
            "seq": src.orig_seq,
            "time": src.time,
            "sender_id": str(src.sender_uin) if src.sender_uin else "",
            "troop_name": src.troop_name,
            "segments": quoted,
        },
    )


def _elem_to_segment(elem_body: bytes, *, is_group: bool) -> Segment | None:
    """非 text/srcMsg 元素 → Segment。"""
    f = decode_fields(elem_body)
    if ELEM_NOT_ONLINE_IMAGE in f:
        return _image_segment(parse_not_online_image(as_bytes(f[ELEM_NOT_ONLINE_IMAGE][0])))
    if ELEM_TRANS in f:
        return _parse_group_file_trans_elem(as_bytes(f[ELEM_TRANS][0]))
    if ELEM_CUSTOM_FACE in f:
        return _image_segment(parse_custom_face(as_bytes(f[ELEM_CUSTOM_FACE][0])))
    if ELEM_GROUP_FILE in f:
        fe = parse_group_file_elem(as_bytes(f[ELEM_GROUP_FILE][0]))
        return Segment(
            type="file",
            name=fe.name,
            url=fe.url,
            md5=fe.md5,
            extra={
                "bus_id": 102,
                "size": fe.size,
                "file_id": fe.file_id,
                "file_key": fe.file_key,
            },
        )
    if ELEM_VIDEO_FILE in f:
        video = decode_fields(as_bytes(f[ELEM_VIDEO_FILE][0]))
        return Segment(
            type="video",
            name=_field_str(video, 3),
            md5=_field_bytes(video, 2).hex(),
            extra={
                "size": _field_int(video, 6),
                "file_id": _field_str(video, 1),
                "duration": _field_int(video, 5),
            },
        )
    if ELEM_COMMON in f:
        return _parse_nt_common_elem(as_bytes(f[ELEM_COMMON][0]), is_group=is_group)
    return None


def _is_nt_image(seg: Segment) -> bool:
    if seg.type != "image":
        return False
    return bool(seg.extra.get("nt")) or "multimedia.nt.qq.com.cn" in seg.url


def _drop_legacy_image_siblings(segments: list[Segment]) -> list[Segment]:
    """NT 图片会附带一份给老客户端看的 CustomFace/NotOnlineImage 兄弟元素。

    QQ 只显示一张，这里同样只留 NT 那张：同 MD5 的旧元素、以及压根没有可用
    地址的旧元素都丢掉。
    """
    nt_md5 = {seg.md5.lower() for seg in segments if _is_nt_image(seg) and seg.md5}
    kept: list[Segment] = []
    for seg in segments:
        if seg.type != "image" or _is_nt_image(seg):
            kept.append(seg)
            continue
        if not seg.url:
            continue
        if seg.md5 and seg.md5.lower() in nt_md5:
            continue
        kept.append(seg)
    return kept


def _convert_elems(raw_elems: list[bytes], *, is_group: bool) -> list[Segment]:
    segments: list[Segment] = []
    saw_reply = False
    drop_next_blank_text = False

    for raw in raw_elems:
        fields = decode_fields(raw)

        if ELEM_SRC_MSG in fields:
            saw_reply = True
            reply = _reply_segment(as_bytes(fields[ELEM_SRC_MSG][0]), is_group=is_group)
            if reply is not None:
                segments.append(reply)
            continue

        if ELEM_TEXT in fields:
            seg, is_reply_mention = _text_segment(
                as_bytes(fields[ELEM_TEXT][0]), saw_reply=saw_reply
            )
            if is_reply_mention:
                drop_next_blank_text = True
                continue
            if drop_next_blank_text:
                drop_next_blank_text = False
                if seg is None or (seg.type == "text" and not seg.text.strip()):
                    continue
            if seg is not None:
                segments.append(seg)
            continue

        seg = _elem_to_segment(raw, is_group=is_group)
        if seg is not None:
            segments.append(seg)

    return _drop_legacy_image_siblings(segments)


def parse_elems(rich_text_body: bytes, *, is_group: bool = False) -> list[Segment]:
    fields = decode_fields(rich_text_body)
    raw_elems = [as_bytes(v) for v in fields.get(RICHTEXT_ELEMS, [])]
    return _convert_elems(raw_elems, is_group=is_group)


def _parse_c2c_file(msg_content: bytes) -> Segment | None:
    file_extra = decode_fields(msg_content)
    file_data = _field_bytes(file_extra, 1)
    if not file_data:
        return None
    file_info = decode_fields(file_data)
    file_id = _field_str(file_info, 3)
    if not file_id:
        return None
    return Segment(
        type="file",
        name=_field_str(file_info, 5),
        extra={
            "size": _field_int(file_info, 6),
            "file_id": file_id,
            "file_hash": _field_str(file_info, 57),
        },
    )


def parse_message_body(body: bytes, *, is_group: bool) -> list[Segment]:
    fields = decode_fields(body)
    segments: list[Segment] = []
    rich_text = _field_bytes(fields, 1)
    if rich_text:
        segments.extend(parse_elems(rich_text, is_group=is_group))
    msg_content = _field_bytes(fields, 2)
    if msg_content:
        file_segment = _parse_c2c_file(msg_content)
        if file_segment is not None:
            segments.append(file_segment)
    return segments
