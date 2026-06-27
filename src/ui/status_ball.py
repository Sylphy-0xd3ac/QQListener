from __future__ import annotations

from src.core.notification_state import (
    add_notification_state_listener,
    is_notifications_muted,
    remove_notification_state_listener,
    toggle_notifications_muted,
)
from src.core.resources import app_icon_path, app_icon_png_path
from src.ui.qt_compat import (
    QColor,
    QCursor,
    QPainter,
    QPen,
    QPoint,
    QRect,
    QRectF,
    Qt,
    QTimer,
    Signal,
    QWidget,
    event_global_position,
    load_icon,
    screen_at,
)


class FloatingStatusBall(QWidget):
    show_settings_requested = Signal()

    _LONG_PRESS_MS = 650
    _DRAG_DISTANCE = 5

    def __init__(self, parent: QWidget | None = None):
        super().__init__(parent)
        self.setFixedSize(50, 50)
        self.setWindowTitle("QQListener")
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)

        self._logo_icon = load_icon(app_icon_path(), app_icon_png_path())
        self._logo_rect = QRect(11, 11, 28, 28)
        self._press_global_pos: QPoint | None = None
        self._press_window_pos: QPoint | None = None
        self._dragging = False
        self._long_press_triggered = False
        self._positioned = False

        self._long_press_timer = QTimer(self)
        self._long_press_timer.setSingleShot(True)
        self._long_press_timer.timeout.connect(self._trigger_long_press)

        self._state_listener = self._on_notifications_muted_changed
        add_notification_state_listener(self._state_listener)
        self.destroyed.connect(
            lambda *_args: remove_notification_state_listener(self._state_listener)
        )

        self.setCursor(QCursor(Qt.CursorShape.PointingHandCursor))
        self.refresh_state()

    def showEvent(self, event):
        super().showEvent(event)
        if not self._positioned:
            self._move_to_default_position()
            self._positioned = True

    def paintEvent(self, event):
        del event

        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        shadow = QRectF(4, 5, self.width() - 8, self.height() - 8)
        painter.setPen(Qt.NoPen)
        painter.setBrush(QColor(0, 0, 0, 34))
        painter.drawEllipse(shadow.translated(0, 2))

        base = QRectF(4, 4, self.width() - 8, self.height() - 8)
        painter.setBrush(QColor(255, 255, 255))
        painter.setPen(QPen(QColor(214, 220, 230), 1))
        painter.drawEllipse(base)

        self._draw_logo(painter)

    def mousePressEvent(self, event):
        if event.button() == Qt.MouseButton.RightButton:
            self.show_settings_requested.emit()
            event.accept()
            return

        if event.button() != Qt.MouseButton.LeftButton:
            super().mousePressEvent(event)
            return

        self._press_global_pos = event_global_position(event)
        self._press_window_pos = self.pos()
        self._dragging = False
        self._long_press_triggered = False
        self._long_press_timer.start(self._LONG_PRESS_MS)
        event.accept()

    def mouseMoveEvent(self, event):
        if self._press_global_pos is None or self._press_window_pos is None:
            super().mouseMoveEvent(event)
            return

        global_pos = event_global_position(event)
        delta = global_pos - self._press_global_pos
        if not self._dragging and delta.manhattanLength() > self._DRAG_DISTANCE:
            self._dragging = True
            self._long_press_timer.stop()

        if self._dragging:
            self.move(self._press_window_pos + delta)
            event.accept()
            return

        event.accept()

    def mouseReleaseEvent(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            super().mouseReleaseEvent(event)
            return

        self._long_press_timer.stop()
        if not self._dragging and not self._long_press_triggered:
            toggle_notifications_muted()

        self._press_global_pos = None
        self._press_window_pos = None
        self._dragging = False
        self._long_press_triggered = False
        event.accept()

    def refresh_state(self):
        muted = is_notifications_muted()
        self.setToolTip("已暂停" if muted else "通知已启用")
        self.update()

    def _on_notifications_muted_changed(self, _muted: bool):
        self.refresh_state()

    def _draw_logo(self, painter: QPainter):
        pixmap = self._logo_icon.pixmap(self._logo_rect.size())
        if pixmap.isNull():
            painter.setPen(QPen(QColor(26, 30, 38), 1))
            painter.drawText(self._logo_rect, Qt.AlignmentFlag.AlignCenter, "Q")
        else:
            painter.drawPixmap(self._logo_rect, pixmap)

        if is_notifications_muted():
            painter.setPen(
                QPen(QColor(225, 29, 72), 5, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
            )
            painter.drawLine(
                self._logo_rect.right() - 1,
                self._logo_rect.top() + 1,
                self._logo_rect.left() + 1,
                self._logo_rect.bottom() - 1,
            )

    def _trigger_long_press(self):
        if self._press_global_pos is None or self._dragging:
            return

        self._long_press_triggered = True
        self.show_settings_requested.emit()

    def _move_to_default_position(self):
        screen = screen_at(QCursor.pos())
        if not screen:
            return

        geometry = screen.availableGeometry()
        self.move(geometry.right() - self.width() - 24, geometry.top() + 96)
