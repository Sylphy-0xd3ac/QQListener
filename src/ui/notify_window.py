import os
import sys

import pygame
from loguru import logger

from src.core.settings import get_settings
from src.native.message_sender import send_text_reply_sync
from src.ui.fluent_compat import FluentIcon as FIF
from src.ui.fluent_compat import TransparentToolButton
from src.ui.notify_media import ImagePreview, MediaCard, SectionDivider
from src.ui.qt_compat import (
    QApplication,
    QColor,
    QDesktopServices,
    QEasingCurve,
    QFont,
    QFontDatabase,
    QFrame,
    QGraphicsDropShadowEffect,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPropertyAnimation,
    QPushButton,
    QScrollArea,
    QSizePolicy,
    Qt,
    QThread,
    QTimer,
    QUrl,
    QVariantAnimation,
    QVBoxLayout,
    QWidget,
    Signal,
)
from src.utils.media import is_http_url, local_path_from_ref
from src.utils.message_processor import digest_entries
from src.utils.tts import TTSManager

CARD_WIDTH = 600
# TTS 卡住时，通知最多再多留这么久就自己关——不能让一条播报把窗口钉死在屏幕上。
TTS_CLOSE_GRACE_MS = 10000

# 主题色 / 次要文字色，引用条和链接都用它，改主题只改这里。
ACCENT = "#0067c0"
SECONDARY_TEXT = "#6b7280"

PRIORITY_STYLES = {
    0: {
        "accent_rgb": "0, 120, 212",
        "text_color": "#202020",
        "overlay": "rgba(0, 0, 0, 120)",
    },
    1: {
        "accent_rgb": "96, 94, 92",
        "text_color": "#202020",
        "overlay": "rgba(0, 0, 0, 96)",
    },
    2: {
        "accent_rgb": "96, 94, 92",
        "text_color": "#202020",
        "overlay": "rgba(0, 0, 0, 80)",
    },
}

_BUTTON_QSS = """
QPushButton {
    border-radius: 6px;
    border: 1px solid #d5dbe4;
    background-color: #ffffff;
    color: #1a1a1a;
    font-size: 14px;
    padding: 0 18px;
}
QPushButton:hover { background-color: #f2f6fc; }
QPushButton:pressed { background-color: #e7eef8; }
"""

_PRIMARY_BUTTON_QSS = """
QPushButton {
    border-radius: 6px;
    border: 1px solid #0067c0;
    background-color: #0067c0;
    color: #ffffff;
    font-size: 14px;
    padding: 0 18px;
}
QPushButton:hover { background-color: #0a74d1; }
QPushButton:pressed { background-color: #005ba6; }
QPushButton:disabled { background-color: #9dbfe0; border-color: #9dbfe0; }
"""


def attachment_qurl(target: object) -> QUrl | None:
    local_path = local_path_from_ref(target)
    if local_path:
        return QUrl.fromLocalFile(os.path.abspath(local_path))
    if is_http_url(target):
        return QUrl(str(target))
    return None


def open_attachment(target: object) -> bool:
    url = attachment_qurl(target)
    if url is None:
        return False
    return bool(QDesktopServices.openUrl(url))


def _plain_button(text: str, *, primary: bool = False) -> QPushButton:
    """通知窗口是高频路径，按钮走原生 QPushButton + QSS，绕开 Fluent 控件的动画开销。"""
    button = QPushButton(text)
    button.setStyleSheet(_PRIMARY_BUTTON_QSS if primary else _BUTTON_QSS)
    button.setFixedHeight(38)
    button.setCursor(Qt.PointingHandCursor)
    return button


class ReplyTask(QThread):
    """把回复发送放到线程里，别卡住通知窗口的绘制。"""

    succeeded = Signal()
    failed = Signal(str)

    def __init__(self, route: dict, text: str, parent=None):
        super().__init__(parent)
        self._route = dict(route or {})
        self._text = text

    def run(self):
        try:
            send_text_reply_sync(self._route, self._text)
        except Exception as exc:
            logger.warning("回复发送失败: {}", exc)
            self.failed.emit(str(exc))
            return
        self.succeeded.emit()


