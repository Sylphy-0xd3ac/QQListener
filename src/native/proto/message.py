from urllib.parse import quote

from src.native.model import Segment
from src.native.proto.element import (
    ImageElem,
    parse_custom_face,
    parse_group_file_elem,
    parse_not_online_image,
    parse_text_elem,
)
from src.native.proto.wire import as_bytes, as_int, as_str, decode_fields

# Elem 子字段号（来自 SnowLuma proto-defs element.ts 的 Elem 接口，权威）
ELEM_TEXT = 1
ELEM_NOT_ONLINE_IMAGE = 4
ELEM_TRANS = 5
ELEM_CUSTOM_FACE = 8  # NT QQ 群图片多走此元素
ELEM_GROUP_FILE = 13
ELEM_COMMON = 53

# RichText.elems 的字段号（message.ts 待真帧核对）
RICHTEXT_ELEMS = 2


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
    return Segment(
        type="image",
        url=img.orig_url or img.big_url or img.thumb_url,
        md5=img.md5,
        name=img.name,
    )


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


def _elem_to_segment(elem_body: bytes, *, is_group: bool) -> Segment | None:
    f = decode_fields(elem_body)
    if ELEM_TEXT in f:
        return Segment(type="text", text=parse_text_elem(as_bytes(f[ELEM_TEXT][0])))
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
    if ELEM_COMMON in f:
        return _parse_nt_common_elem(as_bytes(f[ELEM_COMMON][0]), is_group=is_group)
    return None


def parse_elems(rich_text_body: bytes, *, is_group: bool = False) -> list[Segment]:
    fields = decode_fields(rich_text_body)
    segments: list[Segment] = []
    for elem in fields.get(RICHTEXT_ELEMS, []):
        seg = _elem_to_segment(as_bytes(elem), is_group=is_group)
        if seg is not None:
            segments.append(seg)
    return segments


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
