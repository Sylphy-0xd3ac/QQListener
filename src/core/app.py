import contextlib
import os
import sys
import tempfile
from pathlib import Path

import src.utils.filtered_print

with src.utils.filtered_print.filtered_print():
    import pygame

from loguru import logger

from src.core.core_controller import (
    CoreState,
    add_core_state_listener,
    get_core_state,
    is_core_running,
    remove_core_state_listener,
    set_core_state,
    toggle_core,
    unload_core,
)
from src.core.core_runtime import get_core_runtime
from src.core.core_service import CoreService
from src.core.crash_handler import install_crash_logging
from src.core.ipc import ControlServer, send_command
from src.core.logging import setup_logging
from src.core.pending_queue import clear_pending, drain_pending, pending_count
from src.core.resources import app_icon_path, app_icon_png_path
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.core.worker import NotificationWorker
from src.ui.fluent_dialog import show_fluent_message
from src.ui.notify_manager import get_notify_manager
from src.ui.qt_compat import (
    QApplication,
    QFileSystemWatcher,
    QIcon,
    QTimer,
    QTranslator,
    load_icon,
)
from src.ui.settings_window import SettingsWindow
from src.ui.status_ball import FloatingStatusBall
from src.ui.tray_icon import TrayIcon
from src.utils.message_processor import build_digest_payload

APP_ICON_PATH = app_icon_path()
APP_USER_MODEL_ID = "Sylphy.QQListener"


