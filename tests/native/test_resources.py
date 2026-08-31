import sys

from src.core.resources import app_root, bundle_root, resource_path


def test_source_roots_are_the_repository():
    assert (app_root() / "src").is_dir()
    assert bundle_root() == app_root()


def test_frozen_app_separates_persistent_and_bundle_roots(monkeypatch, tmp_path):
    executable_dir = tmp_path / "installed"
    bundle_dir = tmp_path / "_MEI123"
    monkeypatch.setattr(sys, "frozen", True, raising=False)
    monkeypatch.setattr(sys, "_MEIPASS", str(bundle_dir), raising=False)
    monkeypatch.setattr(sys, "executable", str(executable_dir / "QQListener.exe"))

    assert app_root() == executable_dir
    assert bundle_root() == bundle_dir
    assert resource_path("asset", "icon.png") == bundle_dir / "asset" / "icon.png"
