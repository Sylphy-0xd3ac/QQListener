import os
import sys

import pygame
from loguru import logger
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
    QIcon,
    QLabel,
    QPainter,
    QPixmap,
    QPropertyAnimation,
    QSizePolicy,
    Qt,
    QTimer,
    QUrl,
    QVBoxLayout,
    QVariantAnimation,
    QWidget,
)

from src.core.settings import get_settings
from src.ui.fluent_compat import PrimaryPushButton, PushButton
from src.utils.tts import TTSManager

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


class FilePreview(QFrame):
    """文件附件预览控件"""

    def __init__(self, file_path, icon_path=None):
        super().__init__()
        self.file_path = file_path
        self.setFixedHeight(50)
        self.setCursor(Qt.PointingHandCursor)
        self.setStyleSheet("""
            #FileBox {
                background-color: #f7f7f7;
                border-radius: 6px;
                border: 1px solid #e5e5e5;
            }
            #FileBox:hover {
                background-color: #f0f6ff;
            }
            QLabel { background: transparent; border: none; }
        """)

        layout = QHBoxLayout(self)
        layout.setContentsMargins(15, 5, 15, 5)
        self.setObjectName("FileBox")

        # 图标逻辑
        self.icon_label = QLabel()
        if icon_path and os.path.exists(icon_path):
            self.icon_label.setPixmap(
                QPixmap(icon_path).scaled(24, 24, Qt.KeepAspectRatio, Qt.SmoothTransformation)
            )
        else:
            self.icon_label.setText("📄")
            self.icon_label.setStyleSheet("font-size: 18px; color: #202020;")

        # 文件名逻辑
        self.file_label = QLabel(os.path.basename(file_path))
        self.file_label.setFont(QFont("Segoe UI Variable", 11))
        self.file_label.setStyleSheet("color: #202020; background: transparent;")

        layout.addWidget(self.icon_label)
        layout.addWidget(self.file_label)
        layout.addStretch()

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            QDesktopServices.openUrl(QUrl.fromLocalFile(self.file_path))
        super().mousePressEvent(event)


