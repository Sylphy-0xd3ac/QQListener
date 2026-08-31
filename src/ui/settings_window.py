# ruff: noqa: E402

import os
import subprocess
import sys
import time
import webbrowser

import pygame
from loguru import logger

from src.ui.qt_compat import (
    QApplication,
    QColor,
    QFileDialog,
    QFormLayout,
    QFrame,
    QHBoxLayout,
    QLabel,
    QPainter,
    QRectF,
    QSize,
    QStackedWidget,
    Qt,
    QThread,
    QTimer,
    QTranslator,
    QVBoxLayout,
    QWidget,
    Signal,
)


def _patch_macos_frameless_window() -> None:
    if sys.platform != "darwin":
        return

    try:
        import qframelesswindow.mac as mac
    except Exception:
        logger.exception("加载 macOS 无边框窗口兼容补丁失败")
        return

    # qframelesswindow 0.8.1 uses PyObjC to rewrite NSWindow in __init__, which can
    # segfault with Qt 6.10. Keep FluentWindow frameless via Qt flags instead.
    def init_frameless(self):
        self.windowEffect = mac.MacWindowEffect(self)
        self.titleBar = mac.TitleBar(self)
        self._isResizeEnabled = True
        self._isSystemButtonVisible = False
        self.setWindowFlags(self.windowFlags() | Qt.FramelessWindowHint)

        safe_area_attribute = getattr(Qt, "WA_ContentsMarginsRespectsSafeArea", None)
        layout_attribute = getattr(Qt, "WA_LayoutOnEntireRect", None)
        qt_version = getattr(mac, "QT_VERSION", (0, 0, 0))
        if qt_version >= (6, 8, 0) and safe_area_attribute and layout_attribute:
            self.setAttribute(safe_area_attribute, False)
            self.titleBar.setAttribute(layout_attribute, True)

        self.resize(500, 500)
        self.titleBar.raise_()

    def update_frameless(self):
        return None

    def set_system_title_bar_button_visible(self, is_visible):
        self._isSystemButtonVisible = False

    window_base = getattr(mac, "MacFramelessWindowBase", None) or getattr(
        mac, "MacFramelessWindow", None
    )
    if window_base is None:
        logger.warning("未找到可补丁的 macOS 无边框窗口类")
        return

    window_base._initFrameless = init_frameless
    window_base.updateFrameless = update_frameless
    window_base._hideSystemTitleBar = lambda self: None
    window_base._extendTitleBarToClientArea = lambda self: None
    window_base._updateSystemTitleBar = lambda self: None
    window_base._updateSystemButtonRect = lambda self: None
    window_base.isSystemButtonVisible = lambda self: False
    window_base.setSystemTitleBarButtonVisible = set_system_title_bar_button_visible


_patch_macos_frameless_window()

from src.core.autostart import (
    is_auto_start_enabled,
    is_auto_start_supported,
    set_auto_start_enabled,
)
from src.core.core_controller import (
    CoreState,
    add_core_state_listener,
    get_core_state,
    remove_core_state_listener,
    toggle_core,
    unload_core,
)
from src.core.core_runtime import CoreRuntimeState, get_core_runtime
from src.core.core_updater import (
    check_update,
    current_core_version,
    download_and_install,
    is_core_installed,
)
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.ui.fluent_compat import (
    CaptionLabel,
    CardWidget,
    CheckBox,
    ComboBox,
    EditableComboBox,
    FluentWindow,
    IconInfoBadge,
    IconWidget,
    LineEdit,
    ListWidget,
    NavigationItemPosition,
    PrimaryPushButton,
    PushButton,
    ScrollArea,
    SegmentedWidget,
    SimpleCardWidget,
    Slider,
    SpinBox,
    StrongBodyLabel,
    SubtitleLabel,
    SwitchButton,
    TitleLabel,
)
from src.ui.fluent_compat import (
    FluentIcon as FIF,
)
from src.ui.fluent_dialog import show_fluent_message
from src.utils.tts import set_system_volume_max


class _CoreUpdateThread(QThread):
    """后台执行核心检查/安装，避免阻塞 UI。"""

    checked = Signal(object)  # UpdateStatus
    installed = Signal(str)  # 已装版本
    failed = Signal(str)

    def __init__(self, mode: str, proxy: str | None = None, parent=None):
        super().__init__(parent)
        self._mode = mode
        self._proxy = proxy

    def run(self):
        try:
            if self._mode == "check":
                self.checked.emit(check_update())
            else:
                self.installed.emit(download_and_install(proxy=self._proxy))
        except Exception as exc:  # noqa: BLE001 — 面向用户的错误消息
            self.failed.emit(str(exc))


