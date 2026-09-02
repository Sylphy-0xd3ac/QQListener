"""附件下载：落到用户的下载目录，带进度，完成后交给系统默认程序打开。"""

from __future__ import annotations

import mimetypes
import os
import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlparse
from urllib.request import Request, urlopen

from loguru import logger

_CHUNK = 64 * 1024
_USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/124.0 Safari/537.36 QQ/9.9.0"
)
_ILLEGAL_NAME_RE = re.compile(r'[<>:"/\\|?*\x00-\x1f]')

IMAGE_SUFFIXES = frozenset({".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".jfif"})
VIDEO_SUFFIXES = frozenset({".mp4", ".mov", ".mkv", ".avi", ".flv", ".webm", ".m4v"})


def default_download_dir() -> Path:
    """系统默认下载目录；拿不到就退回用户主目录。"""
    if sys.platform == "win32":
        profile = os.environ.get("USERPROFILE")
        if profile:
            return Path(profile) / "Downloads"
    home = Path.home()
    downloads = home / "Downloads"
    return downloads if downloads.exists() else home


def resolve_download_dir(configured: str | None) -> Path:
    target = Path(configured).expanduser() if configured else default_download_dir()
    try:
        target.mkdir(parents=True, exist_ok=True)
    except OSError:
        logger.warning("下载目录不可用，改用默认目录: {}", target)
        target = default_download_dir()
        target.mkdir(parents=True, exist_ok=True)
    return target


def sanitize_filename(name: str, fallback: str = "QQ 附件") -> str:
    cleaned = _ILLEGAL_NAME_RE.sub("_", (name or "").strip()).strip(" .")
    return cleaned[:180] or fallback


def filename_for(url: str, name: str = "", content_type: str = "") -> str:
    candidate = sanitize_filename(name, "") if name else ""
    if not candidate:
        path_name = os.path.basename(unquote(urlparse(url).path))
        candidate = sanitize_filename(path_name, "")
    if not candidate:
        candidate = "QQ 附件"
    if not Path(candidate).suffix:
        suffix = (
            mimetypes.guess_extension(content_type.split(";")[0].strip()) if content_type else ""
        )
        candidate += suffix or ".bin"
    return candidate


def unique_path(directory: Path, filename: str) -> Path:
    target = directory / filename
    if not target.exists():
        return target
    stem, suffix = target.stem, target.suffix
    for index in range(1, 1000):
        candidate = directory / f"{stem} ({index}){suffix}"
        if not candidate.exists():
            return candidate
    return directory / f"{stem}-{os.getpid()}{suffix}"


def is_image_name(name: str) -> bool:
    return Path(name or "").suffix.lower() in IMAGE_SUFFIXES


def is_video_name(name: str) -> bool:
    return Path(name or "").suffix.lower() in VIDEO_SUFFIXES


def download_to(
    url: str,
    directory: Path,
    *,
    name: str = "",
    on_progress=None,
    should_cancel=None,
    timeout: float = 30.0,
) -> Path:
    """阻塞下载到 ``directory``，返回落地路径。失败抛异常。"""
    if not url.lower().startswith(("http://", "https://")):
        raise ValueError(f"不支持的下载地址: {url}")

    request = Request(url, headers={"User-Agent": _USER_AGENT})
    directory.mkdir(parents=True, exist_ok=True)
    logger.info("开始下载 {} → {}", name or url, directory)
    with urlopen(request, timeout=timeout) as response:  # noqa: S310 - 已校验 http(s)
        status = getattr(response, "status", 200)
        if status >= 400:
            raise OSError(f"下载失败：HTTP {status}")
        content_type = response.headers.get("Content-Type", "") or ""
        total = int(response.headers.get("Content-Length") or 0)
        target = unique_path(directory, filename_for(url, name, content_type))
        partial = target.with_suffix(target.suffix + ".part")
        received = 0
        try:
            with partial.open("wb") as handle:
                while True:
                    if should_cancel is not None and should_cancel():
                        raise InterruptedError("下载已取消")
                    chunk = response.read(_CHUNK)
                    if not chunk:
                        break
                    handle.write(chunk)
                    received += len(chunk)
                    if on_progress is not None:
                        on_progress(received, total)
        except BaseException:
            partial.unlink(missing_ok=True)
            raise
    if received == 0:
        partial.unlink(missing_ok=True)
        raise OSError("下载失败：服务器返回空内容")
    partial.replace(target)
    logger.info("下载完成 {}（{} 字节）", target, received)
    return target
