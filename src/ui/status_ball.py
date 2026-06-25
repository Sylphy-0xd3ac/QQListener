from __future__ import annotations

from src.core.notification_state import is_notifications_muted, toggle_notifications_muted
from src.core.resources import app_icon_path, app_icon_png_path, resource_path
from src.ui.qt_compat import (
    QColor,
    QCursor,
    QPainter,
    QPainterPath,
    QPoint,
    QPen,
    QRect,
    QRectF,
    QSize,
    Qt,
    Signal,
    QWidget,
    event_global_position,
    event_position,
    load_icon,
    screen_at,
)


class FloatingStatusBall(QWidget):
    show_settings_requested = Signal()

    def __init__(self, parent: QWidget | None = None):
        super().__init__(parent)
        self.setFixedSize(88, 54)
        self.setWindowTitle("QQListener")
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating)

        self._logo_icon = load_icon(app_icon_path(), app_icon_png_path())
        self._settings_icon = load_icon(resource_path("asset", "settings.svg"))
        self._logo_rect = QRect(3, 3, 48, 48)
        self._logo_icon_rect = QRect(12, 12, 30, 30)
        self._settings_rect = QRect(60, 18, 18, 18)
        self._settings_hit_rect = QRect(50, 8, 34, 38)
        self._press_global_pos: QPoint | None = None
        self._press_window_pos: QPoint | None = None
        self._pressed_action = ""
        self._dragging = False
        self._positioned = False

        self._update_tooltip()

    def showEvent(self, event):
        super().showEvent(event)
        if not self._positioned:
            self._move_to_default_position()
            self._positioned = True

    def paintEvent(self, event):
        del event

        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        base_path = self._base_path()
        shadow_path = QPainterPath(base_path)
        shadow_path.translate(0, 2)
        painter.fillPath(shadow_path, QColor(0, 0, 0, 34))

        painter.fillPath(base_path, QColor(255, 255, 255))
        painter.setPen(QPen(QColor(215, 220, 230), 1))
        painter.drawPath(base_path)

        self._draw_logo(painter)
        self._draw_settings_icon(painter)

    def mousePressEvent(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            super().mousePressEvent(event)
            return

        self._press_global_pos = self._event_global_pos(event)
        self._press_window_pos = self.pos()
        self._pressed_action = self._action_at(event_position(event))
        self._dragging = False
        event.accept()

    def mouseMoveEvent(self, event):
        pos = event_position(event)
        if self._press_global_pos is None or self._press_window_pos is None:
            self._update_cursor(pos)
            super().mouseMoveEvent(event)
            return

        global_pos = self._event_global_pos(event)
        delta = global_pos - self._press_global_pos
        if not self._dragging and delta.manhattanLength() > 4:
            self._dragging = True

        if self._dragging:
            self.move(self._press_window_pos + delta)
            event.accept()
            return

        self._update_cursor(pos)
        event.accept()

    def mouseReleaseEvent(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            super().mouseReleaseEvent(event)
            return

        released_action = self._action_at(event_position(event))
        if not self._dragging and self._pressed_action == released_action:
            if released_action == "settings":
                self.show_settings_requested.emit()
            elif released_action == "logo":
                toggle_notifications_muted()
                self._update_tooltip()
                self.update()

        self._press_global_pos = None
        self._press_window_pos = None
        self._pressed_action = ""
        self._dragging = False
        self._update_cursor(event_position(event))
        event.accept()

    def leaveEvent(self, event):
        if self._press_global_pos is None:
            self.unsetCursor()
        super().leaveEvent(event)

    def _base_path(self) -> QPainterPath:
        logo_path = QPainterPath()
        logo_path.addEllipse(QRectF(self._logo_rect))

        body_path = QPainterPath()
        body_path.addRoundedRect(QRectF(30, 9, 52, 36), 18, 18)

        return logo_path.united(body_path)

    def _draw_logo(self, painter: QPainter):
        pixmap = self._logo_icon.pixmap(self._logo_icon_rect.size())
        if pixmap.isNull():
            painter.setPen(QPen(QColor(26, 30, 38), 1))
            painter.drawText(self._logo_rect, Qt.AlignmentFlag.AlignCenter, "Q")
        else:
            painter.drawPixmap(self._logo_icon_rect, pixmap)

        if is_notifications_muted():
            painter.setPen(
                QPen(QColor(225, 29, 72), 5, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
            )
            painter.drawLine(43, 10, 11, 43)

    def _draw_settings_icon(self, painter: QPainter):
        icon_rect = QRect(self._settings_rect.translated(-8, 0))
        pixmap = self._settings_icon.pixmap(icon_rect.size())
        if pixmap.isNull():
            painter.setPen(
                QPen(QColor(76, 86, 106), 2, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap)
            )
            painter.drawEllipse(icon_rect.adjusted(4, 4, -4, -4))
            painter.drawPoint(icon_rect.center())
        else:
            painter.drawPixmap(icon_rect, pixmap)

    def _action_at(self, pos: QPoint) -> str:
        if self._settings_hit_rect.contains(pos):
            return "settings"
        if self._logo_rect.contains(pos):
            return "logo"
        if self._base_path().contains(pos):
            return "drag"
        return ""

    def _update_cursor(self, pos: QPoint):
        action = self._action_at(pos)
        if action in {"settings", "logo"}:
            self.setCursor(QCursor(Qt.CursorShape.PointingHandCursor))
        elif action == "drag":
            self.setCursor(QCursor(Qt.CursorShape.OpenHandCursor))
        else:
            self.unsetCursor()

    def _update_tooltip(self):
        self.setToolTip("通知已静音" if is_notifications_muted() else "通知正常")

    def _move_to_default_position(self):
        screen = screen_at(QCursor.pos())
        if not screen:
            return

        geometry = screen.availableGeometry()
        self.move(geometry.right() - self.width() - 24, geometry.top() + 96)

    @staticmethod
    def _event_global_pos(event) -> QPoint:
        return event_global_position(event)
