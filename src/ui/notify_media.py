"""通知窗口里的富媒体控件：图片渲染、文件/视频卡片、下载进度圈。

统一交互：点「下载」→ 转圈（有 Content-Length 就是进度环）→ 变成「打开」→
点一下交给系统默认程序（图片查看器 / 播放器 / Office…）。
"""

from __future__ import annotations

import os
from pathlib import Path

from loguru import logger

from src.core.settings import get_settings
from src.ui.file_icons import file_icon_pixmap
from src.ui.fluent_compat import IndeterminateProgressRing
from src.ui.fluent_compat import ProgressRing as FluentProgressRing
from src.ui.qt_compat import (
    QDesktopServices,
    QFont,
    QFrame,
    QHBoxLayout,
    QLabel,
    QPixmap,
    QSizePolicy,
    QStackedLayout,
    Qt,
    QThread,
    QUrl,
    QVBoxLayout,
    QWidget,
    Signal,
)
from src.utils.downloads import download_to, resolve_download_dir

MAX_IMAGE_WIDTH = 460
MAX_IMAGE_HEIGHT = 280

_CARD_QSS = """
#MediaCard {
    background-color: #f7f9fc;
    border-radius: 10px;
    border: 1px solid #e2e8f0;
}
#MediaCard:hover { background-color: #eef4ff; }
#MediaCard QLabel { background: transparent; border: none; }
"""


def human_size(size: int) -> str:
    if size <= 0:
        return ""
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.0f} {unit}" if unit == "B" else f"{value:.1f} {unit}"
        value /= 1024
    return f"{value:.1f} GB"


def open_with_system_default(path: str) -> bool:
    """交给系统默认程序打开本地文件。"""
    if not path or not os.path.exists(path):
        return False
    return bool(QDesktopServices.openUrl(QUrl.fromLocalFile(os.path.abspath(path))))


class DownloadRing(QWidget):
    """下载进度环。

    用 qfluentwidgets 自带的两种环：知道总大小就画进度（`ProgressRing`），
    不知道就转圈（`IndeterminateProgressRing`）。两个叠在 QStackedLayout 里按需切换，
    这样动效和 Windows 上原生 Fluent 一致——手绘的弧线怎么调都差着味。
    """

    def __init__(self, diameter: int = 24, parent=None):
        super().__init__(parent)
        self.setFixedSize(diameter, diameter)

        self._stack = QStackedLayout(self)
        self._stack.setContentsMargins(0, 0, 0, 0)

        self._spinner = IndeterminateProgressRing(self)
        self._bar = FluentProgressRing(self)
        for ring in (self._spinner, self._bar):
            ring.setFixedSize(diameter, diameter)
            ring.setStrokeWidth(3)
        self._bar.setTextVisible(False)
        self._bar.setRange(0, 100)

        self._stack.addWidget(self._spinner)
        self._stack.addWidget(self._bar)
        self._stack.setCurrentWidget(self._spinner)

    def start(self):
        self._bar.setValue(0)
        self._stack.setCurrentWidget(self._spinner)
        self.show()

    def stop(self):
        self.hide()

    def set_progress(self, received: int, total: int):
        if total > 0:
            self._bar.setValue(int(min(100, received * 100 / total)))
            self._stack.setCurrentWidget(self._bar)
        else:
            self._stack.setCurrentWidget(self._spinner)


class DownloadTask(QThread):
    progressed = Signal(int, int)
    completed = Signal(str)
    failed = Signal(str)

    def __init__(self, url: str, directory: Path, name: str = "", parent=None):
        super().__init__(parent)
        self._url = url
        self._directory = directory
        self._name = name
        self._cancelled = False

    def cancel(self):
        self._cancelled = True

    def run(self):
        try:
            path = download_to(
                self._url,
                self._directory,
                name=self._name,
                on_progress=lambda received, total: self.progressed.emit(received, total),
                should_cancel=lambda: self._cancelled,
            )
        except InterruptedError:
            return
        except Exception as exc:
            logger.warning("附件下载失败 {}: {}", self._url, exc)
            self.failed.emit(str(exc))
            return
        self.completed.emit(str(path))


