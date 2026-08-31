import pytest

from src.native.file_resolver import (
    build_group_file_url_request,
    build_private_file_url_request,
    parse_group_file_url_response,
    parse_private_file_url_response,
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


def test_group_file_url_request_and_response():
    request = decode_fields(build_group_file_url_request(123456, "file-id", 102))
    assert as_int(_first(request, 1)) == 0x6D6
    assert as_int(_first(request, 2)) == 2
    assert as_int(_first(request, 12)) == 1
    body = decode_fields(as_bytes(_first(request, 4)))
    download = decode_fields(as_bytes(_first(body, 3)))
    assert as_int(_first(download, 1)) == 123456
    assert as_int(_first(download, 2)) == 7
    assert as_int(_first(download, 3)) == 102
    assert as_str(_first(download, 4)) == "file-id"

    download_response = (
        encode_varint_field(1, 0)
        + encode_bytes_field(5, "file.qq.com")
        + encode_bytes_field(6, b"\x01\xab")
    )
    response_body = encode_bytes_field(3, download_response)
    envelope = encode_varint_field(3, 0) + encode_bytes_field(4, response_body)
    assert parse_group_file_url_response(envelope, "file id") == (
        "https://file.qq.com/ftn_handler/01AB/?fname=file%20id"
    )


def test_private_file_url_request_and_response():
    request = decode_fields(build_private_file_url_request("uid-self", "uuid", "hash"))
    assert as_int(_first(request, 1)) == 0xE37
    assert as_int(_first(request, 2)) == 1200
    body = decode_fields(as_bytes(_first(request, 4)))
    inner = decode_fields(as_bytes(_first(body, 14)))
    assert as_str(_first(inner, 10)) == "uid-self"
    assert as_str(_first(inner, 20)) == "uuid"
    assert as_str(_first(inner, 60)) == "hash"

    result = (
        encode_bytes_field(20, "127.0.0.1")
        + encode_varint_field(40, 8080)
        + encode_bytes_field(50, "/download?id=1")
    )
    response = encode_bytes_field(14, encode_bytes_field(30, result))
    envelope = encode_varint_field(3, 0) + encode_bytes_field(4, response)
    assert parse_private_file_url_response(envelope) == (
        "http://127.0.0.1:8080/download?id=1&isthumb=0"
    )


def test_file_url_responses_reject_unsafe_hosts():
    group_download = encode_bytes_field(5, "host/path") + encode_bytes_field(6, b"x")
    group_env = encode_bytes_field(4, encode_bytes_field(3, group_download))
    with pytest.raises(ValueError):
        parse_group_file_url_response(group_env, "id")

    private_result = (
        encode_bytes_field(20, "host@evil")
        + encode_varint_field(40, 80)
        + encode_bytes_field(50, "/x")
    )
    private_env = encode_bytes_field(
        4, encode_bytes_field(14, encode_bytes_field(30, private_result))
    )
    with pytest.raises(ValueError):
        parse_private_file_url_response(private_env)
