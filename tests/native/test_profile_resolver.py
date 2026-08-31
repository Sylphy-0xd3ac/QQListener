from src.native.profile_resolver import (
    build_user_profile_request,
    parse_user_profile_response,
)
from src.native.proto.wire import (
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)


def _first(fields, field_no):
    return fields[field_no][0]


def test_profile_request_uses_uin_form_and_requests_remark_and_nickname():
    envelope = decode_fields(build_user_profile_request("123456"))

    assert as_int(_first(envelope, 1)) == 0xFE1
    assert as_int(_first(envelope, 2)) == 2
    assert as_int(_first(envelope, 12)) == 1
    body = decode_fields(as_bytes(_first(envelope, 4)))
    assert as_int(_first(body, 1)) == 123456
    keys = {as_int(_first(decode_fields(as_bytes(item)), 1)) for item in body[3]}
    assert keys == {103, 20002}


def test_profile_request_uses_uid_form_when_uin_is_unavailable():
    envelope = decode_fields(build_user_profile_request("u_example"))
    body = decode_fields(as_bytes(_first(envelope, 4)))

    assert as_int(_first(envelope, 12)) == 0
    assert as_str(_first(body, 1)) == "u_example"


def test_profile_response_extracts_remark_and_nickname():
    nickname = encode_varint_field(1, 20002) + encode_bytes_field(2, "基础昵称")
    remark = encode_varint_field(1, 103) + encode_bytes_field(2, "好友备注")
    properties = encode_bytes_field(2, nickname) + encode_bytes_field(2, remark)
    response_body = encode_bytes_field(2, properties)
    response = encode_bytes_field(1, response_body)
    envelope = encode_varint_field(3, 0) + encode_bytes_field(4, response)

    names = parse_user_profile_response(envelope)

    assert names.nickname == "基础昵称"
    assert names.remark == "好友备注"
