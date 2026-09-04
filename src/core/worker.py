import asyncio
import contextlib

import aiohttp
from loguru import logger

from src.core.core_controller import CoreState, get_core_state
from src.core.core_runtime import CoreRuntimeState, set_core_runtime
from src.core.pending_queue import pending_count, push_pending
from src.core.settings import get_settings
from src.core.signals import get_signals
from src.native.capture import RecvCapture, enumerate_qq_pids
from src.native.file_resolver import resolve_file_url
from src.native.message_sender import build_reply_route
from src.native.model import CapturedMessage, Segment, quoted_message
from src.native.profile_resolver import UserProfileNames, resolve_profile_blocking
from src.native.proto.message import append_rkey
from src.native.rkey_resolver import resolve_rkey, url_needs_rkey
from src.ui.qt_compat import QThread, Signal
from src.utils.media import download_url
from src.utils.message_processor import MessageProcessor

_NO_QQ_BACKOFF_S = 2.0
_ERROR_BACKOFF_S = 2.0
_STATE_POLL_S = 0.3
# 一条消息里最多预下载几张图：群里刷屏时别把带宽和磁盘吃光。
_MAX_INLINE_IMAGES = 4


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
        """RUNNING 与 PAUSED 都读管道（暂停只是不弹窗，消息进积压队列）；
        DETACHED 才真正停下。QQ 未开/管道未通时退避重试。"""
        while self._running:
            state = get_core_state()
            if state == CoreState.DETACHED:
                await asyncio.sleep(_STATE_POLL_S)
                continue
            if state == CoreState.PAUSED:
                self._publish_paused()

            pids = enumerate_qq_pids()
            if not pids:
                self._publish_runtime(CoreRuntimeState.NO_QQ, "未找到正在运行的 QQ 主进程")
                await self._sleep_or_state_change(_NO_QQ_BACKOFF_S)
                continue

            pid = pids[0]
            cap = RecvCapture(
                pid,
                self._on_captured,
                on_connected=lambda pid=pid: self._on_capture_connected(pid),
                on_disconnected=lambda pid=pid: self._on_capture_disconnected(pid),
            )
            watcher = asyncio.create_task(self._watch_core_state(cap, pid))
            try:
                await cap.run()
            except Exception:
                logger.debug("原生捕获中断（QQ 未注入/管道不可用）", exc_info=True)
                self._publish_runtime(
                    CoreRuntimeState.WAITING,
                    "核心尚未提供接收管道，正在重试",
                    pid,
                )
                await self._sleep_or_state_change(_ERROR_BACKOFF_S)
            finally:
                watcher.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await watcher

    @staticmethod
    def _publish_runtime(state: CoreRuntimeState, detail: str, pid: int = 0) -> None:
        """只有 RUNNING 时才上报运行态。

        暂停时捕获仍在跑（积压要用），但绝不能再写运行态——否则会和
        core_service 抢着写同一份快照，UI 就在"已暂停"和"接收管道已连接"
        之间来回跳。
        """
        if get_core_state() == CoreState.RUNNING:
            set_core_runtime(state, detail, pid)

    @staticmethod
    def _publish_paused(pid: int = 0) -> None:
        """暂停态由 worker 统一发布：只有它知道积压了多少条。"""
        queued = pending_count()
        detail = (
            f"监听已暂停，已积压 {queued} 条消息" if queued else "监听已暂停，核心仍保留在 QQ 中"
        )
        set_core_runtime(CoreRuntimeState.PAUSED, detail, pid)

    @staticmethod
    def _on_capture_connected(pid: int) -> None:
        logger.info("接收管道已连接: pid={}", pid)
        NotificationWorker._publish_runtime(
            CoreRuntimeState.CONNECTED, "接收管道已连接，正在监听消息", pid
        )

    @staticmethod
    def _on_capture_disconnected(pid: int) -> None:
        NotificationWorker._publish_runtime(
            CoreRuntimeState.WAITING, "接收管道已断开，正在重连", pid
        )

    async def _watch_core_state(self, cap: RecvCapture, pid: int = 0):
        """核心被卸载（DETACHED）或线程停止时，停掉当前捕获会话。

        暂停不停会话——不然积压就无从谈起，而且恢复时还要重连管道。
        """
        while True:
            await asyncio.sleep(_STATE_POLL_S)
            state = get_core_state()
            if not self._running or state == CoreState.DETACHED:
                cap.stop()
                return
            if state == CoreState.PAUSED:
                # 会话还连着，但界面要显示"已暂停 + 积压条数"。
                self._publish_paused(pid)

    async def _sleep_or_state_change(self, seconds: float) -> None:
        """退避等待期间核心状态一变就立刻醒。

        否则发完 pause/start 后界面要等满一个退避周期才更新，用起来像"指令没生效"。
        """
        started = get_core_state()
        remaining = seconds
        while remaining > 0 and self._running:
            await asyncio.sleep(min(_STATE_POLL_S, remaining))
            remaining -= _STATE_POLL_S
            if get_core_state() != started:
                return

    def _on_captured(self, msg: CapturedMessage):
        # RecvHookClient 在事件循环内同步回调；异步处理下载与推送。
        asyncio.create_task(self._handle_captured(msg))

    @staticmethod
    def _all_segments(msg: CapturedMessage) -> list[Segment]:
        """本条消息 + 被引用消息的全部段。"""
        segments = [seg for seg in msg.segments if seg.type != "reply"]
        for seg in msg.segments:
            if seg.type == "reply":
                segments.extend(seg.extra.get("segments") or [])
        return segments

    async def _handle_captured(self, msg: CapturedMessage):
        logger.debug(
            "收到消息 scene={} peer={} sender={} seq={} 元素={}",
            msg.scene,
            msg.peer_id,
            msg.sender_id,
            msg.raw_seq,
            [seg.type for seg in msg.segments],
        )
        await self._enrich_sender_profile(msg)
        await self._enrich_quoted_sender(msg)

        segments = self._all_segments(msg)
        await self._resolve_file_urls(msg, segments)
        await self._sign_media_urls(msg, segments)
        image_path = await self._download_inline_images(segments)

        data = self.processor.process_captured(
            msg, image_path=image_path, reply_route=build_reply_route(msg)
        )
        if not data:
            logger.debug("消息被规则过滤或去重，未生成通知 seq={}", msg.raw_seq)
            return
        self._dispatch(data)

    def _dispatch(self, data: dict) -> None:
        """暂停中就先压进积压队列，恢复监听时一起弹。"""
        if get_core_state() == CoreState.PAUSED:
            if self.settings.pause_queue_enabled:
                push_pending(data, self.settings.pause_queue_max)
                logger.info("暂停中，消息已积压：{}", data.get("Sender"))
            else:
                logger.debug("暂停中且未开启积压，丢弃：{}", data.get("Sender"))
            return
        logger.info("推送通知：{} | {}", data.get("Sender"), data.get("Message", "")[:40])
        self.notification_ready.emit(data)

    async def _resolve_file_urls(self, msg: CapturedMessage, segments: list[Segment]) -> None:
        for seg in segments:
            if seg.type != "file" or seg.url:
                continue
            try:
                seg.url = await resolve_file_url(msg, seg)
            except Exception:
                logger.debug("文件下载地址解析失败", exc_info=True)

    async def _sign_media_urls(self, msg: CapturedMessage, segments: list[Segment]) -> None:
        """NT 下载地址缺 rkey 时补签，否则 CDN 会以 invalid rkey 拒绝。"""
        is_group = msg.scene == "group"
        for seg in segments:
            if seg.type not in {"image", "video", "file"} or not url_needs_rkey(seg.url):
                continue
            try:
                rkey = await resolve_rkey(msg.source_pid, seg.url, is_group=is_group)
            except Exception:
                logger.debug("rkey 获取失败", exc_info=True)
                continue
            if rkey:
                seg.url = append_rkey(seg.url, rkey)
                logger.debug("已为 {} 补签 rkey", seg.type)
            else:
                logger.warning("未取到 rkey，{} 可能无法下载", seg.type)

    async def _download_inline_images(self, segments: list[Segment]) -> str | None:
        """预下载图片供通知内联渲染；返回首图路径（兼容旧的 Pic_Path 字段）。"""
        if not self.settings.auto_show_thumb:
            return None
        targets = [seg for seg in segments if seg.type == "image" and seg.url]
        if not targets:
            return None

        first_path: str | None = None
        try:
            async with aiohttp.ClientSession() as session:
                for seg in targets[:_MAX_INLINE_IMAGES]:
                    path = await download_url(session, seg.url, "image", seg.name or None)
                    if not path:
                        continue
                    seg.extra["local_path"] = path
                    logger.debug("图片已预取：{}", path)
                    if first_path is None:
                        first_path = path
        except Exception:
            logger.debug("图片预下载失败", exc_info=True)
        return first_path

    async def _enrich_sender_profile(self, msg: CapturedMessage) -> None:
        profile = await self._profile_for(msg.source_pid, msg.sender_id)
        if profile is None:
            return
        if profile.nickname:
            msg.sender_nickname = profile.nickname
        if profile.remark:
            msg.sender_remark = profile.remark

    async def _enrich_quoted_sender(self, msg: CapturedMessage) -> None:
        quoted = quoted_message(msg)
        if quoted is None or not quoted.sender_id:
            return
        profile = await self._profile_for(msg.source_pid, quoted.sender_id)
        name = ""
        if profile is not None:
            name = profile.remark or profile.nickname
        for seg in msg.segments:
            if seg.type == "reply":
                seg.extra["sender_name"] = name or quoted.sender_id
                break

    async def _profile_for(self, pid: int, user_id: str) -> UserProfileNames | None:
        user_id = str(user_id or "").strip()
        if not user_id or not pid:
            return None

        key = (pid, user_id)
        if key not in self._profile_cache:
            try:
                task = self._profile_tasks.get(key)
                if task is None:
                    task = asyncio.create_task(
                        asyncio.to_thread(resolve_profile_blocking, pid, user_id)
                    )
                    self._profile_tasks[key] = task
                self._profile_cache[key] = await task
            except Exception:
                logger.debug("资料解析失败: {}", user_id, exc_info=True)
                self._profile_cache[key] = UserProfileNames()
            finally:
                self._profile_tasks.pop(key, None)
        return self._profile_cache[key]
