import asyncio
import contextlib

import aiohttp
from loguru import logger

from src.core.core_controller import CoreState, get_core_state
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.native.capture import RecvCapture, enumerate_qq_pids
from src.native.model import CapturedMessage
from src.ui.qt_compat import QThread, Signal
from src.utils.media import download_url
from src.utils.message_processor import MessageProcessor

_NO_QQ_BACKOFF_S = 2.0
_ERROR_BACKOFF_S = 2.0
_STATE_POLL_S = 0.3


class NotificationWorker(QThread):
    """通知监控工作线程：核心态驱动的原生捕获。"""

    notification_ready = Signal(dict)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.settings = get_settings()
        self.signals = get_signals()
        self.processor = MessageProcessor()
        self._running = True

    def run(self):
        """线程主循环。"""
        try:
            asyncio.run(self._run_native_capture())
        except Exception:
            logger.exception("工作线程异常")

    def stop(self) -> bool:
        """停止工作线程。"""
        self._running = False
        self.requestInterruption()
        if not self.isRunning():
            return True

        stopped = self.wait(5000)
        if not stopped:
            logger.warning("工作线程未在 5000ms 内停止")
        return stopped

    async def _run_native_capture(self):
        """核心运行时才捕获；QQ 未开/管道未通时退避重试。"""
        while self._running:
            if get_core_state() != CoreState.RUNNING:
                await asyncio.sleep(_STATE_POLL_S)
                continue

            pids = enumerate_qq_pids()
            if not pids:
                await asyncio.sleep(_NO_QQ_BACKOFF_S)
                continue

            cap = RecvCapture(pids[0], self._on_captured)
            watcher = asyncio.create_task(self._watch_core_state(cap))
            try:
                await cap.run()
            except Exception:
                logger.debug("原生捕获中断（QQ 未注入/管道不可用）", exc_info=True)
                await asyncio.sleep(_ERROR_BACKOFF_S)
            finally:
                watcher.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await watcher

    async def _watch_core_state(self, cap: RecvCapture):
        """核心离开 RUNNING（暂停/卸载）或线程停止时，停掉当前捕获会话。"""
        while True:
            await asyncio.sleep(_STATE_POLL_S)
            if not self._running or get_core_state() != CoreState.RUNNING:
                cap.stop()
                return

    def _on_captured(self, msg: CapturedMessage):
        # RecvHookClient 在事件循环内同步回调；异步处理下载与推送。
        asyncio.create_task(self._handle_captured(msg))

    async def _handle_captured(self, msg: CapturedMessage):
        image_path = None
        file_path = None
        image_seg = next((s for s in msg.segments if s.type == "image" and s.url), None)
        # 群文件推送里不带 URL（需 OIDB 换地址）；仅当 segment 已带 url 时才下载。
        file_seg = next((s for s in msg.segments if s.type == "file" and s.url), None)
        if image_seg is not None or file_seg is not None:
            try:
                async with aiohttp.ClientSession() as session:
                    if image_seg is not None and self.settings.auto_show_thumb:
                        image_path = await download_url(session, image_seg.url, "image")
                    if file_seg is not None:
                        file_path = await download_url(
                            session, file_seg.url, "file", filename=file_seg.name or None
                        )
            except Exception:
                logger.debug("附件下载失败", exc_info=True)

        data = self.processor.process_captured(
            msg, image_path=image_path, file_path=file_path
        )
        if data:
            self.notification_ready.emit(data)
