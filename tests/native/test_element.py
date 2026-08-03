from src.native.proto.element import (
    FileElem,
    ImageElem,
    parse_custom_face,
    parse_group_file_elem,
    parse_not_online_image,
    parse_text_elem,
)


def _uvarint(value: int) -> bytes:
    out = bytearray()
    while True:
        b = value & 0x7F
        value >>= 7
        out.append(b | (0x80 if value else 0))
        if not value:
            return bytes(out)


def _len_field(field_no: int, payload: bytes) -> bytes:
    return _uvarint((field_no << 3) | 2) + _uvarint(len(payload)) + payload


def _varint_field(field_no: int, value: int) -> bytes:
    return _uvarint((field_no << 3) | 0) + _uvarint(value)


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


def test_parse_custom_face_extracts_urls():
    body = (
        _len_field(16, b"http://orig")
        + _len_field(14, b"http://thumb")
        + _len_field(13, b"\xab\xcd")
        + _varint_field(25, 4096)
    )
    img = parse_custom_face(body)
    assert isinstance(img, ImageElem)
    assert img.orig_url == "http://orig"
    assert img.thumb_url == "http://thumb"
    assert img.md5 == "abcd"
    assert img.file_size == 4096


def test_parse_group_file_elem():
    body = (
        _len_field(1, "期末复习提纲.docx".encode())
        + _varint_field(2, 20480)
        + _len_field(3, b"file-id-123")
        + _len_field(5, b"file-key-abc")
    )
    fe = parse_group_file_elem(body)
    assert isinstance(fe, FileElem)
    assert fe.name == "期末复习提纲.docx"
    assert fe.size == 20480
    assert fe.file_id == "file-id-123"
    assert fe.file_key == "file-key-abc"
