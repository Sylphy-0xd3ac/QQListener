from src.ui.notify_window import attachment_qurl


def test_attachment_qurl_accepts_http_and_https():
    assert attachment_qurl("https://file.qq.com/a").toString() == "https://file.qq.com/a"
    assert attachment_qurl("http://127.0.0.1:80/a").toString() == "http://127.0.0.1:80/a"


def test_attachment_qurl_accepts_existing_local_file(tmp_path):
    path = tmp_path / "资料.pdf"
    path.write_bytes(b"pdf")
    url = attachment_qurl(str(path))
    assert url is not None and url.isLocalFile()
    assert url.toLocalFile() == str(path)


def test_attachment_qurl_rejects_unknown_scheme_and_missing_path():
    assert attachment_qurl("javascript:alert(1)") is None
    assert attachment_qurl("/does/not/exist.txt") is None
