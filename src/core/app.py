import pathlib
import sys
import tempfile

import pygame
from loguru import logger
from PySide6.QtCore import QFileSystemWatcher, QTimer, QTranslator
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication

from src.core.logging import setup_logging
from src.core.resources import app_icon_path
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.core.worker import NotificationWorker
from src.ui.fluent_dialog import show_fluent_message
from src.ui.notify_manager import get_notify_manager
from src.ui.settings_window import SettingsWindow
from src.ui.tray_icon import TrayIcon

APP_ICON_PATH = app_icon_path()
APP_USER_MODEL_ID = "Sylphy.QQListener"


class QQListenerApp:
    def __init__(self):
        self.app: QApplication | None = None
        self.settings = get_settings()
        self.signals = get_signals()
        self.worker: NotificationWorker | None = None
        self.settings_window: SettingsWindow | None = None
        self.tray_icon: TrayIcon | None = None
        self.translator: QTranslator | None = None
        self.notify_manager = get_notify_manager()
        self.settings_watcher: QFileSystemWatcher | None = None
        self.settings_reload_timer: QTimer | None = None
        self._macos_dock_icon_image = None

    def initialize(self) -> bool:
        setup_logging()
        self._set_windows_app_user_model_id()

        try:
            pygame.mixer.init()
        except Exception:
            logger.exception("初始化音频失败")

        self.app = QApplication(sys.argv)
        self._set_application_icon()
        self.app.setQuitOnLastWindowClosed(False)

        self._load_translator()
        self._watch_settings_file()

        self._connect_signals()

        self.worker = self._create_worker()

        self.tray_icon = TrayIcon()
        self.tray_icon.show_settings_signal.connect(self.show_settings)
        self.tray_icon.exit_signal.connect(self.exit)

        if not self.tray_icon.create():
            logger.error("创建托盘图标失败")

        return True

    def _set_windows_app_user_model_id(self):
        if sys.platform != "win32":
            return

        try:
            import ctypes

            set_app_id = ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID
            set_app_id.argtypes = [ctypes.c_wchar_p]
            set_app_id.restype = ctypes.c_long
            result = set_app_id(APP_USER_MODEL_ID)
            if result:
                logger.warning("设置 Windows AppUserModelID 返回: {}", result)
        except Exception:
            logger.exception("设置 Windows AppUserModelID 失败")

    def _set_application_icon(self):
        if not self.app:
            return

        icon_path = app_icon_path()
        icon = QIcon(str(icon_path))
        if icon.isNull():
            logger.warning("应用图标加载失败: {}", icon_path)
            return

        self.app.setApplicationName("QQListener")
        self.app.setApplicationDisplayName("QQListener")
        self.app.setOrganizationName("Sylphy")
        self.app.setWindowIcon(icon)
        self._set_macos_dock_icon(icon)

    def _set_macos_dock_icon(self, icon: QIcon):
        if sys.platform != "darwin":
            return

        try:
            from AppKit import NSApplication, NSImage

            pixmap = icon.pixmap(512, 512)
            if pixmap.isNull():
                pixmap = icon.pixmap(256, 256)

            image = None
            if not pixmap.isNull():
                dock_icon_path = Path(tempfile.gettempdir()) / "qqlistener-dock-icon.png"
                if pixmap.save(str(dock_icon_path), "PNG"):
                    image = NSImage.alloc().initWithContentsOfFile_(str(dock_icon_path))
                else:
                    logger.warning("保存 macOS Dock 图标失败: {}", dock_icon_path)

            if image is None or not image.isValid():
                image = NSImage.alloc().initWithContentsOfFile_(str(app_icon_path()))

            if image is None or not image.isValid():
                logger.warning("macOS Dock 图标加载失败")
                return

            NSApplication.sharedApplication().setApplicationIconImage_(image)
            self._macos_dock_icon_image = image
        except Exception:
            logger.exception("设置 macOS Dock 图标失败")

    def _load_translator(self):
        lang = self.settings.language
        if lang != "zh_CN" and self.app:
            self.translator = QTranslator()
            if self.translator.load(f"translations/{lang}.qm"):
                self.app.installTranslator(self.translator)

    def _connect_signals(self):
        self.signals.show_settings.connect(self.show_settings)
        self.signals.exit_app.connect(self.exit)
        self.signals.settings_changed.connect(self._on_settings_changed)

    def _watch_settings_file(self):
        if not self.app:
            return

        self.settings_watcher = QFileSystemWatcher(self.app)
        self.settings_watcher.fileChanged.connect(self._on_settings_file_changed)

        self.settings_reload_timer = QTimer(self.app)
        self.settings_reload_timer.setSingleShot(True)
        self.settings_reload_timer.timeout.connect(self._reload_settings_file)

        self._ensure_settings_watch_path()

    def _settings_file_path(self) -> str:
        return str(Path(self.settings.settings_file).resolve())

    def _ensure_settings_watch_path(self):
        if not self.settings_watcher:
            return

        settings_path = self._settings_file_path()
        if Path(settings_path).exists() and settings_path not in self.settings_watcher.files():
            self.settings_watcher.addPath(settings_path)

    def _on_settings_file_changed(self, _path: str):
        if self.settings_reload_timer:
            self.settings_reload_timer.start(250)

    def _reload_settings_file(self):
        if self.settings.reload():
            logger.info("配置文件已从磁盘重新加载")
            self._hot_reload_settings()
        self._ensure_settings_watch_path()

    def run(self):
        if not self.initialize():
            logger.error("初始化失败")
            sys.exit(1)

        if self.settings.is_first_run():
            if self.settings_window is None:
                self.settings_window = SettingsWindow()
            show_fluent_message(
                self.settings_window,
                self.settings_window.tr("你是新来的吧？"),
                self.settings_window.tr(
                    '这个程序配置较为复杂，所以建议你先看了教程再来用喵~\n请点击"关于"选项卡并点击"查看教程"按钮\n第一次保存设置后这条消息将不再出现\n\n\n本程序免费开源，如果你是花钱买的那一定是被骗了！'
                ),
            )
            self.show_settings()
        if self.worker:
            self.worker.start()
        exit_code = self.app.exec() if self.app else 1
        self.cleanup()
        sys.exit(exit_code)

    def show_settings(self):
        try:
            if self.settings_window is None:
                self.settings_window = SettingsWindow()
                self.settings_window.setWindowIcon(QIcon(str(app_icon_path())))

            self.settings_window.showNormal()
            self.settings_window.raise_()
            self.settings_window.activateWindow()
            handle = self.settings_window.windowHandle()
            if handle:
                handle.requestActivate()
            logger.info("设置窗口已显示")
        except RuntimeError:
            logger.warning("设置窗口对象失效，正在重建")
            self.settings_window = SettingsWindow()
            self.settings_window.setWindowIcon(QIcon(str(app_icon_path())))
            self.settings_window.showNormal()
            self.settings_window.raise_()
            self.settings_window.activateWindow()
        except Exception:
            logger.exception("显示设置窗口失败")

    def _on_notification_ready(self, data: dict):
        self.push_notification(data)

    def _create_worker(self) -> NotificationWorker:
        worker = NotificationWorker()
        worker.notification_ready.connect(self._on_notification_ready)
        return worker

    def _on_settings_changed(self):
        logger.info("设置已变更，正在热加载配置")
        self._hot_reload_settings()

    def _hot_reload_settings(self):
        if self.settings_window:
            self.settings_window.refresh_home()

        self._restart_worker()
        self._ensure_settings_watch_path()

    def _restart_worker(self):
        old_worker = self.worker
        if old_worker:
            try:
                old_worker.notification_ready.disconnect(self._on_notification_ready)
            except RuntimeError:
                pass

            stopped = old_worker.stop()
            if stopped:
                old_worker.deleteLater()
            else:
                old_worker.finished.connect(old_worker.deleteLater)

        self.worker = self._create_worker()
        self.worker.start()

    def push_notification(self, data: dict):
        try:
            self.notify_manager.show_notification(data)
        except Exception:
            logger.exception("推送通知失败")

    def exit(self):
        self.cleanup()
        if self.app:
            self.app.quit()

    def cleanup(self):
        self.notify_manager.close_all_notifications()
        if self.worker and self.worker.isRunning():
            self.worker.stop()

        if self.tray_icon:
            self.tray_icon.destroy()
        if self.settings_window:
            self.settings_window.close()
            self.settings_window = None


def run_app():
    app = QQListenerApp()
    app.run()
