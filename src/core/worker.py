import asyncio
import sys

from loguru import logger
from PySide6.QtCore import QThread, Signal

from src.core.notification_engines import (
    ENGINE_IDLE,
    EngineUnavailable,
    HTTPPushNotificationEngine,
    IdleNotificationEngine,
    build_engine_candidates,
    normalize_notification_engine,
)
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.utils.message_processor import MessageProcessor


class NotificationWorker(QThread):
    """通知监控工作线程"""

    notification_ready = Signal(dict)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.settings = get_settings()
        self.signals = get_signals()
        self.processor = MessageProcessor()
        self._running = True
        self._is_win11 = self._detect_win11()

    @property
    def is_win11(self) -> bool:
        return self._is_win11

    @staticmethod
    def _detect_win11() -> bool:
        if sys.platform != "win32" or not hasattr(sys, "getwindowsversion"):
            return False
        try:
            return sys.getwindowsversion().build >= 22000
        except Exception:
            logger.exception("检测 Windows 版本失败")
            return False

    def run(self):
        """线程主循环"""
        try:
            asyncio.run(self._run_selected_engine())
        except Exception:
            logger.exception("工作线程异常")

    def stop(self) -> bool:
        """停止工作线程"""
        self._running = False
        self.requestInterruption()
        if not self.isRunning():
            return True

        wait_ms = max(
            5000,
            int((self.settings.scan_interval + self.settings.max_wait_thumb_time) * 1000) + 1000,
        )
        stopped = self.wait(wait_ms)
        if not stopped:
            logger.warning("工作线程未在 {}ms 内停止", wait_ms)
        return stopped

    async def _run_selected_engine(self):
        preferred = normalize_notification_engine(self.settings.notification_engine)
        http_push_task = None

        if self.settings.http_push_enabled:
            http_push_task = asyncio.create_task(self._run_http_push_debug_engine())

        try:
            for engine_cls in build_engine_candidates(preferred):
                if not self._running:
                    return

                if engine_cls.key != ENGINE_IDLE:
                    reason = engine_cls.unavailable_reason()
                    if reason:
                        logger.warning("{}引擎不可用: {}", engine_cls.display_name, reason)
                        continue

                engine = engine_cls()
                logger.info("使用通知监听引擎: {}", engine.display_name)

                try:
                    await engine.run(self)
                    return
                except EngineUnavailable as exc:
                    logger.warning("{}引擎不可用: {}", engine.display_name, exc)
                except Exception:
                    logger.exception("{}引擎异常", engine.display_name)
                    if preferred != "auto":
                        break

            await IdleNotificationEngine("没有可用的通知监听引擎").run(self)
        finally:
            if http_push_task:
                http_push_task.cancel()
                await asyncio.gather(http_push_task, return_exceptions=True)

    async def _run_http_push_debug_engine(self):
        engine = HTTPPushNotificationEngine()
        reason = engine.unavailable_reason()
        if reason:
            logger.warning("{}不可用: {}", engine.display_name, reason)
            return

        logger.info("启用辅助调试引擎: {}", engine.display_name)
        try:
            await engine.run(self)
        except asyncio.CancelledError:
            raise
        except EngineUnavailable as exc:
            logger.warning("{}不可用: {}", engine.display_name, exc)
        except Exception:
            logger.exception("{}异常", engine.display_name)