class _DownloadMixin:
    """把「下载 → 进度 → 打开」这段状态机复用给图片卡和文件卡。"""

    def _init_download(self, url: str, name: str, local_path: str = ""):
        self._url = url or ""
        self._name = name or ""
        self._local_path = local_path if local_path and os.path.exists(local_path) else ""
        self._task: DownloadTask | None = None

    @property
    def local_path(self) -> str:
        return self._local_path

    def _download_dir(self) -> Path:
        return resolve_download_dir(get_settings().download_dir)

    def start_download(self):
        if self._task is not None or not self._url:
            return
        task = DownloadTask(self._url, self._download_dir(), self._name, self)
        task.progressed.connect(self.on_download_progress)
        task.completed.connect(self._on_download_completed)
        task.failed.connect(self.on_download_failed)
        task.finished.connect(self._clear_task)
        self._task = task
        self.on_download_started()
        task.start()

    def _clear_task(self):
        self._task = None

    def _on_download_completed(self, path: str):
        self._local_path = path
        self.on_download_completed(path)

    def stop_download(self):
        if self._task is not None:
            self._task.cancel()

    # 子类实现
    def on_download_started(self): ...
    def on_download_progress(self, received: int, total: int): ...
    def on_download_completed(self, path: str): ...
    def on_download_failed(self, message: str): ...


class MediaCard(QFrame, _DownloadMixin):
    """文件 / 视频卡片。"""

    changed = Signal()

    def __init__(self, payload: dict, parent=None):
        super().__init__(parent)
        self._init_download(
            payload.get("url", ""), payload.get("name", ""), payload.get("local_path", "")
        )
        self.setObjectName("MediaCard")
        self.setStyleSheet(_CARD_QSS)
        self.setFixedHeight(62)
        self.setCursor(Qt.PointingHandCursor)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(14, 8, 14, 8)
        layout.setSpacing(12)

        self.icon_label = QLabel()
        # 系统图标优先（装了 Office 就是 Office 那张），拿不到才退回内置那几张。
        pixmap = file_icon_pixmap(
            self._local_path or self._name,
            payload.get("icon_file", ""),
            size=28,
        )
        if not pixmap.isNull():
            self.icon_label.setPixmap(pixmap)
        else:
            self.icon_label.setText("🎬" if payload.get("type") == "video" else "📄")
            self.icon_label.setFont(QFont("", 18))
        self.icon_label.setFixedWidth(30)

        text_box = QVBoxLayout()
        text_box.setSpacing(2)
        self.name_label = QLabel(self._name or "QQ 附件")
        self.name_label.setStyleSheet("color: #1a1a1a; font-size: 13px;")
        self.meta_label = QLabel(self._initial_meta(payload))
        self.meta_label.setStyleSheet("color: #7a7a7a; font-size: 11px;")
        text_box.addWidget(self.name_label)
        text_box.addWidget(self.meta_label)

        self.ring = DownloadRing(parent=self)
        self.ring.hide()
        self.action_label = QLabel()
        self.action_label.setStyleSheet("color: #0067c0; font-size: 13px;")

        layout.addWidget(self.icon_label)
        layout.addLayout(text_box, 1)
        layout.addWidget(self.ring)
        layout.addWidget(self.action_label)
        self._sync_action()

    def _initial_meta(self, payload: dict) -> str:
        size = human_size(int(payload.get("size", 0) or 0))
        kind = "视频" if payload.get("type") == "video" else "文件"
        return f"{kind}　{size}" if size else kind

    def _sync_action(self):
        if self._local_path:
            self.action_label.setText("打开")
            self.action_label.setStyleSheet("color: #0067c0; font-size: 13px;")
        elif self._url:
            self.action_label.setText("下载")
            self.action_label.setStyleSheet("color: #0067c0; font-size: 13px;")
        else:
            self.action_label.setText("无下载地址")
            self.action_label.setStyleSheet("color: #9a9a9a; font-size: 12px;")

    def on_download_started(self):
        self.action_label.setText("")
        self.ring.start()

    def on_download_progress(self, received: int, total: int):
        self.ring.set_progress(received, total)
        if total > 0:
            self.meta_label.setText(f"下载中　{human_size(received)} / {human_size(total)}")
        else:
            self.meta_label.setText(f"下载中　{human_size(received)}")

    def on_download_completed(self, path: str):
        self.ring.stop()
        self.meta_label.setText(f"已保存到　{Path(path).parent}")
        self._sync_action()
        # 文件真落地了，这时系统能给出最贴切的图标（含关联程序）。
        pixmap = file_icon_pixmap(path, size=28)
        if not pixmap.isNull():
            self.icon_label.setPixmap(pixmap)
        open_with_system_default(path)
        self.changed.emit()

    def on_download_failed(self, message: str):
        self.ring.stop()
        self.meta_label.setText(f"下载失败：{message}")
        self._sync_action()

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            if self._local_path:
                open_with_system_default(self._local_path)
            else:
                self.start_download()
        super().mousePressEvent(event)


