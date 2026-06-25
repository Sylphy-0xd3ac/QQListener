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
    QIcon,
    QLabel,
    QLineEdit,
    QPainter,
    QRectF,
    QSize,
    QStackedWidget,
    Qt,
    QTranslator,
    QVBoxLayout,
    QWidget,
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

from src.ui.fluent_compat import (
    CaptionLabel,
    CheckBox,
    ComboBox,
    DoubleSpinBox,
    EditableComboBox,
    FluentIcon as FIF,
    FluentWindow,
    IconInfoBadge,
    LineEdit,
    ListWidget,
    NavigationItemPosition,
    Pivot,
    PrimaryPushButton,
    PushButton,
    ScrollArea,
    Slider,
    SpinBox,
    SubtitleLabel,
)

from src.core.autostart import (
    is_auto_start_enabled,
    is_auto_start_supported,
    set_auto_start_enabled,
)
from src.core.notification_engines import (
    ENGINE_CHOICES,
    ENGINE_ONEBOT_V11,
    normalize_notification_engine,
)
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.ui.fluent_dialog import show_fluent_message
from src.utils.tts import set_system_volume_max


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
        self.init_ui()
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

    def _create_home_interface(self) -> QWidget:
        page = QWidget()
        page.setObjectName("homeInterface")
        layout = QVBoxLayout(page)
        layout.setContentsMargins(44, 40, 44, 36)
        layout.setSpacing(24)
        layout.addStretch()

        engine_label = self._current_engine_label()
        user_qq = self.data.get("User_QQ", "") or self.tr("未填写")

        status_row = QHBoxLayout()
        status_row.setSpacing(14)
        status_row.addStretch()

        self.home_status_badge = IconInfoBadge.success(FIF.ACCEPT_MEDIUM, page)
        self.home_status_badge.setFixedSize(36, 36)
        self.home_status_badge.setIconSize(QSize(18, 18))
        status_row.addWidget(self.home_status_badge, alignment=Qt.AlignVCenter)

        status_text_layout = QVBoxLayout()
        status_text_layout.setSpacing(4)
        self.home_status_title = SubtitleLabel(self.tr("正在运行"))
        self.home_status_detail = CaptionLabel(
            self.tr("引擎: {engine}  QQ号: {qq}").format(engine=engine_label, qq=user_qq)
        )
        self.home_status_detail.setStyleSheet("color: #707070;")
        status_text_layout.addWidget(self.home_status_title)
        status_text_layout.addWidget(self.home_status_detail)
        status_row.addLayout(status_text_layout)
        status_row.addStretch()
        layout.addLayout(status_row)
        layout.addStretch()
        return page

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

        layout.addWidget(SubtitleLabel(self.tr("设置")))

        self.settings_pivot = Pivot(page)
        self.settings_pivot.currentItemChanged.connect(self._on_settings_pivot_changed)
        layout.addWidget(self.settings_pivot)

        self.settings_stack = QStackedWidget(page)
        self.settings_stack.setObjectName("settingsStack")
        self.settings_stack.setStyleSheet(
            "QStackedWidget#settingsStack { background: transparent; }"
        )
        layout.addWidget(self.settings_stack, 1)

        self._settings_pages = {}
        for route_key, title, icon, content in [
            ("basic", self.tr("基本"), FIF.HOME, self._create_basic_tab()),
            ("engine", self.tr("引擎"), QIcon("asset/engine.svg"), self._create_engine_tab()),
            ("rule", self.tr("规则"), FIF.CHECKBOX, self._create_rule_tab()),
            ("appearance", self.tr("外观"), FIF.PALETTE, self._create_appearance_tab()),
            ("notify", self.tr("通知"), FIF.MESSAGE, self._create_notify_tab()),
            ("calling", self.tr("呼叫"), FIF.PHONE, self._create_calling_tab()),
            ("sound", self.tr("声音"), FIF.MUSIC, self._create_sound_tab()),
            ("debug", self.tr("调试"), FIF.CODE, self._create_debug_tab()),
            ("about", self.tr("关于"), FIF.INFO, self._create_about_tab()),
        ]:
            self._add_settings_pivot_page(route_key, title, icon, content)

        self._switch_settings_page("basic")

        action_layout = QHBoxLayout()
        action_layout.addStretch()
        btn_test = PushButton(self.tr("测试弹窗"))
        btn_save = PrimaryPushButton(self.tr("保存设置"))
        btn_test.setFixedHeight(38)
        btn_save.setFixedHeight(38)
        btn_test.clicked.connect(self._test_notify)
        btn_save.clicked.connect(self.save_settings)
        action_layout.addWidget(btn_test)
        action_layout.addWidget(btn_save)
        layout.addLayout(action_layout)
        return page

    def _add_settings_pivot_page(self, route_key: str, title: str, icon: FIF, content: QWidget):
        content.setObjectName(f"{route_key}Content")
        scroll_area = ScrollArea()
        scroll_area.setObjectName(f"{route_key}ScrollArea")
        scroll_area.setWidgetResizable(True)
        scroll_area.setFrameShape(QFrame.NoFrame)
        scroll_area.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        scroll_area.setWidget(content)
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
        self.settings_pivot.addItem(
            route_key,
            title,
            icon=icon,
        )

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

        engine_label = self._current_engine_label()
        user_qq = self.data.get("User_QQ", "") or self.tr("未填写")
        self.home_status_title.setText(self.tr("正在运行"))
        self.home_status_detail.setText(
            self.tr("引擎: {engine}  QQ号: {qq}").format(engine=engine_label, qq=user_qq)
        )

    def _config_status(self) -> tuple[bool, list[str]]:
        missing = []
        if not self.data.get("User_QQ", ""):
            missing.append(self.tr("未填写 QQ 号"))
        tencent_path = self.data.get("Tencent_Files_Path", "")
        if not tencent_path:
            missing.append(self.tr("未设置聊天信息保存文件夹"))
        elif not os.path.isdir(tencent_path):
            missing.append(self.tr("聊天信息保存文件夹不存在"))
        return not missing, missing

    def _home_summary_rows(self, status_items: list[str]) -> list[tuple[str, str]]:
        engine_label = self._current_engine_label()
        return [
            (self.tr("QQ 号"), self.data.get("User_QQ", "") or self.tr("未填写")),
            (
                self.tr("聊天信息保存文件夹"),
                self.data.get("Tencent_Files_Path", "") or self.tr("未设置"),
            ),
            (self.tr("通知监听引擎"), engine_label),
            (
                self.tr("重要人物"),
                str(len(self.data.get("Important_Persons", self.settings.important_persons))),
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

    def _current_engine_label(self) -> str:
        engine = normalize_notification_engine(
            self.data.get("NotificationEngine", self.settings.notification_engine),
            legacy_uia=self.data.get("UIAMode", self.settings.uia_mode),
        )
        return next(
            (self.tr(label) for key, label in ENGINE_CHOICES if key == engine),
            self.tr("自动选择"),
        )

    def _create_basic_tab(self):
        """基本设置标签页"""
        widget = QWidget()
        form = QFormLayout(widget)

        self.scan_interval = DoubleSpinBox()
        self.scan_interval.setRange(0.1, 10)
        self.scan_interval.setValue(self.data.get("ScanInterval", self.settings.scan_interval))

        self.cooldown = SpinBox()
        self.cooldown.setRange(0, 60)
        self.cooldown.setValue(self.data.get("Cooldown", self.settings.cooldown))

        self.user_qq = self._line_edit(self.data.get("User_QQ", ""))

        self.tencent_path = self._line_edit(self.data.get("Tencent_Files_Path", ""))
        btn_path = PushButton(self.tr("浏览"))
        btn_path.clicked.connect(self._select_path)

        path_row = QHBoxLayout()
        path_row.addWidget(self.tencent_path)
        path_row.addWidget(btn_path)

        self.whereis_tencentfile = QLabel(self.tr("我的聊天信息保存在哪里？"))
        self.whereis_tencentfile.mousePressEvent = lambda event: show_fluent_message(
            self,
            self.tr("提示"),
            self.tr(
                '打开 QQ 主面板，点击左下角设置，在存储设置选项卡中显示"聊天消息默认保存到..."'
            ),
        )

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

        form.addRow(self.tr("扫描间隔 (秒)"), self.scan_interval)
        form.addRow(self.tr("冷却时间 (秒)"), self.cooldown)
        form.addRow(self.tr("QQ 号"), self.user_qq)
        form.addRow(self.tr("聊天信息保存文件夹"), path_row)
        form.addRow(self.whereis_tencentfile)
        form.addRow(self.tr("界面语言"), self.language_combo)
        form.addRow(self.auto_start)

        return widget

    def _create_engine_tab(self):
        """引擎配置标签页"""
        widget = QWidget()
        layout = QVBoxLayout(widget)
        layout.setSpacing(18)

        form = QFormLayout()
        layout.addLayout(form)

        self.notification_engine_choices = list(ENGINE_CHOICES)
        self.notification_engine = ComboBox()
        for key, label in ENGINE_CHOICES:
            self.notification_engine.addItem(self.tr(label))
        engine = normalize_notification_engine(
            self.data.get("NotificationEngine", self.settings.notification_engine),
            legacy_uia=self.data.get("UIAMode", self.settings.uia_mode),
        )
        engine_index = next(
            (
                index
                for index, (key, _) in enumerate(self.notification_engine_choices)
                if key == engine
            ),
            0,
        )
        self.notification_engine.setCurrentIndex(max(engine_index, 0))
        self.notification_engine.currentIndexChanged.connect(self._sync_engine_config_visibility)

        form.addRow(self.tr("通知监听引擎"), self.notification_engine)

        self.onebot_engine_panel = QWidget()
        onebot_form = QFormLayout(self.onebot_engine_panel)
        onebot_title = SubtitleLabel(self.tr("OneBot V11（正向 WebSocket）"))
        onebot_hint = QLabel(
            self.tr(
                "填写 OneBot 协议端暴露的正向 WebSocket 地址。常见地址为 ws://127.0.0.1:8080/event；若协议端配置了 access_token，请在 Token 中填写同一个值。"
            )
        )
        onebot_hint.setWordWrap(True)

        self.onebot_v11_ws_url = self._line_edit(
            self.data.get("OneBotV11_WS_URL", self.settings.onebot_v11_ws_url)
        )
        self.onebot_v11_token = self._line_edit(
            self.data.get("OneBotV11_Access_Token", self.settings.onebot_v11_token)
        )
        self.onebot_v11_token.setEchoMode(QLineEdit.EchoMode.Password)

        onebot_form.addRow(onebot_title)
        onebot_form.addRow(onebot_hint)
        onebot_form.addRow(self.tr("WS 地址"), self.onebot_v11_ws_url)
        onebot_form.addRow(self.tr("Token"), self.onebot_v11_token)
        layout.addWidget(self.onebot_engine_panel)

        layout.addStretch()

        self._sync_engine_config_visibility()

        return widget

    def _selected_engine_key(self) -> str:
        if not hasattr(self, "notification_engine"):
            return normalize_notification_engine(self.settings.notification_engine)

        index = self.notification_engine.currentIndex()
        if 0 <= index < len(self.notification_engine_choices):
            return self.notification_engine_choices[index][0]
        return "auto"

    def _sync_engine_config_visibility(self, *_args):
        engine = self._selected_engine_key()
        if hasattr(self, "onebot_engine_panel"):
            self.onebot_engine_panel.setVisible(engine == ENGINE_ONEBOT_V11)

    def _sync_http_push_debug_enabled(self, *_args):
        if not hasattr(self, "http_push_enabled"):
            return

        enabled = self.http_push_enabled.isChecked()
        for widget_name in (
            "http_push_host",
            "http_push_port",
            "http_push_path",
            "http_push_token",
        ):
            widget = getattr(self, widget_name, None)
            if widget is not None:
                widget.setEnabled(enabled)

    def _create_rule_tab(self):
        """规则设置标签页"""
        widget = QWidget()
        layout = QVBoxLayout(widget)

        layout.addWidget(QLabel(self.tr("重要人物")))
        self.list_persons = self._create_list(
            self.data.get("Important_Persons", self.settings.important_persons)
        )
        layout.addWidget(self.list_persons)

        layout.addWidget(QLabel(self.tr("重要关键词")))
        self.list_keywords = self._create_list(
            self.data.get("Important_Keywords", self.settings.important_keywords)
        )
        layout.addWidget(self.list_keywords)

        layout.addWidget(QLabel(self.tr("黑名单")))
        self.list_black = self._create_list(self.data.get("BlackList", self.settings.blacklist))
        layout.addWidget(self.list_black)

        layout.addWidget(QLabel(self.tr("白名单")))
        self.list_white = self._create_list(self.data.get("WhiteList", self.settings.whitelist))
        layout.addWidget(self.list_white)

        self.someone_at_me = CheckBox(self.tr("当 [有人@我] 时将通知优先级设为最高"))
        self.someone_at_me.setChecked(self.data.get("Someone_At_Me", self.settings.someone_at_me))
        self.qq_only = CheckBox(self.tr("仅监控 QQ 消息（推荐）"))
        self.qq_only.setChecked(self.data.get("QQ_Only", self.settings.qq_only))
        layout.addWidget(self.someone_at_me)
        layout.addWidget(self.qq_only)

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
        self.notify_label = self._line_edit(
            self.data.get("Notify_Label", self.settings.notify_label)
        )

        # 图标选择
        self.notify_ok_layout = QHBoxLayout()
        self.notify_icon_ok = self._line_edit(self.data.get("icon_ok", self.settings.icon_ok))
        self.notify_ok_select = PushButton(self.tr("浏览"))
        self.notify_ok_select.clicked.connect(lambda: self._select_file(self.notify_icon_ok))
        self.notify_ok_layout.addWidget(self.notify_icon_ok)
        self.notify_ok_layout.addWidget(self.notify_ok_select)

        self.notify_dismiss_layout = QHBoxLayout()
        self.notify_icon_cancel = self._line_edit(
            self.data.get("icon_cancel", self.settings.icon_cancel)
        )
        self.notify_cancel_select = PushButton(self.tr("浏览"))
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
        self.notify_title_select.clicked.connect(lambda: self._select_file(self.notify_title_font))
        self.notify_title_layout.addWidget(self.notify_title_font)
        self.notify_title_layout.addWidget(self.notify_title_select)

        self.notify_message_layout = QHBoxLayout()
        self.notify_message_font = self._line_edit(
            self.data.get("Notify_Message_Font", self.settings.notify_message_font)
        )
        self.notify_message_select = PushButton(self.tr("浏览"))
        self.notify_message_select.clicked.connect(
            lambda: self._select_file(self.notify_message_font)
        )
        self.notify_message_layout.addWidget(self.notify_message_font)
        self.notify_message_layout.addWidget(self.notify_message_select)

        layout.addWidget(self.notify_shadow)
        layout.addWidget(self.notify_animation)
        layout.addWidget(self.notify_mask)
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

        self.max_wait = SpinBox()
        self.max_wait.setRange(1, 20)
        self.max_wait.setValue(
            self.data.get("Max_Wait_Thumb_Time", self.settings.max_wait_thumb_time)
        )

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
        form.addRow(self.tr("最大等待缩略图时间(s)"), self.max_wait)
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
        btn1.clicked.connect(lambda: self._select_file(self.sound_normal))
        btn3 = PushButton(self.tr("试听"))
        btn3.clicked.connect(lambda: self._test_sound(self.sound_normal))

        row1 = QHBoxLayout()
        row1.addWidget(self.sound_normal)
        row1.addWidget(btn1)
        row1.addWidget(btn3)

        self.sound_important = self._line_edit(
            self.data.get("Sound_Effect_Important", self.settings.sound_important)
        )
        btn2 = PushButton(self.tr("浏览"))
        btn2.clicked.connect(lambda: self._select_file(self.sound_important))
        btn4 = PushButton(self.tr("试听"))
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

        self.http_push_enabled = CheckBox(self.tr("启用 HTTP Push 调试引擎"))
        self.http_push_enabled.setChecked(
            self.data.get("HTTPPush_Enabled", self.settings.http_push_enabled)
        )
        self.http_push_enabled.stateChanged.connect(self._sync_http_push_debug_enabled)

        self.http_push_debug_hint = QLabel(
            self.tr(
                'HTTP Push 是调试页的辅助监听器，可与当前主引擎同时运行。测试 JSON：{"sender":"测试群","message":"测试消息"}'
            )
        )
        self.http_push_debug_hint.setWordWrap(True)

        self.http_push_host = self._line_edit(
            self.data.get("HTTPPush_Host", self.settings.http_push_host)
        )
        self.http_push_port = SpinBox()
        self.http_push_port.setRange(1, 65535)
        self.http_push_port.setValue(self.data.get("HTTPPush_Port", self.settings.http_push_port))
        self.http_push_path = self._line_edit(
            self.data.get("HTTPPush_Path", self.settings.http_push_path)
        )
        self.http_push_token = self._line_edit(
            self.data.get("HTTPPush_Token", self.settings.http_push_token)
        )
        self.http_push_token.setEchoMode(QLineEdit.EchoMode.Password)

        form.addRow(self.tr("HTTP Push"), self.http_push_enabled)
        form.addRow(self.http_push_debug_hint)
        form.addRow(self.tr("监听地址"), self.http_push_host)
        form.addRow(self.tr("监听端口"), self.http_push_port)
        form.addRow(self.tr("请求路径"), self.http_push_path)
        form.addRow(self.tr("Token"), self.http_push_token)
        self._sync_http_push_debug_enabled()

        return content

    def _create_about_tab(self):
        """关于标签页"""
        widget = QWidget()
        form = QFormLayout(widget)

        self.title = QLabel(self.tr("QQListener"))
        self.title.setStyleSheet("font-size: 20px; font-weight: 600;")
        self.subtitle = QLabel(self.tr("最好的QQ通知监控软件 - 班级群监控神器 v1.1 20260319"))
        self.subtitle.setStyleSheet("font-size: 16px")

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
        self.clear.clicked.connect(self._clear_cache)
        self.help = PushButton(self.tr("查看教程"))
        self.help.clicked.connect(lambda: webbrowser.open("https://xxtsoft.top/support/qqlistener"))
        self.translation = PushButton(self.tr("提交翻译"))
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

    def _create_list(self, items):
        """创建列表组件"""
        container = QWidget()
        layout = QVBoxLayout(container)

        list_widget = ListWidget()
        for item in items:
            list_widget.addItem(item)

        input_line = LineEdit()
        input_line.setPlaceholderText(self.tr("输入后点击添加。也可使用回车键"))
        input_line.returnPressed.connect(lambda: self._add_item(list_widget, input_line))
        btn_add = PushButton(self.tr("添加"))
        btn_remove = PushButton(self.tr("删除选中"))
        btn_add.clicked.connect(lambda: self._add_item(list_widget, input_line))
        btn_remove.clicked.connect(lambda: self._remove_item(list_widget))

        layout.addWidget(list_widget)

        btn_row = QHBoxLayout()
        btn_row.addWidget(input_line)
        btn_row.addWidget(btn_add)
        btn_row.addWidget(btn_remove)
        layout.addLayout(btn_row)
        container.list_widget = list_widget

        return container

    def _add_item(self, widget, line):
        """添加列表项"""
        text = line.text().strip()
        if not text:
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

    def _select_path(self):
        """选择文件夹"""
        path = QFileDialog.getExistingDirectory(self, self.tr("选择文件夹"))
        if path:
            self.tencent_path.setText(path)

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
        engine_index = self.notification_engine.currentIndex()
        if 0 <= engine_index < len(self.notification_engine_choices):
            notification_engine = self.notification_engine_choices[engine_index][0]
        else:
            notification_engine = "auto"

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
                "ScanInterval": self.scan_interval.value(),
                "Cooldown": self.cooldown.value(),
                "Auto_Start": auto_start_enabled,
                "Tencent_Files_Path": self.tencent_path.text(),
                "User_QQ": self.user_qq.text(),
                "NotificationEngine": notification_engine,
                "UIAMode": notification_engine == "uia",
                "OneBotV11_WS_URL": self.onebot_v11_ws_url.text(),
                "OneBotV11_Access_Token": self.onebot_v11_token.text(),
                "HTTPPush_Enabled": self.http_push_enabled.isChecked(),
                "HTTPPush_Host": self.http_push_host.text(),
                "HTTPPush_Port": self.http_push_port.value(),
                "HTTPPush_Path": self.http_push_path.text(),
                "HTTPPush_Token": self.http_push_token.text(),
                "Important_Persons": self._get_list(self.list_persons),
                "Important_Keywords": self._get_list(self.list_keywords),
                "BlackList": self._get_list(self.list_black),
                "WhiteList": self._get_list(self.list_white),
                "Sound_Effect_Normal": self.sound_normal.text(),
                "Sound_Effect_Important": self.sound_important.text(),
                "Auto_Show_Thumb": self.auto_thumb.isChecked(),
                "Always_On_Top": self.always_on_top.isChecked(),
                "Max_Wait_Thumb_Time": self.max_wait.value(),
                "Duration_Everyone": self.duration_everyone.value(),
                "Duration_Important": self.duration_important.value(),
                "Notify_Shadow": self.notify_shadow.isChecked(),
                "Notify_Animation": self.notify_animation.isChecked(),
                "Notify_Label": self.notify_label.text(),
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
                "QQ_Only": self.qq_only.isChecked(),
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
