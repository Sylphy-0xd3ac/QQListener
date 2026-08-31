from src.native.proto.message import (
    ELEM_CUSTOM_FACE,
    ELEM_GROUP_FILE,
    ELEM_NOT_ONLINE_IMAGE,
    ELEM_TEXT,
    RICHTEXT_ELEMS,
    parse_elems,
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


def test_parse_elems_text_then_image():
    text_elem = _len_field(ELEM_TEXT, _len_field(1, b"hi"))
    img_inner = _len_field(15, b"http://orig")
    image_elem = _len_field(ELEM_NOT_ONLINE_IMAGE, img_inner)
    rich = _len_field(RICHTEXT_ELEMS, text_elem) + _len_field(RICHTEXT_ELEMS, image_elem)
    segs = parse_elems(rich)
    assert [s.type for s in segs] == ["text", "image"]
    assert segs[0].text == "hi"
    assert segs[1].url == "http://orig"


def test_parse_elems_custom_face_image():
    cf_inner = _len_field(16, b"http://cf-orig")
    image_elem = _len_field(ELEM_CUSTOM_FACE, cf_inner)
    rich = _len_field(RICHTEXT_ELEMS, image_elem)
    segs = parse_elems(rich)
    assert [s.type for s in segs] == ["image"]
    assert segs[0].url == "http://cf-orig"


def test_parse_elems_group_file():
    gf_inner = _len_field(1, "作业.docx".encode()) + _varint_field(2, 2048) + _len_field(3, b"fid")
    file_elem = _len_field(ELEM_GROUP_FILE, gf_inner)
    rich = _len_field(RICHTEXT_ELEMS, file_elem)
    segs = parse_elems(rich)
    assert [s.type for s in segs] == ["file"]
    assert segs[0].name == "作业.docx"
    assert segs[0].extra["size"] == 2048
    assert segs[0].extra["file_id"] == "fid"
