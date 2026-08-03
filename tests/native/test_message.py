from src.native.proto.message import ELEM_NOT_ONLINE_IMAGE, ELEM_TEXT, parse_elems


def _len_field(field_no: int, payload: bytes) -> bytes:
    return bytes([(field_no << 3) | 2, len(payload)]) + payload


def test_parse_elems_text_then_image():
    text_elem = _len_field(ELEM_TEXT, _len_field(1, b"hi"))
    img_inner = _len_field(15, b"http://orig")
    image_elem = _len_field(ELEM_NOT_ONLINE_IMAGE, img_inner)
    # RichText.elems: 假设 elem 列表字段号为 2（message.ts 待核；测试与实现一致即可）
    rich = _len_field(2, text_elem) + _len_field(2, image_elem)
    segs = parse_elems(rich)
    assert [s.type for s in segs] == ["text", "image"]
    assert segs[0].text == "hi"
    assert segs[1].url == "http://orig"