class ThumbPreview(QFrame):
    def __init__(self, file_path):
        logger.debug("当前 Pic_Path: {}", file_path)
        super().__init__()
        self.file_path = file_path
        self.original_pixmap = None

        self.setCursor(Qt.PointingHandCursor)
        self.setStyleSheet("""
            #ThumbBox {
                background-color: #f7f7f7;
                border-radius: 6px;
                border: 1px solid #e5e5e5;
            }
            QLabel { background: transparent; border: none; }
        """)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(15, 5, 15, 5)
        self.setObjectName("ThumbBox")

        self.thumb_label = QLabel()
        self.thumb_label.setAlignment(Qt.AlignCenter)
        self.thumb_label.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Preferred)

        if file_path and os.path.exists(file_path):
            self.original_pixmap = QPixmap(file_path)
            self.update_pixmap()

        layout.addWidget(self.thumb_label)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self.update_pixmap()

    def update_pixmap(self):
        if not self.original_pixmap:
            return

        max_width = 440
        max_height = 300

        scaled = self.original_pixmap.scaled(
            max_width,
            max_height,
            Qt.KeepAspectRatio,
            Qt.SmoothTransformation,
        )

        self.thumb_label.setPixmap(scaled)
        self.setFixedHeight(scaled.height() + 10)

    def mousePressEvent(self, event):
        if event.button() == Qt.LeftButton:
            QDesktopServices.openUrl(QUrl.fromLocalFile(self.file_path))
        super().mousePressEvent(event)


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
        self.tts_manager = TTSManager(self)
        self.tts_manager.finished.connect(self._on_tts_finished)

        self._load_fonts()
        self.init_ui()

        if self.settings.notify_animation:
            self.init_animation()
            if data.get("Calling") and self.settings.calling_animation:
                self.start_calling_effect()
        else:
            self.setWindowOpacity(1)

        self._play_sound()
        self._play_tts()

    def _load_fonts(self):
        def load_font(path, fallback="Segoe UI"):
            if not os.path.exists(path):
                logger.warning("字体文件不存在: {}", path)
                return fallback
            font_id = QFontDatabase.addApplicationFont(path)
            if font_id != -1:
                family = QFontDatabase.applicationFontFamilies(font_id)[0]
                logger.info("字体加载成功: {}", family)
                return family
            else:
                logger.warning("字体加载失败: {}", path)
                return fallback

        self.title_family = load_font(self.settings.notify_title_font)
        self.msg_family = load_font(self.settings.notify_message_font)

    def init_ui(self):
        if self.settings.always_on_top:
            self.setWindowFlags(Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool)
        else:
            self.setWindowFlags(Qt.FramelessWindowHint)
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        screen_geo = QApplication.primaryScreen().geometry()
        self.setGeometry(screen_geo)

        style = PRIORITY_STYLES.get(self.data.get("Priority", 2)) or PRIORITY_STYLES[2]
        if style and style.get("overlay") and self.settings.notify_mask:
            self.overlay = QWidget(self)
            self.overlay.setGeometry(0, 0, self.width(), self.height())
            self.overlay.setStyleSheet(f"background-color: {style['overlay']};")

        # 消息容器（动态高度）
        self.bg_widget = QWidget(self)
        self.bg_widget.setObjectName("BgWidget")
        self.bg_widget.setFixedWidth(560)
        self.bg_widget.setStyleSheet("""
            #BgWidget {
                background-color: white;
                border-radius: 8px;
                border: 1px solid #d9d9d9;
            }
            #BgWidget QLabel {
                background: transparent;
                border: none;
            }
        """)

        self.main_layout = QVBoxLayout(self.bg_widget)
        self.main_layout.setContentsMargins(36, 32, 36, 28)
        self.main_layout.setSpacing(16)

        # 发送人
        label_sender = QLabel(f"{self.data.get('Sender', '系统通知')}")
        label_sender.setFont(
            QFont(
                self.title_family,
                18,
                QFont.Bold,
            )
        )
        label_sender.setStyleSheet("color: #111111; border: none; background: transparent;")
        self.main_layout.addWidget(label_sender)

        # 消息内容
        label_msg = QLabel(self.data.get("Message", ""))
        label_msg.setStyleSheet("color: #202020; border: none; background: transparent;")
        label_msg.setWordWrap(True)
        label_msg.setSizePolicy(QSizePolicy.Preferred, QSizePolicy.Minimum)
        label_msg.setFont(self._get_font(self.msg_family, 13))
        self.main_layout.addWidget(label_msg)

        # 文件预览
        file_path = self.data.get("file")
        if file_path and os.path.exists(file_path):
            self.file_preview = FilePreview(file_path, self.data.get("icon_file"))
            self.main_layout.addWidget(self.file_preview)
        elif file_path:
            logger.warning("附件路径未找到: {}", file_path)

        # 缩略图预览
        pic_path = self.data.get("Pic_Path")
        if pic_path and os.path.exists(pic_path):
            self.thumb_preview = ThumbPreview(pic_path)
            self.main_layout.addWidget(self.thumb_preview)
        elif pic_path:
            logger.warning("图片路径未找到: {}", pic_path)

        # 按钮组
        btn_layout = QHBoxLayout()
        btn_layout.setSpacing(12)

        self.btn_ok = self._create_button(
            self.data.get("OK_btn", "确认"), self.settings.icon_ok, primary=True
        )
        self.btn_cancel = self._create_button(
            self.data.get("Cancel_btn", "取消"), self.settings.icon_cancel
        )

        btn_layout.addWidget(self.btn_ok)
        btn_layout.addWidget(self.btn_cancel)
        self.main_layout.addLayout(btn_layout)

        # 提示文本
        if self.settings.notify_label:
            notify_label = QLabel(self.settings.notify_label)
            notify_label.setStyleSheet(
                "font-size: 12px; color: #707070; background: none; border: none;"
            )
            self.main_layout.addWidget(notify_label)

        # 让容器根据内容自动调整大小
        self.bg_widget.adjustSize()

        # 居中定位
        self.bg_widget.move(
            (self.width() - self.bg_widget.width()) // 2,
            (self.height() - self.bg_widget.height()) // 2,
        )

        self.btn_ok.clicked.connect(self.on_ok)
        self.btn_cancel.clicked.connect(lambda *_: self.close_animation())
        self.bg_widget.raise_()

    def _get_font(self, family, size, weight=QFont.Normal):
        """获取字体"""
        return QFont(family, size, weight)

    def _create_button(self, text, icon_path, primary=False):
        """创建按钮"""
        btn = PrimaryPushButton() if primary else PushButton()
        btn.setText(text)
        if icon_path and os.path.exists(icon_path):
            btn.setIcon(QIcon(icon_path) if primary else self._tinted_icon(icon_path, QColor(0, 0, 0)))
        btn.setFixedHeight(40)
        btn.setMinimumWidth(160)
        return btn

    def _tinted_icon(self, icon_path: str, color: QColor) -> QIcon:
        pixmap = QPixmap(icon_path)
        if pixmap.isNull():
            return QIcon(icon_path)

        tinted = QPixmap(pixmap.size())
        tinted.fill(Qt.transparent)

        painter = QPainter(tinted)
        painter.drawPixmap(0, 0, pixmap)
        painter.setCompositionMode(QPainter.CompositionMode_SourceIn)
        painter.fillRect(tinted.rect(), color)
        painter.end()
        return QIcon(tinted)

    def start_calling_effect(self):
        """启动呼叫动画效果"""
        bpm = self.settings.calling_bpm
        duration = int(60000 / bpm)
        style = PRIORITY_STYLES.get(self.data.get("Priority", 2)) or PRIORITY_STYLES[2]

        if not style:
            return

        accent_rgb = style["accent_rgb"]

        self.calling_anim = QVariantAnimation(self)
        self.calling_anim.setDuration(duration)
        self.calling_anim.setStartValue(150)
        self.calling_anim.setKeyValueAt(0.5, 255)
        self.calling_anim.setEndValue(150)
        self.calling_anim.setEasingCurve(QEasingCurve.InOutQuad)
        self.calling_anim.setLoopCount(-1)

        def update_bg(val):
            self.bg_widget.setStyleSheet(f"""
                #BgWidget {{
                    background-color: white;
                    border-radius: 8px;
                    border: 2px solid rgba({accent_rgb}, {int(val)});
                }}
                #BgWidget QLabel {{
                    background: transparent;
                    border: none;
                }}
            """)

        self.calling_anim.valueChanged.connect(update_bg)
        self.bg_widget.setObjectName("BgWidget")
        self.calling_anim.start()

    def init_animation(self):
        """初始化动画"""
        self.setWindowOpacity(0)

        # 淡入
        anim_opacity = QPropertyAnimation(self, b"windowOpacity")
        anim_opacity.setDuration(500)
        anim_opacity.setStartValue(0)
        anim_opacity.setEndValue(1)
        anim_opacity.setEasingCurve(QEasingCurve.OutExpo)
        anim_opacity.start()
        self.animations.append(anim_opacity)

        # 缩放/滑动效果
        start_pos = self.bg_widget.pos()
        self.bg_widget.move(start_pos.x(), start_pos.y() + 50)
        anim_move = QPropertyAnimation(self.bg_widget, b"pos")
        anim_move.setDuration(600)
        anim_move.setStartValue(self.bg_widget.pos())
        anim_move.setEndValue(start_pos)
        anim_move.setEasingCurve(QEasingCurve.OutBack)
        anim_move.start()
        self.animations.append(anim_move)

        # 自动关闭时钟
        if self.duration > 0:
            QTimer.singleShot(self.duration, self._request_auto_close)

    def _request_auto_close(self):
        if self._closing:
            return
        if self.tts_manager.is_active:
            self._auto_close_requested = True
            return
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

        anim = QPropertyAnimation(self, b"windowOpacity")
        anim.setDuration(300)
        anim.setStartValue(self.windowOpacity())
        anim.setEndValue(0)
        anim.finished.connect(self.close)
        anim.start()
        self.animations.append(anim)

    def on_ok(self):
        logger.info("用户点击了确认: {}", self.data.get("Sender"))
        self.close_animation()

    def _play_sound(self):
        try:
            if self.data.get("Calling"):
                sound_file = self.settings.sound_important
            elif self.data.get("Priority") == 0:
                sound_file = self.settings.sound_important
            else:
                sound_file = self.settings.sound_normal

            if sound_file and os.path.exists(sound_file):
                sound = pygame.mixer.Sound(sound_file)
                if self.data.get("Calling"):
                    sound.play(-1)
                else:
                    sound.play()
        except Exception:
            logger.exception("播放声音失败")

    def _play_tts(self):
        message = self.data.get("Message", "")
        if message:
            self.tts_manager.speak(message)

    def closeEvent(self, event):
        self.tts_manager.stop(emit_finished=False)
        super().closeEvent(event)


def show_notification(data: dict) -> NotifyWindow:
    app = QApplication.instance()
    if not app:
        app = QApplication(sys.argv)

    settings = get_settings()

    def apply_shadow(win):
        if settings.notify_shadow:
            shadow = QGraphicsDropShadowEffect()
            shadow.setBlurRadius(50)
            shadow.setXOffset(0)
            shadow.setYOffset(0)
            shadow.setColor(QColor(0, 0, 0, 200))
            win.bg_widget.setGraphicsEffect(shadow)

    win = NotifyWindow(data)
    apply_shadow(win)
    win.show()
    win.setWindowTitle("QQListener - 通知")

    return win