class MessageBlock(QWidget):
    """一条消息的富渲染：文本 + 图片 + 文件/视频卡片。"""

    changed = Signal()

    _TEXT_KINDS = frozenset({"text", "at"})

    def __init__(self, segments: list[dict], *, font: QFont, compact: bool = False, parent=None):
        super().__init__(parent)
        self._font = font
        self._compact = compact
        self._layout = QVBoxLayout(self)
        self._layout.setContentsMargins(0, 0, 0, 0)
        self._layout.setSpacing(6 if compact else 8)
        self._has_content = False

        # 引用块里的图只当缩略图看，别把正文挤下去。
        max_width, max_height = (170, 110) if compact else (460, 280)

        # 按线序渲染：连续的文字/@ 合成一段，图片和卡片就地插入。
        # 否则 "看这个[图片]再看这个[图片]" 会被拆成"文字全在上、图全在下"。
        run: list[str] = []
        for seg in segments:
            kind = seg.get("type")
            if kind in self._TEXT_KINDS:
                run.append(self._segment_text(seg, kind))
                continue

            self._flush_text(run)
            if kind == "image":
                widget = ImagePreview(
                    seg,
                    max_width=max_width,
                    max_height=max_height,
                    align_left=compact,
                    flat=compact,
                    parent=self,
                )
            elif kind in {"file", "video"}:
                widget = MediaCard(seg, parent=self)
            else:
                continue
            widget.changed.connect(self.changed)
            self._layout.addWidget(widget)
            self._has_content = True
        self._flush_text(run)

        if not self._has_content:
            placeholder = QLabel("[空消息]")
            placeholder.setStyleSheet("color: #9aa4b2; font-size: 12px;")
            self._layout.addWidget(placeholder)

    @staticmethod
    def _segment_text(seg: dict, kind: str) -> str:
        if kind == "text":
            return seg.get("text", "")
        # 线上的 @ 文本本来就带 "@" 和尾随空格。
        name = seg.get("text", "").strip().lstrip("@")
        return f"[@{name}]" if name else "[@]"

    def _flush_text(self, run: list[str]) -> None:
        text = "".join(run).strip()
        run.clear()
        if not text:
            return
        label = QLabel(text)
        label.setWordWrap(True)
        label.setFont(self._font)
        label.setStyleSheet(
            f"color: {'#6b7280' if self._compact else '#202020'};"
            " border: none; background: transparent;"
        )
        label.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Minimum)
        self._layout.addWidget(label)
        self._has_content = True

    @property
    def has_content(self) -> bool:
        return self._has_content


class MessageEntry(QWidget):
    """摘要里的一条消息：发送者 + 引用块 + 正文，全部走完整解析。"""

    changed = Signal()

    def __init__(
        self,
        entry: dict,
        *,
        title_font: QFont,
        body_font: QFont,
        quote_font: QFont,
        show_sender: bool,
        details_shown: bool,
        parent=None,
    ):
        super().__init__(parent)
        self._sender = entry.get("sender", "")
        self._detail = entry.get("detail", "")
        self._details_shown = details_shown

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(10)

        self.sender_label: QLabel | None = None
        self.detail_label: QLabel | None = None
        if show_sender and self._sender:
            head = QWidget(self)
            head_layout = QVBoxLayout(head)
            head_layout.setContentsMargins(0, 0, 0, 0)
            head_layout.setSpacing(2)

            self.sender_label = QLabel(self._sender)
            self.sender_label.setFont(title_font)
            self.sender_label.setWordWrap(True)
            self.sender_label.setStyleSheet(
                "color: #111111; border: none; background: transparent;"
            )
            head_layout.addWidget(self.sender_label)

            # 号码是副行：小一号、灰色，别跟名字抢视觉重量。
            self.detail_label = QLabel(self._detail)
            self.detail_label.setWordWrap(True)
            self.detail_label.setStyleSheet(
                f"color: {SECONDARY_TEXT}; font-size: 12px; border: none; background: transparent;"
            )
            self.detail_label.setVisible(bool(self._detail and details_shown))
            head_layout.addWidget(self.detail_label)

            if self._detail:
                head.setCursor(Qt.PointingHandCursor)
                self.sender_label.setCursor(Qt.PointingHandCursor)
                self.sender_label.setToolTip("点击显示/隐藏群号与 QQ 号")
                self.sender_label.mousePressEvent = self._toggle_detail
            layout.addWidget(head)

        quote = entry.get("quote")
        if quote:
            layout.addWidget(self._quote_box(quote, quote_font))
            layout.addWidget(SectionDivider("引用消息"))

        self.body = MessageBlock(list(entry.get("segments") or []), font=body_font, parent=self)
        self.body.changed.connect(self.changed)
        layout.addWidget(self.body)

    def _quote_box(self, quote: dict, font: QFont) -> QFrame:
        box = QFrame()
        box.setObjectName("QuoteBox")
        # Fluent 的引用是"左边一条主题色竖线 + 灰字"，不是一张灰底卡片。
        box.setStyleSheet(f"""
            #QuoteBox {{
                background-color: transparent;
                border: none;
                border-left: 3px solid {ACCENT};
            }}
            #QuoteBox QLabel {{ background: transparent; border: none; }}
        """)
        box_layout = QVBoxLayout(box)
        box_layout.setContentsMargins(14, 2, 0, 2)
        box_layout.setSpacing(4)

        sender = QLabel(quote.get("sender", "对方"))
        sender.setStyleSheet(f"color: {ACCENT}; font-size: 12px; font-weight: 600;")
        box_layout.addWidget(sender)

        block = MessageBlock(list(quote.get("segments") or []), font=font, compact=True, parent=box)
        block.changed.connect(self.changed)
        box_layout.addWidget(block)
        return box

    def _toggle_detail(self, _event=None):
        if self.detail_label is None:
            return
        self._details_shown = not self._details_shown
        self.detail_label.setVisible(self._details_shown)
        self.changed.emit()


