from dataclasses import dataclass

from src.native.proto.wire import WireValue, as_bytes, as_int, as_str, decode_fields


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


def _first_str(fields: dict[int, list[WireValue]], field_no: int) -> str:
    vals = fields.get(field_no)
    return as_str(vals[0]) if vals else ""


def _first_int(fields: dict[int, list[WireValue]], field_no: int) -> int:
    vals = fields.get(field_no)
    return as_int(vals[0]) if vals else 0


def _first_hex(fields: dict[int, list[WireValue]], field_no: int) -> str:
    vals = fields.get(field_no)
    return as_bytes(vals[0]).hex() if vals else ""


def parse_text_elem(body: bytes) -> str:
    return _first_str(decode_fields(body), 1)


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
