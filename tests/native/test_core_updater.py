from src.core import core_updater as cu


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
