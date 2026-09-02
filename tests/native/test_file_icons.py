"""文件图标：系统图标要按真实文件问，所以每种扩展名留一个探针文件。"""

from src.ui import file_icons


def test_probe_file_is_created_once_per_extension(tmp_path, monkeypatch):
    monkeypatch.setattr(file_icons, "_PROBE_DIR", tmp_path / "probes")

    first = file_icons._probe_path(".docx")
    second = file_icons._probe_path(".docx")

    assert first is not None
    assert first.exists() and first.name == "probe.docx"
    assert second == first
    assert list((tmp_path / "probes").iterdir()) == [first]


def test_each_extension_gets_its_own_probe(tmp_path, monkeypatch):
    monkeypatch.setattr(file_icons, "_PROBE_DIR", tmp_path / "probes")

    names = {file_icons._probe_path(ext).name for ext in (".pdf", ".xlsx", ".mp4")}

    assert names == {"probe.pdf", "probe.xlsx", "probe.mp4"}


def test_missing_or_malformed_suffix_yields_no_probe(tmp_path, monkeypatch):
    monkeypatch.setattr(file_icons, "_PROBE_DIR", tmp_path / "probes")

    assert file_icons._probe_path("") is None
    assert file_icons._probe_path(".") is None
    assert file_icons._probe_path("docx") is None  # 没有点，不是扩展名


def test_probe_name_strips_path_separators(tmp_path, monkeypatch):
    """扩展名来自消息里的文件名，不能让它跑出探针目录。"""
    monkeypatch.setattr(file_icons, "_PROBE_DIR", tmp_path / "probes")

    probe = file_icons._probe_path(".do/../../cx")

    assert probe is None or probe.parent == tmp_path / "probes"
