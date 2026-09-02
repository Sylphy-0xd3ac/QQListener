"""文件图标：优先问系统要，问不到再用内置那几张。

内置图标只覆盖 Office/PDF 几种，别的一律是同一张通用图；而且以前是把 28px 的
PNG 直接铺在 28 逻辑点上，HiDPI 屏上必然糊。这里两件事一起解决：

1. 用 `QFileIconProvider` 取系统给这类文件配的图标（Windows 上就是资源管理器里
   那张，装了 Office 就是 Office 的图标）。
2. 按屏幕缩放比取像素并设置 devicePixelRatio，Retina 上不再发虚。
"""

from __future__ import annotations

import tempfile
from pathlib import Path

from loguru import logger

from src.ui.qt_compat import (
    QApplication,
    QFileIconProvider,
    QFileInfo,
    QIcon,
    QPixmap,
    Qt,
    load_icon,
)

_PROBE_DIR = Path(tempfile.gettempdir()) / "qqlistener-icon-probes"

_provider: QFileIconProvider | None = None
_icon_cache: dict[str, QIcon] = {}


def _icon_provider() -> QFileIconProvider:
    global _provider
    if _provider is None:
        _provider = QFileIconProvider()
    return _provider


def _probe_path(suffix: str) -> Path | None:
    """系统图标要按真实文件问，所以给每种扩展名留一个空的探针文件。"""
    if not suffix:
        return None
    safe = "".join(ch for ch in suffix if ch.isalnum() or ch == ".")
    if not safe.startswith(".") or len(safe) < 2:
        return None
    try:
        _PROBE_DIR.mkdir(parents=True, exist_ok=True)
        probe = _PROBE_DIR / f"probe{safe}"
        if not probe.exists():
            probe.touch()
        return probe
    except OSError:
        logger.debug("创建图标探针文件失败: {}", suffix)
        return None


def system_icon_for(name_or_path: str) -> QIcon:
    """系统给这个文件/扩展名配的图标；拿不到返回空 QIcon。"""
    if not name_or_path:
        return QIcon()

    source = Path(name_or_path)
    key = source.suffix.lower() or "<none>"
    if source.exists():
        # 真文件优先：能拿到最贴切的图标（甚至是缩略图/关联程序图标）。
        key = f"path:{source}"
    if key in _icon_cache:
        return _icon_cache[key]

    target = source if source.exists() else _probe_path(source.suffix.lower())
    icon = QIcon()
    if target is not None:
        try:
            icon = _icon_provider().icon(QFileInfo(str(target)))
        except Exception:
            logger.debug("获取系统图标失败: {}", name_or_path, exc_info=True)
            icon = QIcon()
    _icon_cache[key] = icon
    return icon


def icon_pixmap(icon: QIcon, size: int) -> QPixmap:
    """按屏幕缩放比出图，HiDPI 上才不糊。"""
    ratio = 1.0
    app = QApplication.instance()
    if app is not None:
        screen = app.primaryScreen()
        if screen is not None:
            ratio = max(1.0, float(screen.devicePixelRatio()))

    pixels = int(round(size * ratio))
    pixmap = icon.pixmap(pixels, pixels)
    if pixmap.isNull():
        return pixmap
    if pixmap.width() != pixels or pixmap.height() != pixels:
        pixmap = pixmap.scaled(pixels, pixels, Qt.KeepAspectRatio, Qt.SmoothTransformation)
    pixmap.setDevicePixelRatio(ratio)
    return pixmap


def file_icon_pixmap(name_or_path: str, fallback_path: str = "", size: int = 28) -> QPixmap:
    """先系统图标，系统没有再用内置那张；都没有返回空 pixmap。"""
    icon = system_icon_for(name_or_path)
    if icon.isNull():
        icon = load_icon(fallback_path) if fallback_path else QIcon()
    if icon.isNull():
        return QPixmap()
    return icon_pixmap(icon, size)


def clear_cache() -> None:
    _icon_cache.clear()
