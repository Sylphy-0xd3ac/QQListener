import re
from dataclasses import dataclass, field

from src.native.proto.wire import WireValue, as_bytes, as_int, as_str, decode_fields

# QQ 图片元素里的 origUrl/bigUrl 常是相对路径，需要补主机名才可下载。
# 取值规则对齐 SnowLuma packages/protocol/src/msg-push/helpers.ts::makeImageUrl。
NT_MEDIA_HOST = "https://multimedia.nt.qq.com.cn"
LEGACY_IMAGE_HOST = "http://gchat.qpic.cn"

_MD5_HEX_RE = re.compile(r"^[0-9a-fA-F]{32}$")

# TextElem.pbReserve(12) 解出的 MentionExtra：type=1 为真实 @，type=2 为
# 回复消息自带的结构性 @（uin=0），后者不是用户内容，需要丢掉。
MENTION_TYPE_USER = 1
MENTION_TYPE_REPLY = 2


@dataclass
class ImageElem:
    orig_url: str = ""
    big_url: str = ""
    thumb_url: str = ""
    md5: str = ""
    file_size: int = 0
    name: str = ""


@dataclass
class FileElem:
    name: str = ""
    url: str = ""
    md5: str = ""
    size: int = 0
    file_id: str = ""
    file_key: str = ""


@dataclass
class MentionInfo:
    type: int = 0
    uin: int = 0
    uid: str = ""


@dataclass
class SrcMsgElem:
    """SrcMsg（Elem 字段 45）：被引用的那条消息。"""

    orig_seq: int = 0
    sender_uin: int = 0
    time: int = 0
    troop_name: str = ""
    elems: list[bytes] = field(default_factory=list)


def _first_str(fields: dict[int, list[WireValue]], field_no: int) -> str:
    vals = fields.get(field_no)
    return as_str(vals[0]) if vals else ""


def _first_int(fields: dict[int, list[WireValue]], field_no: int) -> int:
    vals = fields.get(field_no)
    return as_int(vals[0]) if vals else 0


def _first_bytes(fields: dict[int, list[WireValue]], field_no: int) -> bytes:
    vals = fields.get(field_no)
    return as_bytes(vals[0]) if vals else b""


def _first_hex(fields: dict[int, list[WireValue]], field_no: int) -> str:
    vals = fields.get(field_no)
    return as_bytes(vals[0]).hex() if vals else ""


def make_image_url(orig_url: str) -> str:
    """把图片元素里的路径补成可下载 URL；补不出来时返回空串。"""
    if not orig_url:
        return ""
    if orig_url.lower().startswith(("http://", "https://")):
        return orig_url
    # 只带查询串没有路径的兼容表情，拼上主机名也是打不开的。
    if not orig_url.startswith("/"):
        return ""
    if "rkey" in orig_url or "fileid" in orig_url:
        return NT_MEDIA_HOST + orig_url
    return LEGACY_IMAGE_HOST + orig_url


def image_url_from_md5(md5_hex: str) -> str:
    """老式群图片地址：线上只给了 MD5、没给下载路径时的兜底。"""
    if not md5_hex or not _MD5_HEX_RE.match(md5_hex):
        return ""
    return f"{LEGACY_IMAGE_HOST}/gchatpic_new/0/0-0-{md5_hex.upper()}/0"


def parse_text_elem(body: bytes) -> str:
    return _first_str(decode_fields(body), 1)


def parse_mention_extra(body: bytes) -> MentionInfo:
    """TextElem.pbReserve(12) → MentionExtra{type=3, uin=4, uid=9}。"""
    f = decode_fields(body)
    return MentionInfo(type=_first_int(f, 3), uin=_first_int(f, 4), uid=_first_str(f, 9))


def parse_src_msg(body: bytes) -> SrcMsgElem:
    """SrcMsg：origSeqs=1, senderUin=2, time=3, elemsRaw=5, troopName=11。"""
    f = decode_fields(body)
    return SrcMsgElem(
        orig_seq=_first_int(f, 1),
        sender_uin=_first_int(f, 2),
        time=_first_int(f, 3),
        troop_name=_first_str(f, 11),
        elems=[as_bytes(v) for v in f.get(5, [])],
    )


def parse_not_online_image(body: bytes) -> ImageElem:
    f = decode_fields(body)
    return ImageElem(
        orig_url=_first_str(f, 15),
        big_url=_first_str(f, 14),
        thumb_url=_first_str(f, 12),
        md5=_first_hex(f, 7),
        file_size=_first_int(f, 2),
        name=_first_str(f, 1),
    )


def parse_custom_face(body: bytes) -> ImageElem:
    # NT QQ 群图片多走 CustomFace（Elem 字段 8）。字段号来自 proto-defs element.ts。
    f = decode_fields(body)
    return ImageElem(
        orig_url=_first_str(f, 16),
        big_url=_first_str(f, 15),
        thumb_url=_first_str(f, 14),
        md5=_first_hex(f, 13),
        file_size=_first_int(f, 25),
        name=_first_str(f, 2),
    )


def parse_group_file_elem(body: bytes) -> FileElem:
    # GroupFileElem（Elem 字段 13）：filename=1, fileSize=2, fileId=3, fileKey=5。
    # 群文件不在推送里带下载 URL——需用 fileId/fileKey 发 OIDB 请求换地址。
    f = decode_fields(body)
    return FileElem(
        name=_first_str(f, 1),
        size=_first_int(f, 2),
        file_id=_first_str(f, 3),
        file_key=_first_str(f, 5),
    )
