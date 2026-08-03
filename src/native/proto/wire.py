from dataclasses import dataclass

WIRE_VARINT = 0
WIRE_I64 = 1
WIRE_LEN = 2
WIRE_I32 = 5


@dataclass
class WireValue:
    wire_type: int
    raw: bytes


def read_varint(data: bytes, pos: int) -> tuple[int, int]:
    result = 0
    shift = 0
    while True:
        if pos >= len(data):
            raise ValueError("truncated varint")
        byte = data[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if not (byte & 0x80):
            return result, pos
        shift += 7


def decode_fields(data: bytes) -> dict[int, list[WireValue]]:
    fields: dict[int, list[WireValue]] = {}
    pos = 0
    n = len(data)
    while pos < n:
        tag, pos = read_varint(data, pos)
        field_no = tag >> 3
        wire_type = tag & 0x07
        if wire_type == WIRE_VARINT:
            value, pos = read_varint(data, pos)
            raw = value.to_bytes((value.bit_length() + 7) // 8 or 1, "little")
        elif wire_type == WIRE_I64:
            raw = data[pos : pos + 8]
            pos += 8
        elif wire_type == WIRE_LEN:
            length, pos = read_varint(data, pos)
            raw = data[pos : pos + length]
            pos += length
        elif wire_type == WIRE_I32:
            raw = data[pos : pos + 4]
            pos += 4
        else:
            raise ValueError(f"unsupported wire_type={wire_type} field={field_no}")
        fields.setdefault(field_no, []).append(WireValue(wire_type, raw))
    return fields


def as_int(v: WireValue) -> int:
    return int.from_bytes(v.raw, "little")


def as_str(v: WireValue) -> str:
    return v.raw.decode("utf-8", "replace")


def as_bytes(v: WireValue) -> bytes:
    return v.raw
