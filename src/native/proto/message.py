from src.native.model import Segment
from src.native.proto.element import parse_not_online_image, parse_text_elem
from src.native.proto.wire import as_bytes, decode_fields

# Elem 子字段号（照抄 SnowLuma message.ts；Phase 0 真帧待核）
ELEM_TEXT = 1
ELEM_NOT_ONLINE_IMAGE = 2

# RichText.elems 的字段号（message.ts 待核；须与解析一致）
RICHTEXT_ELEMS = 2


def _elem_to_segment(elem_body: bytes) -> Segment | None:
    f = decode_fields(elem_body)
    if ELEM_TEXT in f:
        return Segment(type="text", text=parse_text_elem(as_bytes(f[ELEM_TEXT][0])))
    if ELEM_NOT_ONLINE_IMAGE in f:
        img = parse_not_online_image(as_bytes(f[ELEM_NOT_ONLINE_IMAGE][0]))
        return Segment(
            type="image",
            url=img.orig_url or img.big_url or img.thumb_url,
            md5=img.md5,
            name=img.name,
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
