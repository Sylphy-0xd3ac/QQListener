import contextlib

from src.native.proto.wire import (
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    encode_bytes_field,
    encode_varint_field,
)
from src.native.rkey_resolver import (
    RKEY_TYPE_FALLBACK_IMAGE,
    RKEY_TYPE_GROUP_IMAGE,
    RKEY_TYPE_PRIVATE_IMAGE,
    RKeyCache,
    RKeyInfo,
    build_download_rkey_request,
    parse_download_rkey_response,
    rkey_type_for_url,
    url_needs_rkey,
)


def _first(fields, field_no):
    return fields[field_no][0]


def test_request_is_oidb_0x9067_202_asking_for_image_rkeys():
    envelope = decode_fields(build_download_rkey_request())

    assert as_int(_first(envelope, 1)) == 0x9067
    assert as_int(_first(envelope, 2)) == 202
    assert as_int(_first(envelope, 12)) == 1

    body = decode_fields(as_bytes(_first(envelope, 4)))
    req_head = decode_fields(as_bytes(_first(body, 1)))
    common = decode_fields(as_bytes(_first(req_head, 1)))
    assert as_int(_first(common, 2)) == 202
    scene = decode_fields(as_bytes(_first(req_head, 2)))
    assert as_int(_first(scene, 101)) == 2
    assert as_int(_first(scene, 102)) == 1

    types = {as_int(v) for v in decode_fields(as_bytes(_first(body, 4)))[1]}
    assert {RKEY_TYPE_PRIVATE_IMAGE, RKEY_TYPE_GROUP_IMAGE, RKEY_TYPE_FALLBACK_IMAGE} <= types


def _rkey_entry(rkey: str, rkey_type: int, ttl: int = 3600, created: int = 0) -> bytes:
    return encode_bytes_field(
        1,
        encode_bytes_field(1, rkey)
        + encode_varint_field(2, ttl)
        + encode_varint_field(4, created)
        + encode_varint_field(5, rkey_type),
    )


def test_response_yields_rkeys_by_type():
    download = _rkey_entry("&rkey=GROUP", RKEY_TYPE_GROUP_IMAGE) + _rkey_entry(
        "&rkey=PRIVATE", RKEY_TYPE_PRIVATE_IMAGE
    )
    response = encode_bytes_field(4, download)
    envelope = encode_varint_field(3, 0) + encode_bytes_field(4, response)

    rkeys = parse_download_rkey_response(envelope)

    assert {info.type: info.rkey for info in rkeys} == {
        RKEY_TYPE_GROUP_IMAGE: "&rkey=GROUP",
        RKEY_TYPE_PRIVATE_IMAGE: "&rkey=PRIVATE",
    }


def test_response_error_code_raises():
    envelope = encode_varint_field(3, 42)
    try:
        parse_download_rkey_response(envelope)
    except RuntimeError as exc:
        assert "42" in str(exc)
    else:
        raise AssertionError("expected RuntimeError")


def test_response_head_ret_code_raises_with_message():
    head = encode_varint_field(2, 7) + encode_bytes_field(3, "凭证过期")
    response = encode_bytes_field(1, head)
    envelope = encode_varint_field(3, 0) + encode_bytes_field(4, response)

    try:
        parse_download_rkey_response(envelope)
    except RuntimeError as exc:
        assert "凭证过期" in str(exc)
    else:
        raise AssertionError("expected RuntimeError")


def test_rkey_scene_follows_the_appid_in_the_url_not_the_conversation():
    private = "https://multimedia.nt.qq.com.cn/download?appid=1406&fileid=x"
    group = "https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=x"

    assert rkey_type_for_url(private, is_group=True) == RKEY_TYPE_PRIVATE_IMAGE
    assert rkey_type_for_url(group, is_group=False) == RKEY_TYPE_GROUP_IMAGE
    assert rkey_type_for_url("https://x/y", is_group=True) == RKEY_TYPE_GROUP_IMAGE


def test_url_needs_rkey_only_for_unsigned_nt_addresses():
    assert url_needs_rkey("https://multimedia.nt.qq.com.cn/download?appid=1407")
    assert not url_needs_rkey("https://multimedia.nt.qq.com.cn/download?rkey=K")
    assert not url_needs_rkey("http://gchat.qpic.cn/gchatpic_new/0/0-0-AB/0")
    assert not url_needs_rkey("")


def test_cache_serves_from_memory_and_only_refetches_after_expiry():
    calls = []

    def fetch(pid):
        calls.append(pid)
        return [RKeyInfo(rkey="&rkey=G", type=RKEY_TYPE_GROUP_IMAGE, ttl_seconds=3600)]

    cache = RKeyCache(fetch=fetch)
    assert cache.get(7, RKEY_TYPE_GROUP_IMAGE) == "&rkey=G"
    assert cache.get(7, RKEY_TYPE_GROUP_IMAGE) == "&rkey=G"
    assert calls == [7]


def test_cache_falls_back_to_the_generic_image_rkey():
    def fetch(pid):
        return [RKeyInfo(rkey="&rkey=F", type=RKEY_TYPE_FALLBACK_IMAGE, ttl_seconds=3600)]

    cache = RKeyCache(fetch=fetch)
    assert cache.get(7, RKEY_TYPE_GROUP_IMAGE) == "&rkey=F"


def test_cache_backs_off_when_the_fetch_keeps_failing():
    calls = []

    def fetch(pid):
        calls.append(pid)
        raise RuntimeError("管道不可用")

    cache = RKeyCache(fetch=fetch)
    for _ in range(3):
        with contextlib.suppress(RuntimeError):
            cache.get(7, RKEY_TYPE_GROUP_IMAGE)
    assert len(calls) == 1  # 冷却期内不再重试


def test_expired_entry_is_not_served():
    cache = RKeyCache(fetch=lambda pid: [])
    cache._store([RKeyInfo(rkey="&rkey=OLD", type=RKEY_TYPE_GROUP_IMAGE, ttl_seconds=1)], 0.0)

    assert cache._lookup(RKEY_TYPE_GROUP_IMAGE, 10_000.0) == ""


def test_str_helper_is_used_for_rkey_values():
    entry = decode_fields(_rkey_entry("&rkey=X", RKEY_TYPE_GROUP_IMAGE))
    inner = decode_fields(as_bytes(entry[1][0]))
    assert as_str(inner[1][0]) == "&rkey=X"
