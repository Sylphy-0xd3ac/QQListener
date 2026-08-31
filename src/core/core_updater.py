"""SnowLuma 内核（专有二进制）的获取/更新。

合规（见 spec「LICENSE 合规边界」）：只从 SnowLuma **官方 release** 下载，
代理由用户配置、不内置不自建镜像，不随仓库分发二进制。二进制装到
`<app_root>/native/`，版本记在 `native/version.txt`。
"""

from __future__ import annotations

import io
import json
import os
import sys
import urllib.request
import zipfile
from dataclasses import dataclass, field
from pathlib import Path

from loguru import logger

from src.core.resources import app_root

CORE_REPO = "SnowLuma/SnowLuma"
DEFAULT_PLATFORM = "win-x64"

RAW_BASE = f"https://raw.githubusercontent.com/{CORE_REPO}/main"
# 运行时从官方仓库拉取，不随本程序打包 SnowLuma 的法律文本。
LEGAL_DOCS = {
    "eula": ("SnowLuma EULA（最终用户许可协议）", "EULA.md"),
    "license": ("SnowLuma LICENSE（许可证）", "LICENSE"),
    "privacy": ("SnowLuma 隐私说明", "PRIVACY.md"),
}
EULA_ACCEPTED_KEY = "SnowLuma_EULA_Accepted"


def core_supported() -> bool:
    """当前平台是否有 SnowLuma 官方内核（目前仅 Windows x64）。"""
    return sys.platform == "win32"


# Python 装载器只需要钩子 DLL；不再下载 SnowLuma 的 Node 原生装载器。
CORE_BINARY_NAMES = ("snowluma-win32-x64.dll",)


def native_dir() -> Path:
    return app_root() / "native"


def version_file() -> Path:
    return native_dir() / "version.txt"


@dataclass
class ReleaseInfo:
    tag: str
    asset_name: str
    asset_url: str


@dataclass
class UpdateStatus:
    installed: bool
    current_version: str | None
    latest_version: str | None = None
    has_update: bool = False
    error: str = ""
    binaries: list[str] = field(default_factory=list)


def current_core_version() -> str | None:
    vf = version_file()
    if vf.exists():
        try:
            text = vf.read_text(encoding="utf-8").strip()
            return text or None
        except OSError:
            return None
    return None


def installed_binaries() -> list[str]:
    nd = native_dir()
    return [name for name in CORE_BINARY_NAMES if (nd / name).exists()]


def is_core_installed() -> bool:
    return bool(installed_binaries())


def is_eula_accepted(settings) -> bool:
    return bool(settings.get(EULA_ACCEPTED_KEY, False))


def mark_eula_accepted(settings) -> None:
    settings.set(EULA_ACCEPTED_KEY, True)
    settings.save()


def needs_core_setup(settings) -> bool:
    """受支持平台上，EULA 未接受或内核未安装时需要走安装向导。"""
    if not core_supported():
        return False
    return not is_eula_accepted(settings) or not is_core_installed()


def fetch_legal_text(kind: str, proxy: str | None = None, timeout: int = 15) -> str:
    _, path = LEGAL_DOCS[kind]
    url = apply_proxy(f"{RAW_BASE}/{path}", proxy)
    with urllib.request.urlopen(url, timeout=timeout) as resp:  # noqa: S310 (官方域名)
        return resp.read().decode("utf-8", "replace")


def _parse_version(value: str) -> tuple[int, ...]:
    digits = value.strip().lstrip("vV")
    parts: list[int] = []
    for chunk in digits.split("."):
        num = ""
        for ch in chunk:
            if ch.isdigit():
                num += ch
            else:
                break
        parts.append(int(num) if num else 0)
    return tuple(parts) or (0,)


def is_newer(remote: str, local: str | None) -> bool:
    if not local:
        return True
    return _parse_version(remote) > _parse_version(local)


def apply_proxy(url: str, proxy: str | None) -> str:
    """把第三方 GitHub 代理前缀拼到官方下载 URL 前（代理由用户提供）。"""
    if not proxy:
        return url
    return f"{proxy.rstrip('/')}/{url}"


def select_asset(assets: list[dict], platform: str = DEFAULT_PLATFORM) -> dict | None:
    """选完整版（非 lite）的平台压缩包。"""
    for asset in assets:
        name = str(asset.get("name", ""))
        if platform in name and name.endswith(".zip") and "lite" not in name.lower():
            return asset
    return None


def latest_release(platform: str = DEFAULT_PLATFORM, timeout: int = 15) -> ReleaseInfo:
    api = f"https://api.github.com/repos/{CORE_REPO}/releases/latest"
    req = urllib.request.Request(api, headers={"Accept": "application/vnd.github+json"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310 (官方域名)
        data = json.load(resp)
    asset = select_asset(data.get("assets", []), platform)
    if asset is None:
        raise RuntimeError(f"未在 release {data.get('tag_name')} 找到 {platform} 完整包")
    return ReleaseInfo(
        tag=str(data.get("tag_name", "")),
        asset_name=str(asset.get("name", "")),
        asset_url=str(asset.get("browser_download_url", "")),
    )


def check_update(platform: str = DEFAULT_PLATFORM) -> UpdateStatus:
    status = UpdateStatus(
        installed=is_core_installed(),
        current_version=current_core_version(),
        binaries=installed_binaries(),
    )
    try:
        release = latest_release(platform)
    except Exception as exc:  # 网络/解析失败：给出可读错误，不静默
        logger.debug("检查内核更新失败", exc_info=True)
        status.error = str(exc)
        return status
    status.latest_version = release.tag
    status.has_update = is_newer(release.tag, status.current_version)
    return status


def _extract_core_binaries(zip_bytes: bytes, dest: Path) -> list[str]:
    dest.mkdir(parents=True, exist_ok=True)
    installed: list[str] = []
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as zf:
        for info in zf.infolist():
            if info.is_dir():
                continue
            base = os.path.basename(info.filename)
            if base in CORE_BINARY_NAMES:
                (dest / base).write_bytes(zf.read(info))
                installed.append(base)
    return installed


def download_and_install(
    proxy: str | None = None, platform: str = DEFAULT_PLATFORM, timeout: int = 120
) -> str:
    """从官方 release 下载并安装内核二进制到 native/，返回安装的版本号。"""
    release = latest_release(platform)
    download_url = apply_proxy(release.asset_url, proxy)
    logger.info("下载内核: {}", download_url)
    with urllib.request.urlopen(download_url, timeout=timeout) as resp:  # noqa: S310
        zip_bytes = resp.read()

    installed = _extract_core_binaries(zip_bytes, native_dir())
    if not installed:
        raise RuntimeError("压缩包内未找到内核二进制")
    version_file().write_text(release.tag, encoding="utf-8")
    logger.info("内核安装完成: {} ({})", release.tag, ", ".join(installed))
    return release.tag