class QQListenerApp:
    def __init__(self):
        self.app: QApplication | None = None
        self.settings = get_settings()
        self.signals = get_signals()
        self.worker: NotificationWorker | None = None
        self.core_service: CoreService | None = None
        self.settings_window: SettingsWindow | None = None
        self.tray_icon: TrayIcon | None = None
        self.status_ball: FloatingStatusBall | None = None
        self.translator: QTranslator | None = None
        self.notify_manager = get_notify_manager()
        self.settings_watcher: QFileSystemWatcher | None = None
        self.settings_reload_timer: QTimer | None = None
        self.control_server: ControlServer | None = None
        self.daemon = True
        self._macos_dock_icon_image = None

    def initialize(self) -> bool:
        setup_logging()
        install_crash_logging()
        self._set_windows_app_user_model_id()

        try:
            pygame.mixer.init()
        except Exception:
            logger.exception("初始化音频失败")

        self.app = QApplication.instance() or QApplication(sys.argv)
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

        self._sync_status_ball()

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
        icon = load_icon(icon_path, app_icon_png_path())
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
        add_core_state_listener(self._on_core_state_changed)

    def _on_core_state_changed(self, state: CoreState):
        """恢复监听时把暂停期间攒下的消息倒出来；卸载核心则直接丢弃。"""
        logger.info("核心状态切换为 {}", state.value)
        if state == CoreState.DETACHED:
            clear_pending()
            return
        if state != CoreState.RUNNING or not pending_count():
            return
        # 状态可能由后台线程切换，统一回主线程再建窗口。
        QTimer.singleShot(0, self._flush_pending)

    def _flush_pending(self):
        payloads, dropped = drain_pending()
        digest = build_digest_payload(payloads, dropped)
        if digest is None:
            return
        logger.info("恢复监听，弹出积压消息 {} 条（丢弃 {} 条）", len(payloads), dropped)
        self.push_notification(digest)

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

    def run(self, *, daemon: bool = True):
        self.daemon = daemon
        # 先装日志与崩溃钩子：QApplication 创建期间出事也得留下记录。
        setup_logging()
        install_crash_logging()
        # QLocalServer/QLocalSocket 需要一个 Qt 应用对象；单实例检查也要用它，
        # 所以先建 app，再决定这一次到底是"启动"还是"唤起已有实例"。
        self.app = QApplication.instance() or QApplication(sys.argv)
        if not self._start_control_server():
            print("QQListener 已在运行" + ("" if daemon else "，已打开其设置窗口"))
            return

        if not self.initialize():
            logger.error("初始化失败")
            sys.exit(1)

        if not daemon and not self.settings.is_first_run():
            self.show_settings()

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
        self._maybe_run_core_setup()
        self.core_service = CoreService()
        self.core_service.start()
        if self.worker:
            self.worker.start()
        exit_code = self.app.exec() if self.app else 1
        self.cleanup()
        sys.exit(exit_code)

    # ---------- 控制通道 ----------

    def _start_control_server(self) -> bool:
        """返回 False 表示已有实例在跑，本次不该再启动一份。"""
        server = ControlServer(self._handle_control, self.app)
        if server.listen():
            self.control_server = server
            return True
        if server.other_instance:
            send_command("ping" if self.daemon else "show", timeout_ms=1500)
            return False
        logger.warning("控制通道未启用，命令行指令将不可用")
        return True

    def _handle_control(self, command: str, request: dict) -> dict:
        """守护进程指令入口（在 Qt 主线程上执行）。"""
        if command == "ping":
            return {"pid": os.getpid()}
        if command == "status":
            return self._status_payload()
        if command == "start":
            set_core_state(CoreState.RUNNING)
        elif command == "pause":
            set_core_state(CoreState.PAUSED)
        elif command == "toggle":
            toggle_core()
        elif command == "unload":
            unload_core()
        elif command == "show":
            self.show_settings()
        elif command == "reload":
            if self.settings.reload():
                self._hot_reload_settings()
        elif command == "quit":
            QTimer.singleShot(0, self.exit)
            return {"stopping": True}
        return self._status_payload()

    def _status_payload(self) -> dict:
        runtime = get_core_runtime()
        return {
            "pid": os.getpid(),
            "daemon": self.daemon,
            "core_state": get_core_state().value,
            "runtime_state": runtime.state.value,
            "runtime_detail": runtime.detail,
            "qq_pid": runtime.pid,
            "worker_running": bool(self.worker and self.worker.isRunning()),
            "pending_messages": pending_count(),
            "settings_file": self.settings.settings_file,
        }

    def _maybe_run_core_setup(self):
        """受支持平台首次运行：要求阅读同意 SnowLuma 条款并安装核心。"""
        from src.core.core_updater import needs_core_setup

        if not needs_core_setup(self.settings):
            return
        try:
            from src.ui.core_setup_dialog import CoreSetupDialog

            dialog = CoreSetupDialog(self.settings, parent=self.settings_window)
            dialog.exec()
        except Exception:
            logger.exception("核心安装向导失败")

    def show_settings(self):
        try:
            if self.settings_window is None:
                self.settings_window = SettingsWindow()
                self.settings_window.setWindowIcon(load_icon(app_icon_path(), app_icon_png_path()))

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
            self.settings_window.setWindowIcon(load_icon(app_icon_path(), app_icon_png_path()))
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

        self._sync_status_ball()
        self._restart_worker()
        self._ensure_settings_watch_path()

    def _sync_status_ball(self):
        if not self.settings.show_status_ball:
            self._destroy_status_ball()
            return

        if self.status_ball is None:
            self.status_ball = FloatingStatusBall()
            self.status_ball.show_settings_requested.connect(self.show_settings)
            self.status_ball.unload_requested.connect(self._request_unload_core)

        self.status_ball.refresh_state()
        if not self.status_ball.isVisible():
            self.status_ball.show()

    def _restart_worker(self):
        old_worker = self.worker
        if old_worker:
            with contextlib.suppress(RuntimeError):
                old_worker.notification_ready.disconnect(self._on_notification_ready)

            stopped = old_worker.stop()
            if stopped:
                old_worker.deleteLater()
            else:
                old_worker.finished.connect(old_worker.deleteLater)

        self.worker = self._create_worker()
        self.worker.start()

    def _request_unload_core(self):
        parent = self.settings_window if self.settings_window is not None else None
        confirmed = show_fluent_message(
            parent,
            "卸载核心",
            "将从 QQ 进程卸载注入的钩子，需重新启动核心才能恢复监听。确定卸载？",
            yes_text="卸载",
            cancel_text="取消",
        )
        if confirmed:
            unload_core()

    def push_notification(self, data: dict):
        if not is_core_running():
            logger.debug("核心未运行，跳过通知显示：{}", data.get("Sender"))
            return

        try:
            self.notify_manager.show_notification(data)
        except Exception:
            logger.exception("推送通知失败")

    def exit(self):
        self.cleanup()
        if self.app:
            self.app.quit()

    def cleanup(self):
        remove_core_state_listener(self._on_core_state_changed)
        if self.control_server is not None:
            self.control_server.close()
            self.control_server = None
        self.notify_manager.close_all_notifications()
        if self.worker and self.worker.isRunning():
            self.worker.stop()
        if self.core_service:
            self.core_service.stop(unload_owned=True)
            self.core_service = None

        if self.tray_icon:
            self.tray_icon.destroy()
        self._destroy_status_ball()
        if self.settings_window:
            self.settings_window.close()
            self.settings_window = None

    def _destroy_status_ball(self):
        if not self.status_ball:
            return

        ball = self.status_ball
        self.status_ball = None
        ball.close()
        ball.deleteLater()


def run_app(*, daemon: bool = True):
    app = QQListenerApp()
    app.run(daemon=daemon)
