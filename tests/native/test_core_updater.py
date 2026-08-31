from src.core import core_updater as cu


class _FakeSettings:
    def __init__(self):
        self._d = {}
        self.saved = 0

    def get(self, k, default=None):
        return self._d.get(k, default)

    def set(self, k, v):
        self._d[k] = v

    def save(self):
        self.saved += 1
        return True


def test_eula_acceptance_roundtrip():
    s = _FakeSettings()
    assert cu.is_eula_accepted(s) is False
    cu.mark_eula_accepted(s)
    assert cu.is_eula_accepted(s) is True
    assert s.saved == 1


def test_needs_core_setup_gated_by_platform(monkeypatch):
    s = _FakeSettings()
    # 不支持的平台：从不需要向导
    monkeypatch.setattr(cu, "core_supported", lambda: False)
    assert cu.needs_core_setup(s) is False
    # 支持的平台 + 未接受 EULA：需要
    monkeypatch.setattr(cu, "core_supported", lambda: True)
    monkeypatch.setattr(cu, "is_core_installed", lambda: True)
    assert cu.needs_core_setup(s) is True
    # 支持 + 已接受 + 已安装：不需要
    cu.mark_eula_accepted(s)
    assert cu.needs_core_setup(s) is False
    # 支持 + 已接受但内核缺失：需要
    monkeypatch.setattr(cu, "is_core_installed", lambda: False)
    assert cu.needs_core_setup(s) is True


def test_is_newer():
    assert cu.is_newer("v1.13.0", None) is True
    assert cu.is_newer("v1.13.0", "v1.12.9") is True
    assert cu.is_newer("v1.13.0", "v1.13.0") is False
    assert cu.is_newer("v1.13.0", "v1.14.0") is False


def test_parse_version_tolerates_prefix_and_suffix():
    assert cu._parse_version("v1.13.0") == (1, 13, 0)
    assert cu._parse_version("1.13.0-beta") == (1, 13, 0)


def test_apply_proxy():
    url = "https://github.com/SnowLuma/SnowLuma/releases/download/v1/x.zip"
    assert cu.apply_proxy(url, None) == url
    assert cu.apply_proxy(url, "") == url
    assert cu.apply_proxy(url, "https://ghproxy.com") == f"https://ghproxy.com/{url}"
    assert cu.apply_proxy(url, "https://ghproxy.com/") == f"https://ghproxy.com/{url}"


def test_select_asset_prefers_full_win_x64():
    assets = [
        {"name": "SnowLuma-v1.13.0-linux-x64.tar.gz"},
        {"name": "SnowLuma-v1.13.0-win-x64-lite.zip"},
        {"name": "SnowLuma-v1.13.0-win-x64.zip"},
    ]
    picked = cu.select_asset(assets, "win-x64")
    assert picked is not None and picked["name"] == "SnowLuma-v1.13.0-win-x64.zip"


def test_select_asset_none_when_absent():
    assert cu.select_asset([{"name": "SnowLuma-v1.13.0-linux-x64.tar.gz"}], "win-x64") is None


def test_extract_core_binaries(tmp_path):
    import io
    import zipfile

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("SnowLuma/native/snowluma-win32-x64.dll", b"MZ-dll")
        zf.writestr("SnowLuma/native/snowluma-win32-x64.node", b"node-bin")
        zf.writestr("SnowLuma/README.md", b"readme")
    installed = cu._extract_core_binaries(buf.getvalue(), tmp_path)
    assert set(installed) == {"snowluma-win32-x64.dll", "snowluma-win32-x64.node"}
    assert (tmp_path / "snowluma-win32-x64.dll").read_bytes() == b"MZ-dll"
    assert not (tmp_path / "README.md").exists()