class NotifyWindow(QWidget):
    """通知窗口"""

    def __init__(self, data: dict, parent=None):
        super().__init__(parent)
        self.data = data
        self.settings = get_settings()
        self.duration = data.get("Duration", 5000)
        self.animations = []
        self.font_cache = {}
        self._auto_close_requested = False
        self._closing = False
        self._details_shown = self.settings.show_ids_by_default
        self._reply_task: ReplyTask | None = None
        self.entries = digest_entries(data)
        self.entries[0]["segments"] = self._legacy_segments(self.entries[0].get("segments") or [])
        self.is_digest = len(self.entries) > 1 or bool(data.get("Digest"))
        self.lite_mode = self.settings.lite_mode
        self.use_overlay = self.settings.notify_mask and not self.lite_mode
        self.use_animation = self.settings.notify_animation and not self.lite_mode
        self.tts_manager = TTSManager(self)
        self.tts_manager.finished.connect(self._on_tts_finished)

        self._load_fonts()
        self.init_ui()

        if self.use_animation:
            self.init_animation()
            if data.get("Calling") and self.settings.calling_animation:
                self.start_calling_effect()
        else:
            self.setWindowOpacity(1)
            if self.duration > 0:
                QTimer.singleShot(self.duration, self._request_auto_close)

        self._play_sound()
        self._play_tts()

    # ---------- 构建 ----------

    def _load_fonts(self):
        def load_font(path, fallback="Segoe UI"):
            if not os.path.exists(path):
                logger.warning("字体文件不存在: {}", path)
                return fallback
            font_id = QFontDatabase.addApplicationFont(path)
            if font_id != -1:
                family = QFontDatabase.applicationFontFamilies(font_id)[0]
                return family
            logger.warning("字体加载失败: {}", path)
            return fallback

        self.title_family = load_font(self.settings.notify_title_font)
        self.msg_family = load_font(self.settings.notify_message_font)

    def init_ui(self):
        flags = Qt.FramelessWindowHint | Qt.Tool
        if self.settings.always_on_top:
            flags |= Qt.WindowStaysOnTopHint
        self.setWindowFlags(flags)
        self.setAttribute(Qt.WA_TranslucentBackground, True)

        self.overlay = None
        if self.use_overlay:
            screen_geo = QApplication.primaryScreen().geometry()
            self.setGeometry(screen_geo)
            style = PRIORITY_STYLES.get(self.data.get("Priority", 2)) or PRIORITY_STYLES[2]
            self.overlay = QWidget(self)
            self.overlay.setGeometry(0, 0, self.width(), self.height())
            self.overlay.setStyleSheet(f"background-color: {style['overlay']};")

        self.bg_widget = QWidget(self)
        self.bg_widget.setObjectName("BgWidget")
        self.bg_widget.setFixedWidth(CARD_WIDTH)
        self._apply_card_style()

        self.main_layout = QVBoxLayout(self.bg_widget)
        self.main_layout.setContentsMargins(30, 24, 30, 20)
        self.main_layout.setSpacing(14)

        self._build_sender_row()
        self._build_content()
        self._build_actions()
        self._build_footer()

        self._relayout()
        self.bg_widget.raise_()

    def _apply_card_style(self, border: str = "1px solid #dfe5ec"):
        self.bg_widget.setStyleSheet(f"""
            #BgWidget {{
                background-color: white;
                border-radius: 14px;
                border: {border};
            }}
            #BgWidget QLabel {{
                background: transparent;
                border: none;
            }}
        """)

    def _build_sender_row(self):
        """标题行。

        单条消息：标题就是发送者，点一下展开号码。
        积压摘要：标题是"暂停期间的 N 条消息"，发送者由每条消息自己那行负责。
        """
        if self.is_digest:
            self.sender_name = self.data.get("Sender", "系统通知")
            self.sender_detail = ""
        else:
            first = self.entries[0]
            self.sender_name = first.get("sender") or self.data.get("Sender", "系统通知")
            self.sender_detail = first.get("detail") or self.data.get("Sender_Detail", "")

        head = QWidget()
        head_row = QHBoxLayout(head)
        head_row.setContentsMargins(0, 0, 0, 0)
        head_row.setSpacing(8)

        head_text = QWidget(head)
        head_layout = QVBoxLayout(head_text)
        head_layout.setContentsMargins(0, 0, 0, 0)
        head_layout.setSpacing(3)

        self.label_sender = QLabel(self.sender_name)
        self.label_sender.setFont(QFont(self.title_family, 18, QFont.Bold))
        self.label_sender.setWordWrap(True)
        self.label_sender.setStyleSheet("color: #111111; border: none; background: transparent;")
        head_layout.addWidget(self.label_sender)

        # 群号/QQ 号是副行：小一号、灰色，点标题才出现。
        self.label_sender_detail = QLabel(self.sender_detail)
        self.label_sender_detail.setWordWrap(True)
        self.label_sender_detail.setStyleSheet(
            f"color: {SECONDARY_TEXT}; font-size: 12px; border: none; background: transparent;"
        )
        self.label_sender_detail.setVisible(bool(self.sender_detail and self._details_shown))
        head_layout.addWidget(self.label_sender_detail)

        if self.sender_detail:
            self.label_sender.setCursor(Qt.PointingHandCursor)
            self.label_sender.setToolTip("点击显示/隐藏群号与 QQ 号")
            self.label_sender.mousePressEvent = self._toggle_sender_detail

        # 右上角常驻关闭按钮：进了回复模式底部的"取消"就没了，
        # 没有这个按钮窗口就关不掉。
        self.btn_close = TransparentToolButton(FIF.CLOSE, head)
        self.btn_close.setFixedSize(30, 30)
        self.btn_close.setToolTip("关闭（Esc）")
        self.btn_close.clicked.connect(lambda *_: self.close_animation())

        head_row.addWidget(head_text, 1)
        head_row.addWidget(self.btn_close, 0, Qt.AlignTop)
        self.main_layout.addWidget(head)

    def _toggle_sender_detail(self, _event=None):
        if not self.sender_detail:
            return
        self._details_shown = not self._details_shown
        self.label_sender_detail.setVisible(self._details_shown)
        self._relayout()

    def _build_content(self):
        """引用块 + 正文放进可滚动区域：内容再多也不会把卡片撑出屏幕。"""
        self.content_widget = QWidget()
        self.content_layout = QVBoxLayout(self.content_widget)
        self.content_layout.setContentsMargins(0, 0, 0, 0)
        self.content_layout.setSpacing(14)

        self._build_entries()

        self.content_area = QScrollArea()
        self.content_area.setWidget(self.content_widget)
        self.content_area.setWidgetResizable(True)
        self.content_area.setFrameShape(QFrame.NoFrame)
        self.content_area.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.content_area.setStyleSheet(
            "QScrollArea { background: transparent; border: none; }"
            "QScrollArea > QWidget > QWidget { background: transparent; }"
            "QScrollBar:vertical { width: 6px; background: transparent; margin: 2px; }"
            "QScrollBar::handle:vertical { background: #cbd3de; border-radius: 3px; min-height: 24px; }"
            "QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }"
        )
        self.main_layout.addWidget(self.content_area)

    def _build_entries(self):
        """逐条渲染。条与条之间是一条素线（没有"引用消息"那种标签），
        每条内部若有引用，仍按原样显示引用块 + 「引用消息」分割线。"""
        title_font = QFont(self.title_family, 14, QFont.Bold)
        body_font = self._get_font(self.msg_family, 13)
        quote_font = self._get_font(self.msg_family, 12)

        for index, entry in enumerate(self.entries):
            if index:
                self.content_layout.addWidget(SectionDivider())
            widget = MessageEntry(
                entry,
                title_font=title_font,
                body_font=body_font,
                quote_font=quote_font,
                # 单条时发送者已经在大标题上了，不用重复一遍
                show_sender=self.is_digest,
                details_shown=self._details_shown,
                parent=self.content_widget,
            )
            widget.changed.connect(self._relayout)
            self.content_layout.addWidget(widget)

    def _legacy_segments(self, segments: list[dict]) -> list[dict]:
        """兼容只带 Message/Pic_Path/file_target 的老载荷。"""
        segments = list(segments)
        if not segments and self.data.get("Message"):
            segments = [{"type": "text", "text": self.data["Message"]}]

        legacy_pic = self.data.get("Pic_Path")
        if legacy_pic and not any(seg.get("type") == "image" for seg in segments):
            segments.append({"type": "image", "local_path": legacy_pic, "name": ""})

        legacy_file = self.data.get("file_target") or self.data.get("file")
        if legacy_file and not any(seg.get("type") in {"file", "video"} for seg in segments):
            segments.append(
                {
                    "type": "file",
                    "url": legacy_file if is_http_url(legacy_file) else "",
                    "local_path": legacy_file if not is_http_url(legacy_file) else "",
                    "name": self.data.get("file_name", ""),
                    "icon_file": self.data.get("icon_file", ""),
                }
            )
        return segments

    def _build_actions(self):
        self.action_row = QWidget()
        btn_layout = QHBoxLayout(self.action_row)
        btn_layout.setContentsMargins(0, 0, 0, 0)
        btn_layout.setSpacing(12)

        self.btn_ok = _plain_button(self.settings.ok_button_text, primary=True)
        self.btn_cancel = _plain_button(self.settings.cancel_button_text)
        self.btn_ok.setMinimumWidth(160)
        self.btn_cancel.setMinimumWidth(160)
        btn_layout.addWidget(self.btn_ok)
        btn_layout.addWidget(self.btn_cancel)
        self.main_layout.addWidget(self.action_row)

        self.reply_target_label = QLabel()
        self.reply_target_label.setStyleSheet(f"color: {SECONDARY_TEXT}; font-size: 12px;")
        self.reply_target_label.hide()
        self.main_layout.addWidget(self.reply_target_label)

        self.reply_row = QWidget()
        reply_layout = QHBoxLayout(self.reply_row)
        reply_layout.setContentsMargins(0, 0, 0, 0)
        reply_layout.setSpacing(10)
        self.reply_input = QLineEdit(self.settings.reply_default_text)
        self.reply_input.setFixedHeight(38)
        self.reply_input.setStyleSheet(
            "QLineEdit { border: 1px solid #d5dbe4; border-radius: 6px; padding: 0 12px;"
            " font-size: 14px; color: #1a1a1a; background: #ffffff; }"
            "QLineEdit:focus { border-color: #0067c0; }"
        )
        self.btn_send = _plain_button("发送", primary=True)
        self.btn_send.setMinimumWidth(96)
        reply_layout.addWidget(self.reply_input, 1)
        reply_layout.addWidget(self.btn_send)
        self.reply_row.hide()
        self.main_layout.addWidget(self.reply_row)

        self.reply_status = QLabel("")
        self.reply_status.setStyleSheet("color: #b03030; font-size: 12px;")
        self.reply_status.hide()
        self.main_layout.addWidget(self.reply_status)

        self.btn_ok.clicked.connect(self.on_ok)
        self.btn_cancel.clicked.connect(lambda *_: self.close_animation())
        self.btn_send.clicked.connect(self.on_send_reply)
        self.reply_input.returnPressed.connect(self.on_send_reply)

    def _build_footer(self):
        if not self.settings.notify_label:
            return
        notify_label = QLabel(self.settings.notify_label)
        notify_label.setStyleSheet(
            "font-size: 12px; color: #707070; background: none; border: none;"
        )
        self.main_layout.addWidget(notify_label)

    # ---------- 布局 ----------

    def _relayout(self):
        self._fit_content_height()
        self.bg_widget.adjustSize()
        if self.use_overlay:
            self.bg_widget.move(
                (self.width() - self.bg_widget.width()) // 2,
                (self.height() - self.bg_widget.height()) // 2,
            )
            return

        margin = self._card_margin()
        self.bg_widget.move(margin, margin)
        self.resize(self.bg_widget.width() + margin * 2, self.bg_widget.height() + margin * 2)
        screen = QApplication.primaryScreen().availableGeometry()
        self.move(
            screen.x() + (screen.width() - self.width()) // 2,
            screen.y() + (screen.height() - self.height()) // 2,
        )

    def _fit_content_height(self):
        """卡片不许超过屏幕的 82%：超了就让内容区滚动，而不是把窗口撑爆。"""
        screen = QApplication.primaryScreen().availableGeometry()
        margin = 0 if self.use_overlay else self._card_margin()
        limit = int(screen.height() * 0.82) - margin * 2

        natural = max(self.content_widget.sizeHint().height(), 60)
        self.content_area.setFixedHeight(natural)
        chrome = self.bg_widget.sizeHint().height() - natural
        allowed = max(140, limit - chrome)
        if natural > allowed:
            self.content_area.setFixedHeight(allowed)

    def _card_margin(self) -> int:
        return 30 if self.settings.notify_shadow and not self.lite_mode else 8

    def _get_font(self, family, size, weight=QFont.Normal):
        return QFont(family, size, weight)

    # ---------- 交互 ----------

    def on_ok(self):
        """确认 = 进入回复模式：下面这排按钮换成输入框 + 发送。"""
        logger.info("用户点击了确认: {}", self.data.get("Sender"))
        route = self.data.get("Reply") or {}
        if not self.settings.reply_enabled or not route.get("peer_id"):
            self.close_animation()
            return

        target = str(route.get("peer_name") or "") or self.entries[-1].get("sender", "")
        if target:
            self.reply_target_label.setText(f"回复 → {target}")
            self.reply_target_label.show()
        self.action_row.hide()
        self.reply_row.show()
        self.reply_input.setFocus()
        self.reply_input.selectAll()
        self._relayout()
        # 进入回复模式后不再自动关闭，免得打字打一半窗口没了。
        self._auto_close_requested = False
        self.duration = 0

    def on_send_reply(self):
        if self._reply_task is not None:
            return
        text = self.reply_input.text().strip()
        if not text:
            self._show_reply_error("回复内容不能为空")
            return

        route = dict(self.data.get("Reply") or {})
        if not self.settings.reply_quote_in_group:
            route["quote_seq"] = 0
        self.btn_send.setEnabled(False)
        self.btn_send.setText("发送中")
        self.reply_status.hide()

        task = ReplyTask(route, text, self)
        task.succeeded.connect(self._on_reply_sent)
        task.failed.connect(self._show_reply_error)
        task.finished.connect(self._clear_reply_task)
        self._reply_task = task
        task.start()

    def _clear_reply_task(self):
        self._reply_task = None

    def _on_reply_sent(self):
        logger.info("回复已发送: {}", self.data.get("Sender"))
        self.close_animation()

    def _show_reply_error(self, message: str):
        self.btn_send.setEnabled(True)
        self.btn_send.setText("发送")
        self.reply_status.setText(f"发送失败：{message}")
        self.reply_status.show()
        self._relayout()

    # ---------- 动画 / 生命周期 ----------

    def start_calling_effect(self):
        bpm = self.settings.calling_bpm
        duration = int(60000 / bpm)
        style = PRIORITY_STYLES.get(self.data.get("Priority", 2)) or PRIORITY_STYLES[2]
        accent_rgb = style["accent_rgb"]

        self.calling_anim = QVariantAnimation(self)
        self.calling_anim.setDuration(duration)
        self.calling_anim.setStartValue(150)
        self.calling_anim.setKeyValueAt(0.5, 255)
        self.calling_anim.setEndValue(150)
        self.calling_anim.setEasingCurve(QEasingCurve.InOutQuad)
        self.calling_anim.setLoopCount(-1)
        self.calling_anim.valueChanged.connect(
            lambda val: self._apply_card_style(f"2px solid rgba({accent_rgb}, {int(val)})")
        )
        self.calling_anim.start()

    def init_animation(self):
        self.setWindowOpacity(0)

        anim_opacity = QPropertyAnimation(self, b"windowOpacity")
        anim_opacity.setDuration(500)
        anim_opacity.setStartValue(0)
        anim_opacity.setEndValue(1)
        anim_opacity.setEasingCurve(QEasingCurve.OutExpo)
        anim_opacity.start()
        self.animations.append(anim_opacity)

        start_pos = self.bg_widget.pos()
        self.bg_widget.move(start_pos.x(), start_pos.y() + 50)
        anim_move = QPropertyAnimation(self.bg_widget, b"pos")
        anim_move.setDuration(600)
        anim_move.setStartValue(self.bg_widget.pos())
        anim_move.setEndValue(start_pos)
        anim_move.setEasingCurve(QEasingCurve.OutBack)
        anim_move.start()
        self.animations.append(anim_move)

        if self.duration > 0:
            QTimer.singleShot(self.duration, self._request_auto_close)

    def _request_auto_close(self):
        if self._closing or self.duration <= 0:
            return
        if self.reply_row.isVisible():
            return
        if self.tts_manager.is_active:
            self._auto_close_requested = True
            # 播报卡住（网络慢）也不能让窗口一直挂着，给个封顶。
            QTimer.singleShot(TTS_CLOSE_GRACE_MS, self._force_auto_close)
            return
        self.close_animation()

    def _force_auto_close(self):
        if self._closing or not self._auto_close_requested:
            return
        if self.reply_row.isVisible():
            return
        logger.debug("TTS 未在宽限期内结束，仍关闭通知")
        self.close_animation()

    def _on_tts_finished(self):
        if self._auto_close_requested and not self._closing:
            self.close_animation()

    def close_animation(self, *_args, stop_audio: bool = True):
        if self._closing:
            return

        self._closing = True
        self._auto_close_requested = False
        if stop_audio:
            self.tts_manager.stop()
            try:
                pygame.mixer.stop()
            except Exception:
                logger.exception("停止音效失败")

        if not self.use_animation:
            self.close()
            return

        anim = QPropertyAnimation(self, b"windowOpacity")
        anim.setDuration(300)
        anim.setStartValue(self.windowOpacity())
        anim.setEndValue(0)
        anim.finished.connect(self.close)
        anim.start()
        self.animations.append(anim)

    def _play_sound(self):
        try:
            if self.data.get("Calling") or self.data.get("Priority") == 0:
                sound_file = self.settings.sound_important
            else:
                sound_file = self.settings.sound_normal

            if sound_file and os.path.exists(sound_file):
                sound = pygame.mixer.Sound(sound_file)
                sound.set_volume(self.settings.playback_volume / 100.0)
                if self.data.get("Calling"):
                    sound.play(-1)
                else:
                    sound.play()
        except Exception:
            logger.exception("播放声音失败")

    def _play_tts(self):
        message = self.data.get("TTS_Text") or self.data.get("Message", "")
        if message:
            self.tts_manager.speak(message)

    def keyPressEvent(self, event):
        # 全屏遮罩 + 置顶的通知一旦卡住会很难受，留一个 Esc 出口。
        if event.key() == Qt.Key_Escape:
            self.close_animation()
            return
        super().keyPressEvent(event)

    def closeEvent(self, event):
        self.tts_manager.stop(emit_finished=False)
        super().closeEvent(event)


def show_notification(data: dict) -> NotifyWindow:
    app = QApplication.instance()
    if not app:
        app = QApplication(sys.argv)

    settings = get_settings()
    win = NotifyWindow(data)
    if settings.notify_shadow and not win.lite_mode:
        shadow = QGraphicsDropShadowEffect()
        shadow.setBlurRadius(30)
        shadow.setXOffset(0)
        shadow.setYOffset(4)
        shadow.setColor(QColor(0, 0, 0, 160))
        win.bg_widget.setGraphicsEffect(shadow)

    win.show()
    win.setWindowTitle("QQListener - 通知")
    return win
