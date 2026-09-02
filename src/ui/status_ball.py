from __future__ import annotations

from src.core.core_controller import (
    CoreState,
    add_core_state_listener,
    get_core_state,
    remove_core_state_listener,
    toggle_core,
)
from src.core.core_runtime import CoreRuntimeState, get_core_runtime
from src.core.resources import app_icon_path, app_icon_png_path
from src.core.settings import get_settings
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
    QWidget,
    Signal,
    event_global_position,
    load_icon,
    screen_at,
)


class FloatingStatusBall(QWidget):
    show_settings_requested = Signal()
    unload_requested = Signal()

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
        self._visual_key: tuple | None = None
        self._runtime_timer = QTimer(self)
        self._runtime_timer.timeout.connect(self.refresh_state)
        # 悬浮球是常驻的半透明置顶窗口，每次重绘都要重新合成一遍。
        # 所以只轮询状态，状态没变就不重绘。
        self._runtime_timer.start(1000 if get_settings().lite_mode else 500)

        self._state_listener = self._on_core_state_changed
        add_core_state_listener(self._state_listener)
        self.destroyed.connect(lambda *_args: remove_core_state_listener(self._state_listener))

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
        painter.setPen(QPen(self._runtime_ring_color(), 2.5))
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
            toggle_core()

        self._press_global_pos = None
        self._press_window_pos = None
        self._dragging = False
        self._long_press_triggered = False
        event.accept()

    _STATE_TOOLTIP = {
        CoreState.RUNNING: "核心运行中（单击暂停，长按卸载）",
        CoreState.PAUSED: "核心已暂停（单击恢复，长按卸载）",
        CoreState.DETACHED: "核心未启动（单击启动）",
    }
    # 暂停 = 琥珀，未注入 = 灰
    _STATE_SLASH_COLOR = {
        CoreState.PAUSED: QColor(217, 119, 6),
        CoreState.DETACHED: QColor(148, 163, 184),
    }

    def refresh_state(self):
        state = get_core_state()
        snapshot = get_core_runtime()
        if state == CoreState.RUNNING:
            tooltip = {
                CoreRuntimeState.CONNECTED: "接收管道已连接（单击暂停，长按卸载）",
                CoreRuntimeState.WAITING: "正在等待接收管道（单击暂停，长按卸载）",
                CoreRuntimeState.NO_QQ: "未找到 QQ 主进程（单击暂停，长按卸载）",
                CoreRuntimeState.ERROR: "核心运行异常（单击暂停，长按卸载）",
                CoreRuntimeState.UNSUPPORTED: "当前平台不支持核心注入",
            }.get(snapshot.state, snapshot.detail or "QQListener")
        else:
            tooltip = self._STATE_TOOLTIP.get(state, "QQListener")

        visual_key = (state, snapshot.state, tooltip)
        if visual_key == self._visual_key:
            return
        self._visual_key = visual_key
        self.setToolTip(tooltip)
        self.update()

    def _runtime_ring_color(self) -> QColor:
        state = get_core_state()
        if state == CoreState.PAUSED:
            return QColor(217, 119, 6)
        if state == CoreState.DETACHED:
            return QColor(148, 163, 184)
        runtime_state = get_core_runtime().state
        if runtime_state == CoreRuntimeState.CONNECTED:
            return QColor(0, 153, 153)
        if runtime_state == CoreRuntimeState.ERROR:
            return QColor(196, 43, 28)
        if runtime_state in {CoreRuntimeState.WAITING, CoreRuntimeState.NO_QQ}:
            return QColor(217, 119, 6)
        return QColor(148, 163, 184)

    def _on_core_state_changed(self, _state: CoreState):
        self.refresh_state()

    def _draw_logo(self, painter: QPainter):
        pixmap = self._logo_icon.pixmap(self._logo_rect.size())
        if pixmap.isNull():
            painter.setPen(QPen(QColor(26, 30, 38), 1))
            painter.drawText(self._logo_rect, Qt.AlignmentFlag.AlignCenter, "Q")
        else:
            painter.drawPixmap(self._logo_rect, pixmap)

        slash_color = self._STATE_SLASH_COLOR.get(get_core_state())
        if slash_color is not None:
            painter.setPen(QPen(slash_color, 5, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
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
        self.unload_requested.emit()

    def _move_to_default_position(self):
        screen = screen_at(QCursor.pos())
        if not screen:
            return

        geometry = screen.availableGeometry()
        self.move(geometry.right() - self.width() - 24, geometry.top() + 96)