class ImagePreview(QFrame, _DownloadMixin):
    """图片渲染：已下载就直接画出来，点击用系统默认图片查看器打开。"""

    changed = Signal()

    def __init__(
        self,
        payload: dict,
        *,
        max_width: int = MAX_IMAGE_WIDTH,
        max_height: int = MAX_IMAGE_HEIGHT,
        align_left: bool = False,
        flat: bool = False,
        parent=None,
    ):
        super().__init__(parent)
        self._init_download(
            payload.get("url", ""), payload.get("name", ""), payload.get("local_path", "")
        )
        self._max_width = max_width
        self._max_height = max_height
        self._pixmap: QPixmap | None = None
        self.setObjectName("MediaCard")
        # 引用块里的缩略图不套卡片：外面已经有主题色竖线了，再加一层灰底就太重。
        self.setStyleSheet(
            "#MediaCard { background: transparent; border: none; }" if flat else _CARD_QSS
        )
        self.setCursor(Qt.PointingHandCursor)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0) if flat else layout.setContentsMargins(8, 8, 8, 8)
        layout.setSpacing(4 if flat else 6)

        self.image_label = QLabel()
        # 引用块里的缩略图靠左，别在一大片空白里居中飘着。
        self.image_label.setAlignment(
            (Qt.AlignLeft | Qt.AlignVCenter) if align_left else Qt.AlignCenter
        )
        self.image_label.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Preferred)

        self.status_row = QWidget(self)
        status_layout = QHBoxLayout(self.status_row)
        status_layout.setContentsMargins(6, 0, 6, 2)
        status_layout.setSpacing(8)
        self.ring = DownloadRing(parent=self.status_row)
        self.ring.hide()
        self.status_label = QLabel("图片　点击下载")
        self.status_label.setStyleSheet("color: #6a6a6a; font-size: 12px;")
        status_layout.addWidget(self.ring)
        status_layout.addWidget(self.status_label, 1)

        layout.addWidget(self.image_label)
        layout.addWidget(self.status_row)

        if self._local_path:
            self._render(self._local_path)
        elif not self._url:
            self.status_label.setText("图片　无法获取下载地址")

    def _render(self, path: str) -> bool:
        pixmap = QPixmap(path)
        if pixmap.isNull():
            self.status_label.setText("图片　无法解码")
            return False
        self._pixmap = pixmap
        scaled = pixmap.scaled(
            self._max_width,
            self._max_height,
            Qt.KeepAspectRatio,
            Qt.SmoothTransformation,
        )
        self.image_label.setPixmap(scaled)
        self.image_label.setFixedHeight(scaled.height())
        self.status_row.hide()
        return True

    def on_download_started(self):
        self.ring.start()
        self.status_label.setText("图片　下载中")

    def on_download_progress(self, received: int, total: int):
        self.ring.set_progress(received, total)
        if total > 0:
            self.status_label.setText(f"图片　{human_size(received)} / {human_size(total)}")

    def on_download_completed(self, path: str):
        self.ring.stop()
        if self._render(path):
            open_with_system_default(path)
        self.changed.emit()

    def on_download_failed(self, message: str):
        self.ring.stop()
        self.status_label.setText(f"图片下载失败：{message}")

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            if self._local_path:
                open_with_system_default(self._local_path)
            else:
                self.start_download()
        super().mousePressEvent(event)


class SectionDivider(QWidget):
    """分割线。

    给了文字就嵌在中间（引用块用「引用消息」）；不给文字就是一条素线，
    用来隔开积压摘要里的每一条消息。
    """

    def __init__(self, text: str = "", parent=None):
        super().__init__(parent)
        layout = QHBoxLayout(self)
        layout.setContentsMargins(0, 2, 0, 2)
        layout.setSpacing(10 if text else 0)

        layout.addWidget(self._line(), 1)
        if text:
            label = QLabel(text)
            label.setStyleSheet("color: #9aa4b2; font-size: 11px; background: transparent;")
            layout.addWidget(label)
            layout.addWidget(self._line(), 1)

    @staticmethod
    def _line() -> QFrame:
        line = QFrame()
        line.setFrameShape(QFrame.HLine)
        line.setFixedHeight(1)
        line.setStyleSheet("background-color: #e3e7ee; border: none;")
        return line
