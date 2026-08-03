from src.utils.media import (
    file_icon_for_path,
    is_http_url,
    local_path_from_ref,
    safe_filename,
)


def test_is_http_url():
    assert is_http_url("http://a/b") is True
    assert is_http_url("https://a/b") is True
    assert is_http_url("ftp://a") is False
    assert is_http_url(None) is False


def test_file_icon_for_path_known_and_default():
    assert file_icon_for_path("x.docx") == "asset/FileIcon/word.png"
    assert file_icon_for_path("x.PDF") == "asset/FileIcon/pdf.png"
    assert file_icon_for_path("x.zip") == "asset/FileIcon/enb.png"


def test_safe_filename_sanitizes():
    assert safe_filename("http://h/a/期末.docx", "fallback.bin") == "期末.docx"
    assert safe_filename('bad<>:"name.txt', "fallback.bin") == "bad____name.txt"
    assert safe_filename("", "fallback.bin") == "fallback.bin"
    assert safe_filename(None, "fallback.bin") == "fallback.bin"


def test_local_path_from_ref_nonexistent_returns_none():
    assert local_path_from_ref("/no/such/file.png") is None
    assert local_path_from_ref(None) is None


def test_local_path_from_ref_existing(tmp_path):
    f = tmp_path / "a.png"
    f.write_bytes(b"x")
    assert local_path_from_ref(str(f)) == str(f)
