from src.native.proto.wire import (
    WIRE_LEN,
    WIRE_VARINT,
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    read_varint,
)


def test_read_varint_multibyte():
    # 300 = 0xAC 0x02
    val, pos = read_varint(b"\xac\x02", 0)
    assert val == 300 and pos == 2


def test_decode_field_varint():
    # field 2 varint 300: tag=(2<<3)|0=0x10
    fields = decode_fields(b"\x10\xac\x02")
    assert as_int(fields[2][0]) == 300
    assert fields[2][0].wire_type == WIRE_VARINT


def test_decode_field_string():
    # field 1 LEN "hi": tag=0x0A len=2
    fields = decode_fields(b"\x0a\x02hi")
    assert fields[1][0].wire_type == WIRE_LEN
    assert as_str(fields[1][0]) == "hi"
    assert as_bytes(fields[1][0]) == b"hi"


def test_decode_field_15_string():
    # field 15 LEN "u": tag=(15<<3)|2=0x7a len=1
    fields = decode_fields(b"\x7a\x01u")
    assert as_str(fields[15][0]) == "u"


def test_repeated_fields_aggregate_in_order():
    # field 1 varint 1, then field 1 varint 2
    fields = decode_fields(b"\x08\x01\x08\x02")
    assert [as_int(v) for v in fields[1]] == [1, 2]


def test_nested_message_via_as_bytes_then_decode():
    # field 3 LEN wraps {field 1 LEN "x"}
    inner = b"\x0a\x01x"
    outer = bytes([(3 << 3) | 2, len(inner)]) + inner
    fields = decode_fields(outer)
    sub = decode_fields(as_bytes(fields[3][0]))
    assert as_str(sub[1][0]) == "x"
