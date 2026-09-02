"""图片/引用/@ 的解码规则（对照 SnowLuma proto-defs 与真帧结构）。"""

from src.native.proto.element import image_url_from_md5, make_image_url
from src.native.proto.message import (
    ELEM_COMMON,
    ELEM_CUSTOM_FACE,
    ELEM_SRC_MSG,
    ELEM_TEXT,
    RICHTEXT_ELEMS,
    append_rkey,
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


def _rich(*elems: bytes) -> bytes:
    return b"".join(_len_field(RICHTEXT_ELEMS, elem) for elem in elems)


def _nt_image_elem(
    *,
    url_path: str = "/download?appid=1407&fileid=FID",
    domain: str = "multimedia.nt.qq.com.cn",
    original_parameter: str = "&spec=0",
    md5: str = "ea0cd2ae3bf8596f4d75481bd5b544b8",
    rkey: str = "",
    file_uuid: str = "FID",
) -> bytes:
    """CommonElem(service=48, business=10) —— NTQQ 现行图片元素。"""
    file_info = (
        _varint_field(1, 2352855)
        + _len_field(2, md5.encode())
        + _len_field(3, b"sha1")
        + _len_field(4, b"IMG.png")
        + _varint_field(6, 2264)
        + _varint_field(7, 1834)
    )
    index = _len_field(1, file_info) + _len_field(2, file_uuid.encode())
    picture = (
        _len_field(1, url_path.encode())
        + _len_field(2, _len_field(1, original_parameter.encode()))
        + _len_field(3, domain.encode())
    )
    info_body = _len_field(1, index) + _len_field(2, picture)

    msg_info = _len_field(1, info_body)
    if rkey:
        pic_reserve = _len_field(30, rkey.encode())
        ext_biz = _len_field(1, _len_field(11, pic_reserve))
        msg_info += _len_field(2, ext_biz)

    common = _varint_field(1, 48) + _len_field(2, msg_info) + _varint_field(3, 10)
    return _len_field(ELEM_COMMON, common)


def _text_elem(text: str, *, pb_reserve: bytes = b"", attr6: bytes = b"") -> bytes:
    inner = _len_field(1, text.encode())
    if attr6:
        inner += _len_field(3, attr6)
    if pb_reserve:
        inner += _len_field(12, pb_reserve)
    return _len_field(ELEM_TEXT, inner)


def _mention_extra(*, mention_type: int, uin: int = 0, uid: str = "") -> bytes:
    out = _varint_field(3, mention_type) + _varint_field(4, uin)
    if uid:
        out += _len_field(9, uid.encode())
    return out


def _src_msg_elem(*, seq: int, sender_uin: int, elems: list[bytes], time: int = 0) -> bytes:
    src = _varint_field(1, seq) + _varint_field(2, sender_uin)
    if time:
        src += _varint_field(3, time)
    for elem in elems:
        src += _len_field(5, elem)
    return _len_field(ELEM_SRC_MSG, src)


# ---------- 图片地址 ----------


def test_nt_image_url_carries_embedded_rkey():
    """缺 rkey 时 CDN 会以 invalid rkey 拒绝，图片就"炸"成占位符了。"""
    segs = parse_elems(_rich(_nt_image_elem(rkey="&rkey=CAESQabc")), is_group=True)

    assert [s.type for s in segs] == ["image"]
    assert segs[0].url == (
        "https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=FID&spec=0&rkey=CAESQabc"
    )


def test_nt_image_url_without_rkey_is_left_bare_for_later_signing():
    segs = parse_elems(_rich(_nt_image_elem()), is_group=True)

    assert segs[0].url.endswith("&spec=0")
    assert "rkey=" not in segs[0].url


def test_append_rkey_strips_prefix_and_picks_separator():
    assert append_rkey("https://x/download?a=1", "&rkey=K") == "https://x/download?a=1&rkey=K"
    assert append_rkey("https://x/download", "?rkey=K") == "https://x/download?rkey=K"
    assert append_rkey("https://x/download?rkey=OLD", "K") == "https://x/download?rkey=OLD"
    assert append_rkey("", "K") == ""


def test_relative_legacy_image_path_gets_a_host():
    assert (
        make_image_url("/gchatpic_new/0/0-0-AB/0") == "http://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0"
    )
    assert (
        make_image_url("/download?fileid=x") == "https://multimedia.nt.qq.com.cn/download?fileid=x"
    )
    assert make_image_url("https://already/absolute") == "https://already/absolute"
    assert make_image_url("?spec=0") == ""
    assert make_image_url("") == ""


def test_custom_face_relative_url_is_completed():
    custom_face = _len_field(ELEM_CUSTOM_FACE, _len_field(16, b"/gchatpic_new/0/0-0-AB/0"))
    segs = parse_elems(_rich(custom_face), is_group=True)

    assert segs[0].url == "http://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0"


def test_image_without_any_url_falls_back_to_md5_address():
    md5 = "AB" * 16
    custom_face = _len_field(ELEM_CUSTOM_FACE, _len_field(13, bytes.fromhex(md5)))
    segs = parse_elems(_rich(custom_face), is_group=True)

    assert segs[0].url == image_url_from_md5(md5)
    assert segs[0].url.endswith("/gchatpic_new/0/0-0-ABABABABABABABABABABABABABABABAB/0")


def test_legacy_sibling_of_the_same_nt_image_is_dropped():
    """NT 图片会附带一份给老客户端的 CustomFace；QQ 只显示一张。"""
    md5 = "ea0cd2ae3bf8596f4d75481bd5b544b8"
    sibling = _len_field(
        ELEM_CUSTOM_FACE,
        _len_field(16, b"/gchatpic_new/0/0-0-X/0") + _len_field(13, bytes.fromhex(md5)),
    )
    segs = parse_elems(_rich(_nt_image_elem(md5=md5, rkey="&rkey=K"), sibling), is_group=True)

    assert [s.type for s in segs] == ["image"]
    assert "multimedia.nt.qq.com.cn" in segs[0].url


def test_unrelated_legacy_image_survives():
    other = _len_field(
        ELEM_CUSTOM_FACE,
        _len_field(16, b"/gchatpic_new/0/0-0-Y/0") + _len_field(13, bytes.fromhex("BB" * 16)),
    )
    segs = parse_elems(_rich(_nt_image_elem(rkey="&rkey=K"), other), is_group=True)

    assert len(segs) == 2


# ---------- @ ----------


def test_mention_is_decoded_from_pb_reserve():
    elem = _text_elem("@李四 ", pb_reserve=_mention_extra(mention_type=1, uin=1002))
    segs = parse_elems(_rich(elem), is_group=True)

    assert [s.type for s in segs] == ["at"]
    assert segs[0].target_id == "1002"
    assert segs[0].text == "@李四 "


def test_mention_all_is_flagged_as_all():
    elem = _text_elem("@全体成员", pb_reserve=_mention_extra(mention_type=1, uin=0))
    segs = parse_elems(_rich(elem), is_group=True)

    assert segs[0].target_id == "all"


def test_mention_from_attr6_buffer():
    attr6 = bytes(7) + (1234).to_bytes(4, "big") + bytes(2)
    segs = parse_elems(_rich(_text_elem("@王五 ", attr6=attr6)), is_group=True)

    assert segs[0].type == "at" and segs[0].target_id == "1234"


# ---------- 引用 ----------


def test_reply_carries_the_quoted_message_elements():
    quoted_text = _text_elem("昨天的作业交了吗")
    quoted_image = _nt_image_elem(rkey="&rkey=K", file_uuid="QUOTED")
    reply = _src_msg_elem(
        seq=42, sender_uin=1002, time=1700000000, elems=[quoted_text, quoted_image]
    )
    segs = parse_elems(_rich(reply, _text_elem("交了")), is_group=True)

    assert [s.type for s in segs] == ["reply", "text"]
    quoted = segs[0].extra["segments"]
    assert segs[0].extra["seq"] == 42
    assert segs[0].extra["sender_id"] == "1002"
    assert segs[0].extra["time"] == 1700000000
    assert [s.type for s in quoted] == ["text", "image"]
    assert quoted[0].text == "昨天的作业交了吗"
    assert "rkey=K" in quoted[1].url
    assert segs[1].text == "交了"


def test_reply_structural_auto_mention_and_blank_separator_are_dropped():
    """QQ NT 在 srcMsg 后塞一个 type=2/uin=0 的结构性 @ 和一个空白文本。"""
    reply = _src_msg_elem(seq=42, sender_uin=1002, elems=[_text_elem("原文")])
    auto_mention = _text_elem("@李四 ", pb_reserve=_mention_extra(mention_type=2, uin=0, uid="u_a"))
    blank = _text_elem(" ")
    segs = parse_elems(_rich(reply, auto_mention, blank, _text_elem("交了")), is_group=True)

    assert [s.type for s in segs] == ["reply", "text"]
    assert segs[1].text == "交了"


def test_real_user_mention_after_a_reply_is_kept():
    reply = _src_msg_elem(seq=42, sender_uin=1002, elems=[_text_elem("原文")])
    real_at = _text_elem("@张三 ", pb_reserve=_mention_extra(mention_type=1, uin=1001))
    segs = parse_elems(_rich(reply, real_at, _text_elem("看看")), is_group=True)

    assert [s.type for s in segs] == ["reply", "at", "text"]
    assert segs[1].target_id == "1001"
