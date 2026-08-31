import asyncio
import contextlib

import aiohttp
from loguru import logger

from src.core.core_controller import CoreState, get_core_state
from src.core.core_runtime import CoreRuntimeState, set_core_runtime
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.native.capture import RecvCapture, enumerate_qq_pids
from src.native.file_resolver import resolve_file_url
from src.native.model import CapturedMessage
from src.native.profile_resolver import UserProfileNames, resolve_user_profile
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
        self._profile_cache: dict[tuple[int, str], UserProfileNames] = {}
        self._profile_tasks: dict[tuple[int, str], asyncio.Task[UserProfileNames]] = {}
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
                set_core_runtime(CoreRuntimeState.NO_QQ, "未找到正在运行的 QQ 主进程")
                await asyncio.sleep(_NO_QQ_BACKOFF_S)
                continue

            pid = pids[0]
            cap = RecvCapture(
                pid,
                self._on_captured,
                on_connected=lambda pid=pid: self._on_capture_connected(pid),
                on_disconnected=lambda pid=pid: self._on_capture_disconnected(pid),
            )
            watcher = asyncio.create_task(self._watch_core_state(cap))
            try:
                await cap.run()
            except Exception:
                logger.debug("原生捕获中断（QQ 未注入/管道不可用）", exc_info=True)
                if get_core_state() == CoreState.RUNNING:
                    set_core_runtime(
                        CoreRuntimeState.WAITING,
                        "核心尚未提供接收管道，正在重试",
                        pid,
                    )
                await asyncio.sleep(_ERROR_BACKOFF_S)
            finally:
                watcher.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await watcher

    @staticmethod
    def _on_capture_connected(pid: int) -> None:
        logger.info("接收管道已连接: pid={}", pid)
        set_core_runtime(CoreRuntimeState.CONNECTED, "接收管道已连接，正在监听消息", pid)

    @staticmethod
    def _on_capture_disconnected(pid: int) -> None:
        if get_core_state() == CoreState.RUNNING:
            set_core_runtime(CoreRuntimeState.WAITING, "接收管道已断开，正在重连", pid)

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
        await self._enrich_sender_profile(msg)
        image_path = None
        image_seg = next((s for s in msg.segments if s.type == "image" and s.url), None)
        file_seg = next((s for s in msg.segments if s.type == "file"), None)
        if file_seg is not None and not file_seg.url:
            try:
                file_seg.url = await resolve_file_url(msg, file_seg)
            except Exception:
                logger.debug("文件下载地址解析失败", exc_info=True)

        if image_seg is not None:
            try:
                async with aiohttp.ClientSession() as session:
                    if self.settings.auto_show_thumb:
                        image_path = await download_url(session, image_seg.url, "image")
            except Exception:
                logger.debug("附件下载失败", exc_info=True)

        data = self.processor.process_captured(msg, image_path=image_path)
        if data:
            self.notification_ready.emit(data)

    async def _enrich_sender_profile(self, msg: CapturedMessage) -> None:
        user_id = str(msg.sender_id or "").strip()
        if not user_id or not msg.source_pid:
            return

        key = (msg.source_pid, user_id)
        if key not in self._profile_cache:
            try:
                task = self._profile_tasks.get(key)
                if task is None:
                    task = asyncio.create_task(resolve_user_profile(msg))
                    self._profile_tasks[key] = task
                self._profile_cache[key] = await task
            except Exception:
                logger.debug("发送者资料解析失败", exc_info=True)
                self._profile_cache[key] = UserProfileNames()
            finally:
                self._profile_tasks.pop(key, None)

        profile = self._profile_cache[key]
        if profile.nickname:
            msg.sender_nickname = profile.nickname
        if profile.remark:
            msg.sender_remark = profile.remark
