"""媒体附件工具：URL 判定、安全文件名、文件图标匹配、异步下载。

从原 notification_engines.py 迁出，供原生捕获链下载图片/文件缩略图使用。
"""

from __future__ import annotations

import mimetypes
import os
import re
import sys
import tempfile
import uuid
from pathlib import Path
from urllib.parse import unquote, urlparse

from loguru import logger

DOCUMENT_ICON_MAP = {
    ".pdf": "asset/FileIcon/pdf.png",
    ".ppt": "asset/FileIcon/powerpoint.png",
    ".pptx": "asset/FileIcon/powerpoint.png",
    ".xls": "asset/FileIcon/excel.png",
    ".xlsx": "asset/FileIcon/excel.png",
    ".doc": "asset/FileIcon/word.png",
    ".docx": "asset/FileIcon/word.png",
}


def is_http_url(value: object) -> bool:
    return isinstance(value, str) and value.startswith(("http://", "https://"))


def local_path_from_ref(value: object) -> str | None:
    if not isinstance(value, str) or not value:
        return None

    if value.startswith("file://"):
        parsed = urlparse(value)
        path = unquote(parsed.path)
        if sys.platform == "win32" and path.startswith("/") and re.match(r"^/[a-zA-Z]:", path):
            path = path[1:]
        return path if os.path.exists(path) else None

    return value if os.path.exists(value) else None


def safe_filename(value: object, fallback: str) -> str:
    if isinstance(value, str) and value:
        name = os.path.basename(unquote(urlparse(value).path)) or os.path.basename(value)
        name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")
        if name:
            return name
    return fallback


def file_icon_for_path(path: str) -> str:
    ext = Path(path).suffix.lower()
    return DOCUMENT_ICON_MAP.get(ext, "asset/FileIcon/enb.png")


async def download_url(session, url: str, category: str, filename: str | None = None) -> str | None:
    if not is_http_url(url):
        return None

    try:
        async with session.get(url, timeout=20) as response:
            if response.status >= 400:
                logger.warning("下载附件失败: {} {}", response.status, url)
                return None

            content = await response.read()
            if not content:
                return None

            content_type = response.headers.get("Content-Type", "").split(";")[0].strip()
            ext = Path(filename or "").suffix
            if not ext:
                ext = mimetypes.guess_extension(content_type) or Path(urlparse(url).path).suffix
            if not ext:
                ext = ".bin"

            safe_name = safe_filename(filename or url, f"{uuid.uuid4().hex}{ext}")
            if not Path(safe_name).suffix:
                safe_name = f"{safe_name}{ext}"

            media_dir = Path(tempfile.gettempdir()) / "qqlistener_media" / category
            media_dir.mkdir(parents=True, exist_ok=True)
            path = media_dir / f"{uuid.uuid4().hex}_{safe_name}"
            path.write_bytes(content)
            return str(path)
    except Exception:
        logger.exception("下载附件异常: {}", url)
        return None
