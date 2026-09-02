import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest

from src.utils.downloads import (
    default_download_dir,
    download_to,
    filename_for,
    is_image_name,
    is_video_name,
    resolve_download_dir,
    sanitize_filename,
    unique_path,
)


def test_filename_prefers_the_wire_name_then_the_url_path():
    assert filename_for("https://x/download?id=1", "期末复习.pdf") == "期末复习.pdf"
    assert filename_for("https://x/files/作业.docx") == "作业.docx"


def test_filename_gets_an_extension_from_the_content_type():
    assert filename_for("https://x/download?id=1", "", "image/png").endswith(".png")
    assert filename_for("https://x/download?id=1").endswith(".bin")


def test_illegal_characters_are_stripped():
    assert sanitize_filename("a/b:c*d?.txt") == "a_b_c_d?.txt".replace("?", "_")
    assert sanitize_filename("   ") == "QQ 附件"


def test_unique_path_avoids_clobbering_an_existing_file(tmp_path):
    (tmp_path / "a.txt").write_text("1")
    assert unique_path(tmp_path, "a.txt").name == "a (1).txt"
    (tmp_path / "a (1).txt").write_text("2")
    assert unique_path(tmp_path, "a.txt").name == "a (2).txt"


def test_media_kind_is_detected_by_suffix():
    assert is_image_name("板书.PNG") and not is_image_name("作业.docx")
    assert is_video_name("实验.mp4") and not is_video_name("板书.png")


def test_download_dir_falls_back_when_configured_path_is_unusable(tmp_path):
    configured = tmp_path / "downloads"
    assert resolve_download_dir(str(configured)) == configured
    assert configured.is_dir()
    assert resolve_download_dir("") == default_download_dir()


class _Handler(BaseHTTPRequestHandler):
    payload = b"hello-attachment"

    def do_GET(self):  # noqa: N802 - BaseHTTPRequestHandler 的接口
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(self.payload)))
        self.end_headers()
        self.wfile.write(self.payload)

    def log_message(self, *_args):
        pass


@pytest.fixture
def http_server():
    server = HTTPServer(("127.0.0.1", 0), _Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    yield f"http://127.0.0.1:{server.server_port}"
    server.shutdown()


def test_download_lands_in_the_target_dir_and_reports_progress(tmp_path, http_server):
    seen: list[tuple[int, int]] = []

    path = download_to(
        f"{http_server}/file",
        tmp_path,
        name="作业.txt",
        on_progress=lambda received, total: seen.append((received, total)),
    )

    assert path.parent == tmp_path
    assert path.name == "作业.txt"
    assert path.read_bytes() == _Handler.payload
    assert seen and seen[-1] == (len(_Handler.payload), len(_Handler.payload))


def test_cancelled_download_leaves_no_partial_file(tmp_path, http_server):
    with pytest.raises(InterruptedError):
        download_to(f"{http_server}/file", tmp_path, name="x.txt", should_cancel=lambda: True)

    assert list(tmp_path.iterdir()) == []


def test_non_http_scheme_is_refused(tmp_path):
    with pytest.raises(ValueError):
        download_to("file:///etc/passwd", tmp_path)
