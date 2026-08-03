from src.native.proto.element import ImageElem, parse_not_online_image, parse_text_elem


def _len_field(field_no: int, payload: bytes) -> bytes:
    tag = (field_no << 3) | 2
    return bytes([tag, len(payload)]) + payload


def _varint_field(field_no: int, value: int) -> bytes:
    out = bytearray([(field_no << 3) | 0])
    while True:
        b = value & 0x7F
        value >>= 7
        out.append(b | (0x80 if value else 0))
        if not value:
            return bytes(out)


def test_parse_text_elem():
    body = _len_field(1, "明天带这个".encode())
    assert parse_text_elem(body) == "明天带这个"


def test_parse_not_online_image_extracts_urls():
    body = (
        _len_field(15, b"http://orig/u")
        + _len_field(12, b"http://thumb/u")
        + _len_field(7, b"\xde\xad\xbe\xef")
        + _varint_field(2, 2048)
    )
    img = parse_not_online_image(body)
    assert isinstance(img, ImageElem)
    assert img.orig_url == "http://orig/u"
    assert img.thumb_url == "http://thumb/u"
    assert img.md5 == "deadbeef"
    assert img.file_size == 2048


def test_parse_not_online_image_missing_fields_default_empty():
    img = parse_not_online_image(b"")
    assert img.orig_url == "" and img.thumb_url == "" and img.md5 == "" and img.file_size == 0