class SettingsWindow(FluentWindow):
    """设置窗口"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.settings = get_settings()
        self.signals = get_signals()

        self.setWindowTitle(self.tr("QQ Listener - 设置"))
        self.resize(860, 640)
        self.setMinimumSize(760, 540)
        self._corner_radius = 14
        self.setAttribute(Qt.WA_TranslucentBackground, True)
        self.setAutoFillBackground(False)

        self.data = self.settings.get_all()
        self._debug_click_count = 0
        self._debug_page_unlocked = False
        self.init_ui()
        self._core_state_listener = self._on_core_state_changed
        add_core_state_listener(self._core_state_listener)
        self.destroyed.connect(lambda *_args: remove_core_state_listener(self._core_state_listener))
        self._badge_long_press_timer = QTimer(self)
        self._badge_long_press_timer.setSingleShot(True)
        self._badge_long_press_timer.timeout.connect(self._on_badge_long_press)
        self._badge_long_pressed = False
        self._polish_window_chrome()

    def resizeEvent(self, event):
        super().resizeEvent(event)

    def changeEvent(self, event):
        super().changeEvent(event)

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.setPen(Qt.NoPen)
        painter.setBrush(QColor(240, 244, 249))

        rect = QRectF(self.rect()).adjusted(0.5, 0.5, -0.5, -0.5)
        if self.isMaximized() or self.isFullScreen():
            painter.drawRect(rect)
        else:
            radius = getattr(self, "_corner_radius", 14)
            painter.drawRoundedRect(rect, radius, radius)

    def _polish_window_chrome(self):
        for widget in (
            self,
            getattr(self, "titleBar", None),
            getattr(self, "navigationInterface", None),
            getattr(self, "stackedWidget", None),
            getattr(self, "home_interface", None),
            getattr(self, "settings_interface", None),
        ):
            if widget is not None:
                widget.setAttribute(Qt.WA_TranslucentBackground, True)
                widget.setAutoFillBackground(False)

        if hasattr(self, "stackedWidget"):
            self.stackedWidget.setStyleSheet("""
                StackedWidget {
                    background: transparent;
                    border: none;
                }
            """)

        if hasattr(self, "titleBar"):
            self.titleBar.setStyleSheet("""
                FluentTitleBar {
                    background: transparent;
                    border: none;
                }
            """)
            for button in (
                getattr(self.titleBar, "minBtn", None),
                getattr(self.titleBar, "maxBtn", None),
                getattr(self.titleBar, "closeBtn", None),
            ):
                if button is None:
                    continue
                button.setNormalBackgroundColor(QColor(0, 0, 0, 0))
                button.setHoverBackgroundColor(QColor(0, 0, 0, 18))
                button.setPressedBackgroundColor(QColor(0, 0, 0, 34))

            close_btn = getattr(self.titleBar, "closeBtn", None)
            if close_btn is not None:
                close_btn.setHoverColor(QColor(0, 0, 0))
                close_btn.setPressedColor(QColor(0, 0, 0))

    def init_ui(self):
        """初始化UI"""
        self.home_interface = self._create_home_interface()
        self.settings_interface = self._create_settings_interface()

        self.addSubInterface(self.home_interface, FIF.HOME, self.tr("主页"))
        self.addSubInterface(
            self.settings_interface,
            FIF.SETTING,
            self.tr("设置"),
            position=NavigationItemPosition.BOTTOM,
        )
        self.navigationInterface.addItem(
            "exitInterface",
            FIF.POWER_BUTTON,
            self.tr("退出"),
            onClick=lambda *_: self.signals.exit_app.emit(),
            selectable=False,
            position=NavigationItemPosition.BOTTOM,
        )

    def _line_edit(self, text="") -> LineEdit:
        edit = LineEdit()
        edit.setText(str(text) if text is not None else "")
        return edit

    def _section_label(self, text: str) -> QLabel:
        label = QLabel(text)
        label.setStyleSheet(
            "color: #202020; font-size: 14px; font-weight: 600; background: transparent;"
        )
        return label

    def _create_home_interface(self) -> QWidget:
        page = QWidget()
        page.setObjectName("homeInterface")
        layout = QVBoxLayout(page)
        layout.setContentsMargins(42, 34, 42, 34)
        layout.setSpacing(18)

        user_qq = self.data.get("User_QQ", "") or self.tr("未填写")

        header = QHBoxLayout()
        header_text = QVBoxLayout()
        header_text.setSpacing(2)
        header_text.addWidget(TitleLabel(self.tr("QQ Listener")))
        header_text.addWidget(CaptionLabel(self.tr("原生 QQ 消息监听与重要通知")))
        header.addLayout(header_text)
        header.addStretch()
        layout.addLayout(header)

        hero = CardWidget(page)
        hero.setMinimumHeight(154)
        hero_layout = QHBoxLayout(hero)
        hero_layout.setContentsMargins(24, 22, 24, 22)
        hero_layout.setSpacing(18)

        self.home_status_badge = None
        self.home_status_badge_state = None
        self.home_status_badge_box = QWidget(hero)
        self.home_status_badge_box.setFixedSize(52, 52)
        self.home_status_badge_box.setCursor(Qt.CursorShape.PointingHandCursor)
        self.home_status_badge_box.mousePressEvent = self._on_badge_pressed
        self.home_status_badge_box.mouseReleaseEvent = self._on_badge_released
        hero_layout.addWidget(self.home_status_badge_box, alignment=Qt.AlignVCenter)

        status_text_layout = QVBoxLayout()
        status_text_layout.setSpacing(4)
        self.home_status_title = SubtitleLabel(self.tr("正在运行"))
        self.home_status_detail = CaptionLabel(self.tr("QQ {qq}").format(qq=user_qq))
        self.home_status_detail.setStyleSheet("color: #707070;")
        self.home_status_detail.setWordWrap(True)
        status_text_layout.addWidget(self.home_status_title)
        status_text_layout.addWidget(self.home_status_detail)
        hero_layout.addLayout(status_text_layout, 1)

        hero_actions = QVBoxLayout()
        hero_actions.setSpacing(8)
        open_settings = PrimaryPushButton(self.tr("打开设置"))
        open_settings.setIcon(FIF.SETTING)
        open_settings.clicked.connect(lambda *_: self._show_settings_interface())
        test_notification = PushButton(self.tr("测试通知"))
        test_notification.setIcon(FIF.MESSAGE)
        test_notification.clicked.connect(self._test_notify)
        hero_actions.addWidget(open_settings)
        hero_actions.addWidget(test_notification)
        hero_layout.addLayout(hero_actions)
        layout.addWidget(hero)

        stats = QHBoxLayout()
        stats.setSpacing(14)
        version_card, self.home_version_value = self._dashboard_stat_card(
            FIF.INFO, self.tr("核心版本"), self._core_version_text()
        )
        people_card, self.home_important_value = self._dashboard_stat_card(
            FIF.PEOPLE,
            self.tr("重要人物 QQ"),
            str(len(self.data.get("Important_Person_QQs", self.settings.important_person_qqs))),
        )
        rules_card, self.home_rules_value = self._dashboard_stat_card(
            FIF.CHECKBOX, self.tr("名单规则"), self._home_rules_text()
        )
        stats.addWidget(version_card, 1)
        stats.addWidget(people_card, 1)
        stats.addWidget(rules_card, 1)
        layout.addLayout(stats)
        layout.addStretch()
        self._refresh_notification_status()
        return page

    def _dashboard_stat_card(self, icon, title: str, value: str):
        card = SimpleCardWidget()
        card.setMinimumHeight(112)
        card_layout = QHBoxLayout(card)
        card_layout.setContentsMargins(18, 16, 18, 16)
        card_layout.setSpacing(12)
        icon_widget = IconWidget(card)
        icon_widget.setIcon(icon)
        icon_widget.setFixedSize(30, 30)
        card_layout.addWidget(icon_widget)
        text_layout = QVBoxLayout()
        text_layout.setSpacing(3)
        text_layout.addWidget(CaptionLabel(title))
        value_label = StrongBodyLabel(value)
        text_layout.addWidget(value_label)
        card_layout.addLayout(text_layout, 1)
        return card, value_label

    def _home_rules_text(self) -> str:
        names = []
        if self.data.get("Whitelist_Enabled", self.settings.whitelist_enabled):
            names.append(self.tr("白名单"))
        if self.data.get("Blacklist_Enabled", self.settings.blacklist_enabled):
            names.append(self.tr("黑名单"))
        return " + ".join(names) if names else self.tr("未启用")

    def _show_settings_interface(self):
        self.switchTo(self.settings_interface)

    def _show_home_interface(self):
        self.switchTo(self.home_interface)

    def _create_settings_interface(self) -> QWidget:
        page = QWidget()
        page.setObjectName("settingsInterface")

        layout = QVBoxLayout(page)
        layout.setContentsMargins(32, 28, 32, 24)
        layout.setSpacing(16)

        layout.addWidget(TitleLabel(self.tr("设置")))
        settings_hint = CaptionLabel(self.tr("监听、规则、通知与核心管理"))
        settings_hint.setStyleSheet("color: #707070;")
        layout.addWidget(settings_hint)

        self.settings_pivot = SegmentedWidget(page)
        self.settings_pivot.currentItemChanged.connect(self._on_settings_pivot_changed)
        layout.addWidget(self.settings_pivot)

        self.settings_stack = QStackedWidget(page)
        self.settings_stack.setObjectName("settingsStack")
        self.settings_stack.setStyleSheet(
            "QStackedWidget#settingsStack { background: transparent; }"
        )
        layout.addWidget(self.settings_stack, 1)

        self._settings_pages = {}
        self._settings_page_meta = {}
        self._settings_pivot_routes = []
        for route_key, title, icon, content in [
            ("basic", self.tr("基本"), FIF.HOME, self._create_basic_tab()),
            ("core", self.tr("核心"), FIF.IOT, self._create_core_tab()),
            ("rule", self.tr("规则"), FIF.CHECKBOX, self._create_rule_tab()),
            ("appearance", self.tr("外观"), FIF.PALETTE, self._create_appearance_tab()),
            ("notify", self.tr("通知"), FIF.MESSAGE, self._create_notify_tab()),
            ("calling", self.tr("呼叫"), FIF.PHONE, self._create_calling_tab()),
            ("sound", self.tr("声音"), FIF.MUSIC, self._create_sound_tab()),
            ("debug", self.tr("调试"), FIF.CODE, self._create_debug_tab()),
            ("about", self.tr("关于"), FIF.INFO, self._create_about_tab()),
        ]:
            self._add_settings_pivot_page(
                route_key,
                title,
                icon,
                content,
                visible=route_key != "debug",
            )

        self._switch_settings_page("basic")

        action_layout = QHBoxLayout()
        action_layout.addStretch()
        btn_test = PushButton(self.tr("测试弹窗"))
        btn_save = PrimaryPushButton(self.tr("保存设置"))
        btn_test.setIcon(FIF.MESSAGE)
        btn_save.setIcon(FIF.SAVE)
        btn_test.setFixedHeight(38)
        btn_save.setFixedHeight(38)
        btn_test.clicked.connect(self._test_notify)
        btn_save.clicked.connect(self.save_settings)
        action_layout.addWidget(btn_test)
        action_layout.addWidget(btn_save)
        layout.addLayout(action_layout)
        return page

    def _add_settings_pivot_page(
        self,
        route_key: str,
        title: str,
        icon: FIF,
        content: QWidget,
        visible: bool = True,
    ):
        content.setObjectName(f"{route_key}Content")
        page_content = content
        if route_key != "core":
            surface = SimpleCardWidget()
            surface_layout = QVBoxLayout(surface)
            surface_layout.setContentsMargins(20, 18, 20, 18)
            surface_layout.addWidget(content)
            host = QWidget()
            host_layout = QVBoxLayout(host)
            host_layout.setContentsMargins(4, 4, 8, 12)
            host_layout.addWidget(surface)
            host_layout.addStretch()
            page_content = host
        scroll_area = ScrollArea()
        scroll_area.setObjectName(f"{route_key}ScrollArea")
        scroll_area.setWidgetResizable(True)
        scroll_area.setFrameShape(QFrame.NoFrame)
        scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        scroll_area.setWidget(page_content)
        scroll_area.setStyleSheet("""
            ScrollArea {
                background: transparent;
                border: none;
            }
            ScrollArea > QWidget > QWidget {
                background: transparent;
            }
        """)
        self.settings_stack.addWidget(scroll_area)
        self._settings_pages[route_key] = scroll_area
        self._settings_page_meta[route_key] = (title, icon)
        if visible:
            self._add_settings_pivot_item(route_key, title, icon)

    def _add_settings_pivot_item(
        self,
        route_key: str,
        title: str,
        icon: FIF,
        insert_index: int | None = None,
    ):
        if route_key in self._settings_pivot_routes:
            return

        if insert_index is None or insert_index >= len(self._settings_pivot_routes):
            self.settings_pivot.addItem(route_key, title, icon=icon)
            self._settings_pivot_routes.append(route_key)
            return

        self.settings_pivot.insertItem(insert_index, route_key, title, icon=icon)
        self._settings_pivot_routes.insert(insert_index, route_key)

    def _switch_settings_page(self, route_key: str):
        self.settings_pivot.setCurrentItem(route_key)
        self._on_settings_pivot_changed(route_key)

    def _on_settings_pivot_changed(self, route_key: str):
        page = self._settings_pages.get(route_key)
        if page is None:
            return
        self.settings_stack.setCurrentWidget(page)

    def refresh_home(self):
        self.data = self.settings.get_all()
        if not hasattr(self, "home_status_detail"):
            return

        user_qq = self.data.get("User_QQ", "") or self.tr("未填写")
        self.home_status_detail.setText(self.tr("QQ {qq}").format(qq=user_qq))
        if hasattr(self, "home_version_value"):
            self.home_version_value.setText(self._core_version_text())
            self.home_important_value.setText(
                str(len(self.data.get("Important_Person_QQs", self.settings.important_person_qqs)))
            )
            self.home_rules_value.setText(self._home_rules_text())
        self._refresh_notification_status()

    _CORE_STATE_TITLE = {
        CoreState.RUNNING: "正在运行",
        CoreState.PAUSED: "已暂停",
        CoreState.DETACHED: "核心未启动",
    }

    def _refresh_notification_status(self, state: CoreState | None = None):
        if not hasattr(self, "home_status_title"):
            return

        state = get_core_state() if state is None else state
        if state == CoreState.RUNNING:
            title, detail, _color = self._runtime_display()
            self.home_status_title.setText(title)
            user_qq = self.data.get("User_QQ", "") or self.tr("未填写")
            self.home_status_detail.setText(
                self.tr("QQ {qq} · {detail}").format(qq=user_qq, detail=detail)
            )
        else:
            self.home_status_title.setText(self.tr(self._CORE_STATE_TITLE.get(state, "正在运行")))
        self._refresh_home_status_badge(state)

    def _refresh_home_status_badge(self, state: CoreState):
        if not hasattr(self, "home_status_badge_box"):
            return

        runtime_state = get_core_runtime().state if state == CoreState.RUNNING else None
        badge_state = (state, runtime_state)
        if self.home_status_badge is not None and self.home_status_badge_state == badge_state:
            return

        if self.home_status_badge is not None:
            self.home_status_badge.setParent(None)
            self.home_status_badge.deleteLater()

        if state == CoreState.RUNNING and runtime_state == CoreRuntimeState.CONNECTED:
            self.home_status_badge = IconInfoBadge.success(
                FIF.ACCEPT_MEDIUM, self.home_status_badge_box
            )
            tooltip = self.tr("接收管道已连接（单击暂停，长按卸载）")
        elif state == CoreState.RUNNING and runtime_state == CoreRuntimeState.ERROR:
            self.home_status_badge = IconInfoBadge.error(FIF.CLOSE, self.home_status_badge_box)
            tooltip = self.tr("核心运行异常（单击暂停，长按卸载）")
        elif state == CoreState.RUNNING:
            self.home_status_badge = IconInfoBadge.warning(FIF.SYNC, self.home_status_badge_box)
            tooltip = self.tr("正在等待 QQ 或接收管道（单击暂停，长按卸载）")
        elif state == CoreState.PAUSED:
            self.home_status_badge = IconInfoBadge.warning(FIF.PAUSE, self.home_status_badge_box)
            tooltip = self.tr("核心已暂停（单击恢复，长按卸载）")
        else:
            self.home_status_badge = IconInfoBadge.error(FIF.CLOSE, self.home_status_badge_box)
            tooltip = self.tr("核心未启动（单击启动）")

        self.home_status_badge_state = badge_state
        self.home_status_badge.setFixedSize(36, 36)
        self.home_status_badge.setIconSize(QSize(18, 18))
        self.home_status_badge.move(4, 4)
        self.home_status_badge.setCursor(Qt.CursorShape.PointingHandCursor)
        self.home_status_badge.mousePressEvent = self._on_badge_pressed
        self.home_status_badge.mouseReleaseEvent = self._on_badge_released
        self.home_status_badge.setToolTip(tooltip)
        self.home_status_badge.show()

    def _on_badge_pressed(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            return
        self._badge_long_pressed = False
        self._badge_long_press_timer.start(650)
        event.accept()

    def _on_badge_released(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            return
        self._badge_long_press_timer.stop()
        if not self._badge_long_pressed:
            toggle_core()
        event.accept()

    def _on_badge_long_press(self):
        self._badge_long_pressed = True
        confirmed = show_fluent_message(
            self,
            self.tr("卸载核心"),
            self.tr("将从 QQ 进程卸载注入的钩子，需重新启动核心才能恢复监听。确定卸载？"),
            yes_text=self.tr("卸载"),
            cancel_text=self.tr("取消"),
        )
        if confirmed:
            unload_core()

    def _on_core_state_changed(self, state: CoreState):
        self._refresh_notification_status(state)
        if hasattr(self, "core_status_value"):
            self.core_status_value.setText(self.tr(self._CORE_STATE_TITLE.get(state, "正在运行")))
        self._refresh_core_actions()
        self._refresh_core_runtime_status()

    def _create_core_tab(self):
        """核心管理页：明确区分映像映射与接收管道真正连通。"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        layout.setContentsMargins(4, 4, 4, 16)
        layout.setSpacing(16)

        layout.addWidget(SubtitleLabel(self.tr("SnowLuma 核心")))
        subtitle = CaptionLabel(
            self.tr("只有“接收管道已连接”才表示监听可用；核心映像映射完成不等于能收到消息。")
        )
        subtitle.setWordWrap(True)
        subtitle.setStyleSheet("color: #666666;")
        layout.addWidget(subtitle)

        status_card = QFrame()
        status_card.setObjectName("CoreStatusCard")
        status_card.setStyleSheet("""
            QFrame#CoreStatusCard {
                background: rgba(255, 255, 255, 220);
                border: 1px solid #dfe5ec;
                border-radius: 12px;
            }
            QFrame#CoreStatusCard QLabel {
                color: #202020;
                background: transparent;
            }
        """)
        status_layout = QVBoxLayout(status_card)
        status_layout.setContentsMargins(20, 18, 20, 18)
        status_layout.setSpacing(9)

        status_header = QHBoxLayout()
        self.core_runtime_dot = QLabel("●")
        self.core_runtime_title = SubtitleLabel(self.tr("正在检查运行状态"))
        status_header.addWidget(self.core_runtime_dot)
        status_header.addWidget(self.core_runtime_title)
        status_header.addStretch()
        status_layout.addLayout(status_header)

        self.core_runtime_detail = CaptionLabel("")
        self.core_runtime_detail.setWordWrap(True)
        self.core_runtime_detail.setStyleSheet("color: #666666;")
        status_layout.addWidget(self.core_runtime_detail)

        metadata = QHBoxLayout()
        metadata.addWidget(QLabel(self.tr("控制状态")))

        self.core_status_value = CaptionLabel(
            self.tr(self._CORE_STATE_TITLE.get(get_core_state(), "正在运行"))
        )
        metadata.addWidget(self.core_status_value)
        metadata.addSpacing(28)
        metadata.addWidget(QLabel(self.tr("已安装版本")))
        self.core_version_value = CaptionLabel(self._core_version_text())
        metadata.addWidget(self.core_version_value)
        metadata.addStretch()
        status_layout.addLayout(metadata)

        actions = QHBoxLayout()
        self.core_toggle_btn = PrimaryPushButton()
        self.core_toggle_btn.clicked.connect(lambda *_: toggle_core())
        self.core_unload_btn = PushButton(self.tr("卸载核心"))
        self.core_unload_btn.setIcon(FIF.DELETE)
        self.core_unload_btn.clicked.connect(self._request_core_unload)
        actions.addWidget(self.core_toggle_btn)
        actions.addWidget(self.core_unload_btn)
        actions.addStretch()
        status_layout.addLayout(actions)
        layout.addWidget(status_card)

        update_card = QFrame()
        update_card.setObjectName("CoreUpdateCard")
        update_card.setStyleSheet("""
            QFrame#CoreUpdateCard {
                background: rgba(255, 255, 255, 220);
                border: 1px solid #dfe5ec;
                border-radius: 12px;
            }
            QFrame#CoreUpdateCard QLabel {
                color: #202020;
                background: transparent;
            }
        """)
        update_layout = QVBoxLayout(update_card)
        update_layout.setContentsMargins(20, 18, 20, 18)
        update_layout.setSpacing(10)
        update_layout.addWidget(SubtitleLabel(self.tr("核心更新")))
        update_hint = CaptionLabel(
            self.tr("从 SnowLuma 官方 release 获取专有核心，本程序不随包分发该组件。")
        )
        update_hint.setWordWrap(True)
        update_hint.setStyleSheet("color: #666666;")
        update_layout.addWidget(update_hint)

        proxy_row = QHBoxLayout()
        proxy_row.addWidget(QLabel(self.tr("下载代理")))
        self.core_proxy_edit = self._line_edit(
            self.data.get("Core_Download_Proxy", "https://ghfast.top")
        )
        self.core_proxy_edit.setPlaceholderText(
            self.tr("推荐 https://ghfast.top；留空则直连 GitHub 官方")
        )
        proxy_row.addWidget(self.core_proxy_edit, 1)
        update_layout.addLayout(proxy_row)

        self.core_check_btn = PrimaryPushButton(self.tr("检查更新"))
        self.core_check_btn.setIcon(FIF.SYNC)
        self.core_check_btn.clicked.connect(self._on_check_core_update)
        update_layout.addWidget(self.core_check_btn, alignment=Qt.AlignLeft)

        self.core_update_result = CaptionLabel("")
        self.core_update_result.setWordWrap(True)
        self.core_update_result.setStyleSheet("color: #707070;")
        update_layout.addWidget(self.core_update_result)
        layout.addWidget(update_card)
        layout.addStretch()

        self._core_runtime_timer = QTimer(self)
        self._core_runtime_timer.timeout.connect(self._refresh_core_runtime_status)
        self._core_runtime_timer.start(500)
        self._refresh_core_actions()
        self._refresh_core_runtime_status()
        return widget

    def _runtime_display(self) -> tuple[str, str, str]:
        snapshot = get_core_runtime()
        mapping = {
            CoreRuntimeState.CONNECTED: (
                self.tr("接收管道已连接"),
                snapshot.detail or self.tr("正在监听 QQ 消息"),
                "#16833b",
            ),
            CoreRuntimeState.WAITING: (
                self.tr("等待接收管道"),
                snapshot.detail or self.tr("核心已映射，但监听链路尚未连通"),
                "#c27a00",
            ),
            CoreRuntimeState.NO_QQ: (
                self.tr("未找到 QQ"),
                snapshot.detail or self.tr("请先启动 QQ 主进程"),
                "#c27a00",
            ),
            CoreRuntimeState.ERROR: (
                self.tr("核心运行异常"),
                snapshot.detail,
                "#c42b1c",
            ),
            CoreRuntimeState.PAUSED: (
                self.tr("监听已暂停"),
                snapshot.detail,
                "#6b6b6b",
            ),
            CoreRuntimeState.DETACHED: (
                self.tr("核心已卸载"),
                snapshot.detail,
                "#6b6b6b",
            ),
            CoreRuntimeState.UNSUPPORTED: (
                self.tr("当前平台不可用"),
                snapshot.detail,
                "#6b6b6b",
            ),
        }
        return mapping[snapshot.state]

    def _refresh_core_runtime_status(self):
        if not hasattr(self, "core_runtime_title"):
            return
        title, detail, color = self._runtime_display()
        self.core_runtime_title.setText(title)
        self.core_runtime_detail.setText(detail)
        self.core_runtime_dot.setStyleSheet(f"font-size: 18px; color: {color};")
        self._refresh_notification_status()

    def _refresh_core_actions(self):
        if not hasattr(self, "core_toggle_btn"):
            return
        state = get_core_state()
        self.core_toggle_btn.setText(
            self.tr("暂停监听") if state == CoreState.RUNNING else self.tr("启动监听")
        )
        self.core_toggle_btn.setIcon(FIF.PAUSE if state == CoreState.RUNNING else FIF.PLAY)
        self.core_unload_btn.setEnabled(state != CoreState.DETACHED)

    def _request_core_unload(self):
        confirmed = show_fluent_message(
            self,
            self.tr("卸载核心"),
            self.tr("将从 QQ 进程卸载核心并停止监听，确定继续？"),
            yes_text=self.tr("卸载"),
            cancel_text=self.tr("取消"),
        )
        if confirmed:
            unload_core()

    def _core_version_text(self) -> str:
        if not is_core_installed():
            return self.tr("未安装")
        version = current_core_version()
        return version or self.tr("已安装（版本未知）")

    def _on_check_core_update(self):
        self.core_check_btn.setEnabled(False)
        self.core_update_result.setText(self.tr("正在检查更新…"))
        self._core_thread = _CoreUpdateThread("check", parent=self)
        self._core_thread.checked.connect(self._on_core_update_checked)
        self._core_thread.failed.connect(self._on_core_update_failed)
        self._core_thread.start()

    def _on_core_update_checked(self, status):
        if status.error:
            self.core_update_result.setText(self.tr("检查失败: {err}").format(err=status.error))
            self.core_check_btn.setEnabled(True)
            return

        if not status.has_update and status.installed:
            self.core_update_result.setText(
                self.tr("已是最新: {ver}").format(
                    ver=status.current_version or status.latest_version
                )
            )
            self.core_check_btn.setEnabled(True)
            return

        action = self.tr("安装") if not status.installed else self.tr("更新")
        confirmed = show_fluent_message(
            self,
            self.tr("{action}核心").format(action=action),
            self.tr(
                "将从 SnowLuma 官方 release 下载核心 {ver}。\n"
                "该组件为 SnowLuma 专有，使用即表示遵守其许可。是否继续？"
            ).format(ver=status.latest_version),
            yes_text=action,
            cancel_text=self.tr("取消"),
        )
        if not confirmed:
            self.core_update_result.setText("")
            self.core_check_btn.setEnabled(True)
            return

        self.core_update_result.setText(self.tr("正在下载安装…"))
        proxy = self.core_proxy_edit.text().strip() or None
        self._core_thread = _CoreUpdateThread("install", proxy=proxy, parent=self)
        self._core_thread.installed.connect(self._on_core_update_installed)
        self._core_thread.failed.connect(self._on_core_update_failed)
        self._core_thread.start()

    def _on_core_update_installed(self, version: str):
        self.core_version_value.setText(self._core_version_text())
        self.core_update_result.setText(self.tr("已安装: {ver}").format(ver=version))
        self.core_check_btn.setEnabled(True)

    def _on_core_update_failed(self, message: str):
        self.core_update_result.setText(self.tr("失败: {msg}").format(msg=message))
        self.core_check_btn.setEnabled(True)

    def _on_version_label_clicked(self, event):
        if event.button() != Qt.MouseButton.LeftButton:
            return

        if self._debug_page_unlocked:
            self._switch_settings_page("debug")
            event.accept()
            return

        self._debug_click_count += 1
        if self._debug_click_count >= 7:
            self._unlock_debug_page()
            self._switch_settings_page("debug")
        event.accept()

    def _unlock_debug_page(self):
        if self._debug_page_unlocked:
            return

        self._debug_page_unlocked = True
        title, icon = self._settings_page_meta["debug"]
        insert_index = (
            self._settings_pivot_routes.index("about")
            if "about" in self._settings_pivot_routes
            else len(self._settings_pivot_routes)
        )
        self._add_settings_pivot_item("debug", title, icon, insert_index)

    def _config_status(self) -> tuple[bool, list[str]]:
        missing = []
        if not self.data.get("User_QQ", ""):
            missing.append(self.tr("未填写 QQ 号"))
        return not missing, missing

    def _home_summary_rows(self, status_items: list[str]) -> list[tuple[str, str]]:
        return [
            (self.tr("QQ 号"), self.data.get("User_QQ", "") or self.tr("未填写")),
            (
                self.tr("重要人物 QQ"),
                str(len(self.data.get("Important_Person_QQs", self.settings.important_person_qqs))),
            ),
            (
                self.tr("重要关键词"),
                str(len(self.data.get("Important_Keywords", self.settings.important_keywords))),
            ),
            (
                self.tr("状态"),
                self.tr("正常") if not status_items else "，".join(status_items),
            ),
        ]

    def _create_basic_tab(self):
        """基本设置标签页"""
        widget = QWidget()
        form = QFormLayout(widget)

        self.cooldown = SpinBox()
        self.cooldown.setRange(0, 60)
        self.cooldown.setValue(self.data.get("Cooldown", self.settings.cooldown))

        self.user_qq = self._line_edit(self.data.get("User_QQ", ""))

        self.language_combo = ComboBox()
        self.language_combo.addItems([self.tr("English"), self.tr("日本語"), self.tr("简体中文")])
        self.language_combo.currentIndexChanged.connect(self._on_language_changed)

        lang = self.data.get("Language", self.settings.language)
        if lang == "en-US":
            self.language_combo.setCurrentIndex(0)
        elif lang == "ja-JP":
            self.language_combo.setCurrentIndex(1)
        else:
            self.language_combo.setCurrentIndex(2)

        self.auto_start = CheckBox(self.tr("开机自启动（仅 Windows）"))
        self.auto_start.setChecked(
            is_auto_start_enabled()
            if is_auto_start_supported()
            else self.data.get("Auto_Start", self.settings.auto_start)
        )
        self.auto_start.setEnabled(is_auto_start_supported())
        if not is_auto_start_supported():
            self.auto_start.setToolTip(self.tr("开机自启动目前仅支持 Windows"))

        form.addRow(self.tr("冷却时间 (秒)"), self.cooldown)
        form.addRow(self.tr("QQ 号"), self.user_qq)
        form.addRow(self.tr("界面语言"), self.language_combo)
        form.addRow(self.auto_start)

        return widget

    def _create_rule_tab(self):
        """规则设置标签页"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        layout.setSpacing(12)

        hint = QLabel(
            self.tr(
                "所有编号均为精确匹配。黑名单优先；启用白名单后，群号或发送者 QQ 任一命中即可通知。白名单为空时会阻止全部消息。"
            )
        )
        hint.setWordWrap(True)
        hint.setStyleSheet("color: #606060;")
        layout.addWidget(hint)

        important_row = QHBoxLayout()
        important_row.setSpacing(16)
        person_column = QVBoxLayout()
        person_column.addWidget(self._section_label(self.tr("重要人物 QQ 号")))
        self.list_important_person_qqs = self._create_list(
            self.data.get("Important_Person_QQs", self.settings.important_person_qqs),
            placeholder=self.tr("输入 QQ 号"),
            numeric_only=True,
        )
        person_column.addWidget(self.list_important_person_qqs)

        keyword_column = QVBoxLayout()
        keyword_column.addWidget(self._section_label(self.tr("重要关键词")))
        self.list_keywords = self._create_list(
            self.data.get("Important_Keywords", self.settings.important_keywords),
            placeholder=self.tr("输入消息关键词"),
        )
        keyword_column.addWidget(self.list_keywords)
        important_row.addLayout(person_column, 1)
        important_row.addLayout(keyword_column, 1)
        layout.addLayout(important_row)

        switch_row = QHBoxLayout()
        self.whitelist_enabled = CheckBox(self.tr("启用白名单"))
        self.whitelist_enabled.setChecked(
            self.data.get("Whitelist_Enabled", self.settings.whitelist_enabled)
        )
        self.blacklist_enabled = CheckBox(self.tr("启用黑名单"))
        self.blacklist_enabled.setChecked(
            self.data.get("Blacklist_Enabled", self.settings.blacklist_enabled)
        )
        switch_row.addWidget(self.whitelist_enabled)
        switch_row.addWidget(self.blacklist_enabled)
        switch_row.addStretch()
        layout.addLayout(switch_row)

        lists_row = QHBoxLayout()
        lists_row.setSpacing(16)

        white_column = QVBoxLayout()
        white_column.addWidget(self._section_label(self.tr("白名单群号")))
        self.list_whitelist_groups = self._create_list(
            self.data.get("Whitelist_Groups", self.settings.whitelist_groups),
            placeholder=self.tr("输入群号"),
            numeric_only=True,
        )
        white_column.addWidget(self.list_whitelist_groups)
        white_column.addWidget(self._section_label(self.tr("白名单人物 QQ 号")))
        self.list_whitelist_person_qqs = self._create_list(
            self.data.get("Whitelist_Person_QQs", self.settings.whitelist_person_qqs),
            placeholder=self.tr("输入 QQ 号"),
            numeric_only=True,
        )
        white_column.addWidget(self.list_whitelist_person_qqs)

        black_column = QVBoxLayout()
        black_column.addWidget(self._section_label(self.tr("黑名单群号")))
        self.list_blacklist_groups = self._create_list(
            self.data.get("Blacklist_Groups", self.settings.blacklist_groups),
            placeholder=self.tr("输入群号"),
            numeric_only=True,
        )
        black_column.addWidget(self.list_blacklist_groups)
        black_column.addWidget(self._section_label(self.tr("黑名单人物 QQ 号")))
        self.list_blacklist_person_qqs = self._create_list(
            self.data.get("Blacklist_Person_QQs", self.settings.blacklist_person_qqs),
            placeholder=self.tr("输入 QQ 号"),
            numeric_only=True,
        )
        black_column.addWidget(self.list_blacklist_person_qqs)

        lists_row.addLayout(white_column, 1)
        lists_row.addLayout(black_column, 1)
        layout.addLayout(lists_row)

        self.someone_at_me = CheckBox(self.tr("当有人 @ 我时设为重要通知"))
        self.someone_at_me.setChecked(self.data.get("Someone_At_Me", self.settings.someone_at_me))
        layout.addWidget(self.someone_at_me)
        layout.addStretch()

        return widget

    def _create_appearance_tab(self):
        """外观设置标签页"""
        widget = QWidget()
        layout = QVBoxLayout(widget)

        self.notify_shadow = CheckBox(self.tr("通知窗口启用阴影"))
        self.notify_shadow.setChecked(self.data.get("Notify_Shadow", self.settings.notify_shadow))
        self.notify_animation = CheckBox(self.tr("通知窗口启用动画"))
        self.notify_animation.setChecked(
            self.data.get("Notify_Animation", self.settings.notify_animation)
        )
        self.notify_mask = CheckBox(self.tr("通知窗口启用遮罩"))
        self.notify_mask.setChecked(self.data.get("Notify_Mask", self.settings.notify_mask))
        self.show_status_ball = SwitchButton()
        self.show_status_ball.setOnText(self.tr("已显示悬浮球"))
        self.show_status_ball.setOffText(self.tr("已隐藏悬浮球"))
        self.show_status_ball.setChecked(
            self.data.get("Show_Status_Ball", self.settings.show_status_ball)
        )
        self.notify_label = self._line_edit(
            self.data.get("Notify_Label", self.settings.notify_label)
        )

        # 图标选择
        self.notify_ok_layout = QHBoxLayout()
        self.notify_icon_ok = self._line_edit(self.data.get("icon_ok", self.settings.icon_ok))
        self.notify_ok_select = PushButton(self.tr("浏览"))
        self.notify_ok_select.setIcon(FIF.FOLDER)
        self.notify_ok_select.clicked.connect(lambda: self._select_file(self.notify_icon_ok))
        self.notify_ok_layout.addWidget(self.notify_icon_ok)
        self.notify_ok_layout.addWidget(self.notify_ok_select)

        self.notify_dismiss_layout = QHBoxLayout()
        self.notify_icon_cancel = self._line_edit(
            self.data.get("icon_cancel", self.settings.icon_cancel)
        )
        self.notify_cancel_select = PushButton(self.tr("浏览"))
        self.notify_cancel_select.setIcon(FIF.FOLDER)
        self.notify_cancel_select.clicked.connect(
            lambda: self._select_file(self.notify_icon_cancel)
        )
        self.notify_dismiss_layout.addWidget(self.notify_icon_cancel)
        self.notify_dismiss_layout.addWidget(self.notify_cancel_select)

        # 字体选择
        self.notify_title_layout = QHBoxLayout()
        self.notify_title_font = self._line_edit(
            self.data.get("Notify_Title_Font", self.settings.notify_title_font)
        )
        self.notify_title_select = PushButton(self.tr("浏览"))
        self.notify_title_select.setIcon(FIF.FOLDER)
        self.notify_title_select.clicked.connect(lambda: self._select_file(self.notify_title_font))
        self.notify_title_layout.addWidget(self.notify_title_font)
        self.notify_title_layout.addWidget(self.notify_title_select)

        self.notify_message_layout = QHBoxLayout()
        self.notify_message_font = self._line_edit(
            self.data.get("Notify_Message_Font", self.settings.notify_message_font)
        )
        self.notify_message_select = PushButton(self.tr("浏览"))
        self.notify_message_select.setIcon(FIF.FOLDER)
        self.notify_message_select.clicked.connect(
            lambda: self._select_file(self.notify_message_font)
        )
        self.notify_message_layout.addWidget(self.notify_message_font)
        self.notify_message_layout.addWidget(self.notify_message_select)

        layout.addWidget(self.notify_shadow)
        layout.addWidget(self.notify_animation)
        layout.addWidget(self.notify_mask)
        layout.addWidget(self.show_status_ball)
        layout.addWidget(QLabel(self.tr("通知下方显示文本（可留空）")))
        layout.addWidget(self.notify_label)
        layout.addWidget(QLabel(self.tr("通知收到按钮图标")))
        layout.addLayout(self.notify_ok_layout)
        layout.addWidget(QLabel(self.tr("通知关闭按钮图标")))
        layout.addLayout(self.notify_dismiss_layout)
        layout.addWidget(QLabel(self.tr("通知标题字体（TTF 文件）")))
        layout.addLayout(self.notify_title_layout)
        layout.addWidget(QLabel(self.tr("通知内容字体（TTF 文件）")))
        layout.addLayout(self.notify_message_layout)
        layout.addStretch()

        return widget

    def _create_notify_tab(self):
        """通知设置标签页"""
        widget = QWidget()
        form = QFormLayout(widget)

        self.auto_thumb = CheckBox(self.tr("当有人发送[图片]自动显示缩略图（不稳定）"))
        self.auto_thumb.setChecked(self.data.get("Auto_Show_Thumb", self.settings.auto_show_thumb))

        self.always_on_top = CheckBox(self.tr("通知始终置顶"))
        self.always_on_top.setChecked(self.data.get("Always_On_Top", self.settings.always_on_top))

        self.duration_everyone = SpinBox()
        self.duration_everyone.setRange(1000, 20000)
        self.duration_everyone.setValue(
            self.data.get("Duration_Everyone", self.settings.duration_everyone)
        )

        self.duration_important = SpinBox()
        self.duration_important.setRange(1000, 30000)
        self.duration_important.setValue(
            self.data.get("Duration_Important", self.settings.duration_important)
        )

        self.tts = CheckBox(self.tr("全局 TTS（语音播报） 开关"))
        self.tts.setChecked(self.data.get("TTS", self.settings.tts_enabled))
        self.tts.stateChanged.connect(self._on_tts_changed)

        self.edge_tts = CheckBox(self.tr("使用新版 EdgeTTS"))
        self.edge_tts.setChecked(self.data.get("Edge_TTS", self.settings.edge_tts_enabled))

        self.edge_voice = EditableComboBox()
        voices = [
            "zh-CN-XiaoxiaoNeural",
            "zh-CN-YunxiNeural",
            "zh-CN-YunjianNeural",
            "ja-JP-NanamiNeural",
            "ja-JP-KeitaNeural",
            "en-US-JennyNeural",
        ]
        self.edge_voice.addItems(voices)
        current_voice = self.data.get("Edge_Voice", self.settings.edge_voice)
        if current_voice not in voices:
            self.edge_voice.addItem(current_voice)
        self.edge_voice.setCurrentText(current_voice)
        self.edge_voice.setEnabled(self.edge_tts.isChecked())

        self.edge_rate = Slider()
        self.edge_rate.setOrientation(Qt.Horizontal)
        self.edge_rate.setRange(-100, 100)
        rate_str = self.data.get("Edge_Rate", self.settings.edge_rate)
        rate_value = int(rate_str.replace("%", "").replace("+", ""))
        self.edge_rate.setValue(rate_value)
        self.edge_rate.setEnabled(self.edge_tts.isChecked())

        self.edge_pitch = Slider()
        self.edge_pitch.setOrientation(Qt.Horizontal)
        self.edge_pitch.setRange(-100, 100)
        pitch_str = self.data.get("Edge_Pitch", self.settings.edge_pitch)
        pitch_value = int(pitch_str.replace("Hz", "").replace("+", ""))
        self.edge_pitch.setValue(pitch_value)
        self.edge_pitch.setEnabled(self.edge_tts.isChecked())

        self.edge_volume = Slider()
        self.edge_volume.setOrientation(Qt.Horizontal)
        self.edge_volume.setRange(-100, 100)
        vol_str = self.data.get("Edge_Volume", self.settings.edge_volume)
        vol_value = int(vol_str.replace("%", "").replace("+", ""))
        self.edge_volume.setValue(vol_value)
        self.edge_volume.setEnabled(self.edge_tts.isChecked())

        self.edge_test_text = self._line_edit(self.tr("你好呀，这里是 EdgeTTS 酱哦~"))
        self.edge_test_layout = QHBoxLayout()
        self.edge_test_btn = PushButton(self.tr("试听"))
        self.edge_test_btn.setIcon(FIF.PLAY)
        self.edge_test_btn.clicked.connect(self._on_edge_test)
        self.edge_test_layout.addWidget(self.edge_test_text)
        self.edge_test_layout.addWidget(self.edge_test_btn)
        self.edge_tts_warning = QLabel(
            self.tr(
                "EdgeTTS 基于神经网络，需要联网，但可自定义效果，若不勾选使用系统自带 TTS（已知问题：EdgeTTS 音调和语速设为负数可能会报错，也不是所有系统支持EdgeTTS，若无声音请取消勾选此复选框）"
            )
        )
        self.edge_tts_warning.setWordWrap(True)
        form.addRow(self.auto_thumb)
        form.addRow(self.always_on_top)
        form.addRow(self.tr("普通通知时长(ms)"), self.duration_everyone)
        form.addRow(self.tr("重要通知时长(ms)"), self.duration_important)
        form.addRow(self.tts)
        form.addRow(self.edge_tts_warning)
        form.addRow(self.tr("EdgeTTS 音色"), self.edge_voice)
        form.addRow(self.tr("EdgeTTS 语速"), self.edge_rate)
        form.addRow(self.tr("EdgeTTS 音高"), self.edge_pitch)
        form.addRow(self.tr("EdgeTTS 音量"), self.edge_volume)
        form.addRow(self.tr("测试 TTS"), self.edge_test_layout)

        return widget

    def _create_calling_tab(self):
        widget = QWidget()
        form = QFormLayout(widget)

        self.calling = CheckBox(self.tr("允许老师呼叫"))
        self.calling.setChecked(self.data.get("Calling", self.settings.calling_enabled))
        self.calling_keyword = self._line_edit(
            self.data.get("Calling_Keyword", self.settings.calling_keyword)
        )
        self.calling_during = SpinBox()
        self.calling_during.setRange(0, 999999)
        self.calling_during.setValue(
            self.data.get("Calling_Duration", self.settings.calling_duration)
        )
        self.calling_anim = CheckBox(self.tr("呼叫启用动画"))
        self.calling_anim.setChecked(
            self.data.get("Calling_Animation", self.settings.calling_animation)
        )
        self.calling_bpm = SpinBox()
        self.calling_bpm.setRange(0, 1000)
        self.calling_bpm.setValue(self.data.get("Calling_BPM", self.settings.calling_bpm))

        self.calling_hint = QLabel(
            self.tr(
                "当老师按一定格式（例如 呼叫XXX，来办公室搬下作业）呼叫，弹出窗口将持续更长时间，并且循环播放铃声和夸张的动画效果直到有人响应。使用本功能前请先和老师约定好呼叫关键词（只能设置一个）"
            )
        )
        self.calling_hint.setWordWrap(True)

        form.addRow(self.calling_hint)
        form.addRow(self.calling)
        form.addRow(self.tr("呼叫关键词"), self.calling_keyword)
        form.addRow(self.tr("呼叫窗口弹出时间(ms)"), self.calling_during)
        form.addRow(self.calling_anim)
        form.addRow(self.tr("呼叫动画 BPM"), self.calling_bpm)

        return widget

    def _create_sound_tab(self):
        widget = QWidget()
        form = QFormLayout(widget)

        self.sound_normal = self._line_edit(
            self.data.get("Sound_Effect_Normal", self.settings.sound_normal)
        )
        btn1 = PushButton(self.tr("浏览"))
        btn1.setIcon(FIF.FOLDER)
        btn1.clicked.connect(lambda: self._select_file(self.sound_normal))
        btn3 = PushButton(self.tr("试听"))
        btn3.setIcon(FIF.PLAY)
        btn3.clicked.connect(lambda: self._test_sound(self.sound_normal))

        row1 = QHBoxLayout()
        row1.addWidget(self.sound_normal)
        row1.addWidget(btn1)
        row1.addWidget(btn3)

        self.sound_important = self._line_edit(
            self.data.get("Sound_Effect_Important", self.settings.sound_important)
        )
        btn2 = PushButton(self.tr("浏览"))
        btn2.setIcon(FIF.FOLDER)
        btn2.clicked.connect(lambda: self._select_file(self.sound_important))
        btn4 = PushButton(self.tr("试听"))
        btn4.setIcon(FIF.PLAY)
        btn4.clicked.connect(lambda: self._test_sound(self.sound_important))

        row2 = QHBoxLayout()
        row2.addWidget(self.sound_important)
        row2.addWidget(btn2)
        row2.addWidget(btn4)

        form.addRow(self.tr("普通提示音"), row1)
        form.addRow(self.tr("重要提示音"), row2)

        return widget

    def _create_debug_tab(self):
        content = QWidget()
        form = QFormLayout(content)

        hint = QLabel(
            self.tr("捕获方式：核心注入（SnowLuma）。核心开关请在悬浮球或首页状态徽章上操作。")
        )
        hint.setWordWrap(True)
        form.addRow(hint)

        return content

    def _create_about_tab(self):
        """关于标签页"""
        widget = QWidget()
        form = QFormLayout(widget)

        self.title = QLabel(self.tr("QQListener"))
        self.title.setStyleSheet("font-size: 20px; font-weight: 600;")
        self.subtitle = QLabel(self.tr("最好的QQ通知监控软件 - 班级群监控神器 v1.1 20260319"))
        self.subtitle.setStyleSheet("font-size: 16px")
        self.subtitle.setCursor(Qt.CursorShape.PointingHandCursor)
        self.subtitle.mousePressEvent = self._on_version_label_clicked

        self.author_title = QLabel(
            self.tr(
                "作者：株洲市南方中学 xxt8582753\n网站：https://xxtsoft.top\n邮箱：xxt8582753@126.com"
            )
        )
        self.author_title.setStyleSheet("font-size: 16px")

        self.privacy_hint = QLabel(self.tr("我的数据安全吗？"))
        self.privacy_hint.mousePressEvent = lambda event: show_fluent_message(
            self,
            self.tr("我是绝对绝对不会出卖你的！"),
            self.tr(
                "您的数据是安全的，您的QQ号，文件路径全部保存在本地，聊天记录等信息不会上传，也没有任何遥测和错误报告。\nQQListener 是开源软件，使用 MIT 许可证，您可以在 GitHub 上查看源代码"
            ),
        )

        self.help_me_hint = QLabel(self.tr("支持开发者"))
        self.help_me_hint.mousePressEvent = lambda event: self._open_donate_if_confirmed()

        self.find_icon_hint = QLabel(self.tr("QQListener 征集图标"))
        self.find_icon_hint.mousePressEvent = lambda event: show_fluent_message(
            self,
            self.tr("QQListener 征集图标"),
            self.tr("目前这个图标有点丑，如果你有更好的，欢迎联系我！"),
        )

        self.button_layout = QHBoxLayout()
        self.clear = PushButton(self.tr("清除缓存"))
        self.clear.setIcon(FIF.DELETE)
        self.clear.clicked.connect(self._clear_cache)
        self.help = PushButton(self.tr("查看教程"))
        self.help.setIcon(FIF.HELP)
        self.help.clicked.connect(lambda: webbrowser.open("https://xxtsoft.top/support/qqlistener"))
        self.translation = PushButton(self.tr("提交翻译"))
        self.translation.setIcon(FIF.SEND)
        self.translation.clicked.connect(
            lambda: webbrowser.open("https://xxtsoft.top/support/qqlistener/translation")
        )
        self.button_layout.addWidget(self.clear)
        self.button_layout.addWidget(self.help)
        self.button_layout.addWidget(self.translation)

        form.addRow(self.title)
        form.addRow(self.subtitle)
        form.addRow(self.author_title)
        form.addRow(self.privacy_hint)
        form.addRow(self.help_me_hint)
        form.addRow(self.find_icon_hint)
        form.addRow(self.button_layout)

        return widget

    def _open_donate_if_confirmed(self):
        if show_fluent_message(
            self,
            self.tr("支持一下嘛"),
            self.tr(
                "我是一名高中生，没有稳定的经济来源，如果您喜欢这个项目，并且想要支持我继续开发和维护，可以考虑请我喝杯奶茶哦~\n"
            ),
            yes_text=self.tr("确认"),
            cancel_text=self.tr("取消"),
        ):
            webbrowser.open("https://xxtsoft.top/donate")

    def _create_list(self, items, placeholder=None, numeric_only=False):
        """创建列表组件"""
        container = QWidget()
        layout = QVBoxLayout(container)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(8)

        list_widget = ListWidget()
        list_widget.setFixedHeight(96)
        list_widget.setStyleSheet("""
            ListWidget {
                color: #202020;
                background: rgba(255, 255, 255, 215);
                border: 1px solid #dfe5ec;
                border-radius: 8px;
            }
        """)
        for item in items:
            list_widget.addItem(item)

        input_line = LineEdit()
        input_line.setPlaceholderText(placeholder or self.tr("输入后点击添加，也可按回车"))
        input_line.returnPressed.connect(
            lambda: self._add_item(list_widget, input_line, numeric_only)
        )
        btn_add = PushButton(self.tr("添加"))
        btn_remove = PushButton(self.tr("删除选中"))
        btn_add.setIcon(FIF.ADD)
        btn_remove.setIcon(FIF.DELETE)
        btn_add.clicked.connect(lambda: self._add_item(list_widget, input_line, numeric_only))
        btn_remove.clicked.connect(lambda: self._remove_item(list_widget))

        layout.addWidget(list_widget)

        btn_row = QHBoxLayout()
        btn_row.addWidget(input_line)
        btn_row.addWidget(btn_add)
        btn_row.addWidget(btn_remove)
        layout.addLayout(btn_row)
        container.list_widget = list_widget
        container.setMaximumHeight(154)

        return container

    def _add_item(self, widget, line, numeric_only=False):
        """添加列表项"""
        text = line.text().strip()
        if not text:
            return
        if numeric_only and not text.isdecimal():
            show_fluent_message(self, self.tr("提示"), self.tr("这里只能填写数字编号"))
            return
        for i in range(widget.count()):
            if widget.item(i).text() == text:
                show_fluent_message(self, self.tr("提示"), self.tr("该项已存在"))
                line.clear()
                return
        widget.addItem(text)
        line.clear()

    def _remove_item(self, widget):
        """删除列表项"""
        selected = widget.selectedItems()
        for item in selected:
            widget.takeItem(widget.row(item))

    def _get_list(self, container):
        """获取列表数据"""
        list_widget = container.list_widget
        return [list_widget.item(i).text() for i in range(list_widget.count())]

    def _select_file(self, line):
        """选择文件"""
        path, _ = QFileDialog.getOpenFileName(self, self.tr("选择文件"))
        if path:
            line.setText(path)

    def _test_sound(self, line):
        """测试声音"""
        path = line.text().strip()
        if path and os.path.exists(path):
            pygame.mixer.music.load(path)
            pygame.mixer.music.play()

    def _test_exe(self, name):
        """测试程序是否运行"""
        try:
            output = subprocess.check_output(
                f'tasklist /fi "imagename eq {name}*"',
                shell=True,
                text=True,
                stderr=subprocess.STDOUT,
            )
            if name in output:
                show_fluent_message(self, self.tr("成功"), self.tr(f"{name} 活着"))
            else:
                show_fluent_message(self, self.tr("失败"), self.tr(f"{name} 死了"))
        except subprocess.CalledProcessError as e:
            show_fluent_message(self, self.tr("错误"), self.tr(f"执行命令失败: {e.output}"))

    def _clear_cache(self):
        """清除缓存"""
        if os.path.exists("tts_output.mp3"):
            os.remove("tts_output.mp3")
        show_fluent_message(self, self.tr("成功"), self.tr("缓存已清除"))

    def _test_notify(self):
        """测试通知"""
        from src.ui.notify_manager import get_notify_manager

        test_data = {
            "Sender": "测试发送者",
            "Message": "这是一条测试消息",
            "Duration": 5000,
            "Priority": 0,
            "Calling": False,
            "icon_file": "asset/pdf.png",
        }
        get_notify_manager().show_notification(test_data)

    def _on_language_changed(self):
        """语言改变"""
        selected = self.language_combo.currentIndex()
        app = QApplication.instance()
        if not app:
            return
        translator = QTranslator()

        if selected == 0:
            translator.load("translations/en_US.qm")
            app.installTranslator(translator)
        elif selected == 1:
            translator.load("translations/ja_JP.qm")
            app.installTranslator(translator)

    def _on_tts_changed(self, state):
        """TTS状态改变"""
        current = state == Qt.CheckState.Checked
        self.edge_tts.setEnabled(current)
        self.edge_pitch.setEnabled(current and self.edge_tts.isChecked())
        self.edge_rate.setEnabled(current and self.edge_tts.isChecked())
        self.edge_test_btn.setEnabled(current)
        self.edge_test_text.setEnabled(current)
        self.edge_voice.setEnabled(current and self.edge_tts.isChecked())
        self.edge_volume.setEnabled(current and self.edge_tts.isChecked())

    def _on_edge_test(self):
        """Edge TTS测试"""
        set_system_volume_max()
        if self.edge_tts.isChecked():
            self._edge_tts_engine(
                TEXT=self.edge_test_text.text(),
                VOICE=self.edge_voice.currentText(),
                PITCH=f"{self.edge_pitch.value():+d}Hz",
                VOLUME=f"{self.edge_volume.value():+d}%",
                RATE=f"{self.edge_rate.value():+d}%",
            )
        else:
            try:
                import pyttsx3

                engine = pyttsx3.init()
                engine.setProperty(
                    "voice",
                    r"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\Tokens\TTS_MS_ZH-CN_HUIHUI_11.0",
                )
                engine.setProperty("volume", 1)
                engine.say(self.edge_test_text.text())
                engine.runAndWait()
            except ImportError:
                show_fluent_message(
                    self,
                    self.tr("系统 TTS 不可用"),
                    self.tr("未安装 pyttsx3，无法使用系统 TTS。"),
                )
            except Exception:
                logger.exception("系统 TTS 试听失败")
                show_fluent_message(
                    self,
                    self.tr("系统 TTS 不可用"),
                    self.tr("系统 TTS 初始化失败，请改用 EdgeTTS 或安装可用语音引擎。"),
                )

    def _edge_tts_engine(self, TEXT, VOICE, RATE, PITCH, VOLUME):
        """Edge TTS引擎"""
        OUTPUT_FILE = "tts_output.mp3"
        cmd = (
            f"edge-tts "
            f'--voice "{VOICE}" '
            f'--rate "{RATE}" '
            f'--pitch "{PITCH}" '
            f'--volume "{VOLUME}" '
            f'--text "{TEXT}" '
            f'--write-media "{OUTPUT_FILE}"'
        )

        subprocess.run(cmd, shell=True, check=True)
        pygame.mixer.init()
        pygame.mixer.music.load(OUTPUT_FILE)
        pygame.mixer.music.play()
        while pygame.mixer.music.get_busy():
            time.sleep(0.1)
        pygame.mixer.quit()

    def _apply_theme(self):
        """主题切换功能已移除，保留空方法兼容旧调用。"""
        return

    def save_settings(self):
        """保存设置"""
        auto_start_enabled = self.auto_start.isChecked() if is_auto_start_supported() else False
        if is_auto_start_supported() and not set_auto_start_enabled(auto_start_enabled):
            show_fluent_message(
                self,
                self.tr("错误"),
                self.tr("开机自启动设置失败，请检查系统权限后重试。"),
            )
            return

        self.settings.update(
            {
                "Cooldown": self.cooldown.value(),
                "Auto_Start": auto_start_enabled,
                "User_QQ": self.user_qq.text(),
                "Core_Download_Proxy": self.core_proxy_edit.text().strip(),
                "Important_Person_QQs": self._get_list(self.list_important_person_qqs),
                "Important_Keywords": self._get_list(self.list_keywords),
                "Whitelist_Enabled": self.whitelist_enabled.isChecked(),
                "Blacklist_Enabled": self.blacklist_enabled.isChecked(),
                "Whitelist_Groups": self._get_list(self.list_whitelist_groups),
                "Blacklist_Groups": self._get_list(self.list_blacklist_groups),
                "Whitelist_Person_QQs": self._get_list(self.list_whitelist_person_qqs),
                "Blacklist_Person_QQs": self._get_list(self.list_blacklist_person_qqs),
                "Sound_Effect_Normal": self.sound_normal.text(),
                "Sound_Effect_Important": self.sound_important.text(),
                "Auto_Show_Thumb": self.auto_thumb.isChecked(),
                "Always_On_Top": self.always_on_top.isChecked(),
                "Duration_Everyone": self.duration_everyone.value(),
                "Duration_Important": self.duration_important.value(),
                "Notify_Shadow": self.notify_shadow.isChecked(),
                "Notify_Animation": self.notify_animation.isChecked(),
                "Notify_Label": self.notify_label.text(),
                "Show_Status_Ball": self.show_status_ball.isChecked(),
                "Someone_At_Me": self.someone_at_me.isChecked(),
                "Calling": self.calling.isChecked(),
                "Calling_Keyword": self.calling_keyword.text(),
                "Calling_Duration": self.calling_during.value(),
                "TTS": self.tts.isChecked(),
                "Edge_TTS": self.edge_tts.isChecked(),
                "Edge_Voice": self.edge_voice.currentText(),
                "Edge_Rate": f"{self.edge_rate.value():+d}%",
                "Edge_Volume": f"{self.edge_volume.value():+d}%",
                "Edge_Pitch": f"{self.edge_pitch.value():+d}Hz",
                "Green_Hand": False,
                "Language": self._get_language_code(),
                "Notify_Mask": self.notify_mask.isChecked(),
                "Calling_BPM": self.calling_bpm.value(),
                "Calling_Animation": self.calling_anim.isChecked(),
                "icon_ok": self.notify_icon_ok.text(),
                "icon_cancel": self.notify_icon_cancel.text(),
                "Notify_Title_Font": self.notify_title_font.text(),
                "Notify_Message_Font": self.notify_message_font.text(),
            }
        )

        if self.settings.save():
            self.data = self.settings.get_all()
            self.refresh_home()
            self.signals.settings_changed.emit()
            show_fluent_message(self, self.tr("成功"), self.tr("设置已保存"))
        else:
            show_fluent_message(self, self.tr("错误"), self.tr("设置保存失败"))

    def _get_language_code(self):
        """获取语言代码"""
        idx = self.language_combo.currentIndex()
        if idx == 0:
            return "en-US"
        elif idx == 1:
            return "ja-JP"
        else:
            return "zh-CN"
