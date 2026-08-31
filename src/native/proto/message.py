from src.native.model import Segment
from src.native.proto.element import (
    ImageElem,
    parse_custom_face,
    parse_group_file_elem,
    parse_not_online_image,
    parse_text_elem,
)
from src.native.proto.wire import as_bytes, decode_fields

# Elem 子字段号（来自 SnowLuma proto-defs element.ts 的 Elem 接口，权威）
ELEM_TEXT = 1
ELEM_NOT_ONLINE_IMAGE = 4
ELEM_CUSTOM_FACE = 8  # NT QQ 群图片多走此元素
ELEM_GROUP_FILE = 13

# RichText.elems 的字段号（message.ts 待真帧核对）
RICHTEXT_ELEMS = 2


def _image_segment(img: ImageElem) -> Segment:
    return Segment(
        type="image",
        url=img.orig_url or img.big_url or img.thumb_url,
        md5=img.md5,
        name=img.name,
    )


def _elem_to_segment(elem_body: bytes) -> Segment | None:
    f = decode_fields(elem_body)
    if ELEM_TEXT in f:
        return Segment(type="text", text=parse_text_elem(as_bytes(f[ELEM_TEXT][0])))
    if ELEM_NOT_ONLINE_IMAGE in f:
        return _image_segment(parse_not_online_image(as_bytes(f[ELEM_NOT_ONLINE_IMAGE][0])))
    if ELEM_CUSTOM_FACE in f:
        return _image_segment(parse_custom_face(as_bytes(f[ELEM_CUSTOM_FACE][0])))
    if ELEM_GROUP_FILE in f:
        fe = parse_group_file_elem(as_bytes(f[ELEM_GROUP_FILE][0]))
        return Segment(
            type="file",
            name=fe.name,
            url=fe.url,
            md5=fe.md5,
            extra={"size": fe.size, "file_id": fe.file_id, "file_key": fe.file_key},
        )
    return None


def parse_elems(rich_text_body: bytes) -> list[Segment]:
    fields = decode_fields(rich_text_body)
    segments: list[Segment] = []
    for elem in fields.get(RICHTEXT_ELEMS, []):
        seg = _elem_to_segment(as_bytes(elem))
        if seg is not None:
            segments.append(seg)
    return segments
