from __future__ import annotations

import asyncio
import hashlib
import importlib.util
import json
import mimetypes
import os
import re
import sys
import tempfile
import uuid
from abc import ABC, abstractmethod
from pathlib import Path
from typing import TYPE_CHECKING
from urllib.parse import unquote, urlparse

from loguru import logger

from src.core.notification_state import is_notifications_muted

if TYPE_CHECKING:
    from src.core.worker import NotificationWorker


ENGINE_AUTO = "auto"
ENGINE_WINSDK = "winsdk"
ENGINE_UIA = "uia"
ENGINE_ONEBOT_V11 = "onebot_v11"
ENGINE_HTTP_PUSH = "http_push"
ENGINE_IDLE = "idle"

ENGINE_CHOICES = (
    (ENGINE_AUTO, "自动选择"),
    (ENGINE_WINSDK, "WinSDK 通知中心"),
    (ENGINE_UIA, "UI Automation"),
    (ENGINE_ONEBOT_V11, "OneBot V11（正向）"),
    (ENGINE_IDLE, "空跑（不监听）"),
)

VALID_NOTIFICATION_ENGINES = {key for key, _ in ENGINE_CHOICES}


class EngineUnavailable(RuntimeError):
    """Raised when an engine cannot be initialized on the current machine."""


def normalize_notification_engine(value: object, legacy_uia: bool = False) -> str:
    if isinstance(value, str):
        key = value.strip().lower().replace("-", "_")
        aliases = {
            "default": ENGINE_AUTO,
            "disabled": ENGINE_IDLE,
            "none": ENGINE_IDLE,
            "dry_run": ENGINE_IDLE,
            "win_sdk": ENGINE_WINSDK,
            "winrt": ENGINE_WINSDK,
            "ui_automation": ENGINE_UIA,
            "uiamode": ENGINE_UIA,
            "ob11": ENGINE_ONEBOT_V11,
            "obv11": ENGINE_ONEBOT_V11,
            "onebot": ENGINE_ONEBOT_V11,
            "onebot-v11": ENGINE_ONEBOT_V11,
            "onebot_v11_forward": ENGINE_ONEBOT_V11,
            "onebot_v11_ws": ENGINE_ONEBOT_V11,
        }
        key = aliases.get(key, key)
        if key in VALID_NOTIFICATION_ENGINES:
            return key

    if legacy_uia:
        return ENGINE_UIA
    return ENGINE_AUTO


def _has_package(package: str) -> bool:
    return importlib.util.find_spec(package) is not None


class NotificationEngine(ABC):
    key = ""
    display_name = ""
    required_packages: tuple[str, ...] = ()
    windows_only = True

    @classmethod
    def unavailable_reason(cls) -> str:
        if cls.windows_only and sys.platform != "win32":
            return "只支持 Windows"

        missing = [package for package in cls.required_packages if not _has_package(package)]
        if missing:
            return f"缺少依赖: {', '.join(missing)}"

        return ""

    @abstractmethod
    async def run(self, worker: NotificationWorker) -> None:
        """Run the engine until the worker stops or the engine becomes unavailable."""


class IdleNotificationEngine(NotificationEngine):
    key = ENGINE_IDLE
    display_name = "空跑"
    required_packages = ()
    windows_only = False

    def __init__(self, reason: str = "") -> None:
        self.reason = reason

    async def run(self, worker: NotificationWorker) -> None:
        if self.reason:
            logger.warning("通知监听空跑: {}", self.reason)
        else:
            logger.info("通知监听已禁用，应用将保持运行")

        while worker._running:
            await asyncio.sleep(max(worker.settings.scan_interval, 1.0))


class UIAutomationNotificationEngine(NotificationEngine):
    key = ENGINE_UIA
    display_name = "UI Automation"
    required_packages = ("uiautomation",)

    async def run(self, worker: NotificationWorker) -> None:
        try:
            import uiautomation as auto
        except Exception as exc:
            raise EngineUnavailable("UI Automation 加载失败") from exc

        while worker._running:
            try:
                current_found_keys: set[str] = set()
                toasts = self._get_toasts(worker, auto)

                for texts in toasts:
                    if not texts or not isinstance(texts, list):
                        continue

                    norm = [" ".join(t.split()) for t in texts if t and isinstance(t, str)]
                    if not norm:
                        continue

                    key = hashlib.md5("|".join(norm).encode("utf-8")).hexdigest()
                    current_found_keys.add(key)

                    if is_notifications_muted():
                        worker.processor.active_toasts.add(key)
                        continue

                    result = worker.processor.process_notification(texts)
                    if result and isinstance(result, dict):
                        worker.notification_ready.emit(result)

                worker.processor.update_active_toasts(current_found_keys)

            except Exception:
                logger.exception("UIA异常")

            await asyncio.sleep(worker.settings.scan_interval)

    def _get_toasts(self, worker: NotificationWorker, auto) -> list[list[str]]:
        try:
            desktop = auto.GetRootControl()
        except Exception:
            return []

        texts_list: list[list[str]] = []

        if not worker.is_win11:
            try:
                for pane in desktop.GetChildren():
                    if not pane or pane.ClassName != "Windows.UI.Core.CoreWindow":
                        continue
                    for win in pane.GetChildren():
                        if not win or win.ControlTypeName != "WindowControl":
                            continue
                        try:
                            childs = win.GetChildren()
                            texts = [
                                c.Name
                                for c in childs
                                if c and c.ControlTypeName == "TextControl" and c.Name
                            ]
                            if len(texts) >= 2:
                                texts_list.append(texts)
                        except Exception:
                            continue
            except Exception:
                logger.exception("获取UIA通知失败")
        else:
            try:
                container = auto.WindowControl(
                    searchDepth=1,
                    ClassName="Windows.UI.Core.CoreWindow",
                    Name="新通知",
                )
                if container and container.Exists(0):
                    for toast, _ in auto.WalkControl(container, maxDepth=3):
                        if not toast or toast.ClassName != "FlexibleToastView":
                            continue
                        try:
                            childs = toast.GetChildren()
                            texts = [
                                c.Name
                                for c in childs
                                if c
                                and c.ControlTypeName == "TextControl"
                                and c.Name
                                and len(c.Name) > 1
                            ]
                            if len(texts) >= 2:
                                texts_list.append(texts)
                        except Exception:
                            continue
            except Exception:
                logger.exception("获取Win11 UIA通知失败")

        return texts_list


IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"}
DOCUMENT_ICON_MAP = {
    ".pdf": "asset/FileIcon/pdf.png",
    ".ppt": "asset/FileIcon/powerpoint.png",
    ".pptx": "asset/FileIcon/powerpoint.png",
    ".xls": "asset/FileIcon/excel.png",
    ".xlsx": "asset/FileIcon/excel.png",
    ".doc": "asset/FileIcon/word.png",
    ".docx": "asset/FileIcon/word.png",
}


def _is_http_url(value: object) -> bool:
    return isinstance(value, str) and value.startswith(("http://", "https://"))


def _local_path_from_ref(value: object) -> str | None:
    if not isinstance(value, str) or not value:
        return None

    if value.startswith("file://"):
        parsed = urlparse(value)
        path = unquote(parsed.path)
        if sys.platform == "win32" and path.startswith("/") and re.match(r"^/[a-zA-Z]:", path):
            path = path[1:]
        return path if os.path.exists(path) else None

    return value if os.path.exists(value) else None


def _safe_filename(value: object, fallback: str) -> str:
    if isinstance(value, str) and value:
        name = os.path.basename(unquote(urlparse(value).path)) or os.path.basename(value)
        name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")
        if name:
            return name
    return fallback


def _file_icon_for_path(path: str) -> str:
    ext = Path(path).suffix.lower()
    return DOCUMENT_ICON_MAP.get(ext, "asset/FileIcon/enb.png")


async def _download_url(session, url: str, category: str, filename: str | None = None) -> str | None:
    if not _is_http_url(url):
        return None

    try:
        async with session.get(url, timeout=20) as response:
            if response.status >= 400:
                logger.warning("下载附件失败: {} {}", response.status, url)
                return None

            content = await response.read()
            if not content:
                return None

            content_type = response.headers.get("Content-Type", "").split(";")[0].strip()
            ext = Path(filename or "").suffix
            if not ext:
                ext = mimetypes.guess_extension(content_type) or Path(urlparse(url).path).suffix
            if not ext:
                ext = ".bin"

            safe_name = _safe_filename(filename or url, f"{uuid.uuid4().hex}{ext}")
            if not Path(safe_name).suffix:
                safe_name = f"{safe_name}{ext}"

            media_dir = Path(tempfile.gettempdir()) / "qqlistener_media" / category
            media_dir.mkdir(parents=True, exist_ok=True)
            path = media_dir / f"{uuid.uuid4().hex}_{safe_name}"
            path.write_bytes(content)
            return str(path)
    except Exception:
        logger.exception("下载附件异常: {}", url)
        return None


def _clean_cq_code_text(text: str) -> str:
    replacements = {
        "image": "[图片]",
        "record": "[语音]",
        "video": "[视频]",
        "file": "[文件]",
        "face": "[表情]",
    }

    def replace(match: re.Match[str]) -> str:
        cq_type = match.group("type")
        if cq_type == "at":
            qq = match.group("body")
            target = ""
            if qq:
                target_match = re.search(r"(?:^|,)qq=([^,\]]+)", qq)
                if target_match:
                    target = target_match.group(1)
            return f"[@{target}]" if target else "[@]"
        return replacements.get(cq_type, "")

    return re.sub(r"\[CQ:(?P<type>[a-zA-Z0-9_]+)(?P<body>[^\]]*)\]", replace, text)


def _format_onebot_v11_message(message: object, raw_message: object = None) -> str:
    if isinstance(raw_message, str) and raw_message:
        return _clean_cq_code_text(raw_message).strip()

    if isinstance(message, str):
        return _clean_cq_code_text(message).strip()

    if not isinstance(message, list):
        return str(message).strip() if message is not None else ""

    parts: list[str] = []
    for segment in message:
        if not isinstance(segment, dict):
            if segment is not None:
                parts.append(str(segment))
            continue

        segment_type = str(segment.get("type", "")).strip()
        data = segment.get("data")
        data = data if isinstance(data, dict) else {}

        if segment_type == "text":
            parts.append(str(data.get("text", "")))
        elif segment_type == "at":
            qq = data.get("qq", "")
            parts.append(f"[@{qq}]" if qq else "[@]")
        elif segment_type == "image":
            parts.append("[图片]")
        elif segment_type == "record":
            parts.append("[语音]")
        elif segment_type == "video":
            parts.append("[视频]")
        elif segment_type == "file":
            name = data.get("name") or data.get("file") or data.get("file_name") or ""
            parts.append(f"[文件] {name}".strip())
        elif segment_type:
            parts.append(f"[{segment_type}]")

    return "".join(parts).strip()


def _iter_onebot_v11_segments(event: dict) -> list[dict]:
    message = event.get("message")
    if not isinstance(message, list):
        return []
    return [segment for segment in message if isinstance(segment, dict)]


def _onebot_v11_mentions_me(event: dict, user_qq: str) -> bool:
    if not user_qq:
        return False

    message = event.get("message")
    if isinstance(message, list):
        for segment in message:
            if not isinstance(segment, dict) or segment.get("type") != "at":
                continue
            data = segment.get("data")
            if not isinstance(data, dict):
                continue
            qq = str(data.get("qq", "")).strip()
            if qq in {user_qq, "all"}:
                return True

    raw_message = event.get("raw_message")
    if isinstance(raw_message, str):
        return f"[CQ:at,qq={user_qq}" in raw_message or "[CQ:at,qq=all" in raw_message

    return False


def _onebot_v11_event_to_texts(event: dict, user_qq: str = "") -> list[str] | None:
    if event.get("post_type") != "message":
        return None

    message_text = _format_onebot_v11_message(event.get("message"), event.get("raw_message"))
    if not message_text:
        return None

    sender_info = event.get("sender")
    sender_info = sender_info if isinstance(sender_info, dict) else {}
    sender_name = (
        sender_info.get("card")
        or sender_info.get("nickname")
        or sender_info.get("user_id")
        or event.get("user_id")
        or "OneBot"
    )
    sender = str(sender_name)

    if event.get("message_type") == "group":
        group_id = event.get("group_id", "")
        sender = f"群 {group_id} | {sender}" if group_id else f"群消息 | {sender}"
    elif event.get("message_type") == "private":
        sender = f"私聊 | {sender}"

    if _onebot_v11_mentions_me(event, str(user_qq).strip()):
        sender = f"有人@我 {sender}"

    return [sender, message_text]


class OneBotV11ForwardNotificationEngine(NotificationEngine):
    key = ENGINE_ONEBOT_V11
    display_name = "OneBot V11（正向）"
    required_packages = ("aiohttp",)
    windows_only = False

    def __init__(self) -> None:
        self._pending_actions: dict[str, asyncio.Future] = {}

    async def run(self, worker: NotificationWorker) -> None:
        try:
            import aiohttp
        except Exception as exc:
            raise EngineUnavailable("aiohttp 加载失败") from exc

        ws_url = worker.settings.onebot_v11_ws_url
        if not ws_url:
            raise EngineUnavailable("未配置 OneBot V11 正向 WebSocket 地址")
        if not ws_url.startswith(("ws://", "wss://")):
            raise EngineUnavailable("OneBot V11 WebSocket 地址必须以 ws:// 或 wss:// 开头")

        headers = {}
        token = worker.settings.onebot_v11_token.strip()
        if token:
            headers["Authorization"] = f"Bearer {token}"

        timeout = aiohttp.ClientTimeout(total=None, sock_connect=15, sock_read=None)
        reconnect_interval = max(worker.settings.scan_interval, 3.0)

        while worker._running:
            try:
                async with aiohttp.ClientSession(timeout=timeout) as session:
                    async with session.ws_connect(
                        ws_url,
                        headers=headers,
                        heartbeat=30,
                        autoping=True,
                    ) as ws:
                        logger.info("已连接 OneBot V11 正向 WebSocket: {}", ws_url)
                        async for msg in ws:
                            if not worker._running:
                                break

                            if msg.type == aiohttp.WSMsgType.TEXT:
                                await self._handle_payload(worker, session, ws, msg.data)
                            elif msg.type in {
                                aiohttp.WSMsgType.CLOSE,
                                aiohttp.WSMsgType.CLOSING,
                                aiohttp.WSMsgType.CLOSED,
                            }:
                                break
                            elif msg.type == aiohttp.WSMsgType.ERROR:
                                logger.warning("OneBot V11 WebSocket 错误: {}", ws.exception())
                                break
            except aiohttp.WSServerHandshakeError as exc:
                logger.warning("OneBot V11 WebSocket 握手失败: {}", exc)
            except OSError as exc:
                logger.warning("OneBot V11 WebSocket 连接失败: {}", exc)
            except Exception:
                logger.exception("OneBot V11 引擎异常")

            if worker._running:
                await asyncio.sleep(reconnect_interval)

    async def _handle_payload(self, worker: NotificationWorker, session, ws, payload: str) -> None:
        try:
            event = json.loads(payload)
        except json.JSONDecodeError:
            logger.warning("忽略无效 OneBot V11 事件: {}", payload[:200])
            return

        if not isinstance(event, dict):
            return

        if self._resolve_action_response(event):
            return

        await self._handle_event(worker, session, ws, event)

    def _resolve_action_response(self, payload: dict) -> bool:
        echo = payload.get("echo")
        if not echo:
            return False

        future = self._pending_actions.pop(str(echo), None)
        if not future:
            return False

        if not future.done():
            future.set_result(payload)
        return True

    async def _call_action(self, ws, action: str, params: dict, timeout: float = 8.0) -> dict | None:
        echo = f"qqlistener-{uuid.uuid4().hex}"
        loop = asyncio.get_running_loop()
        future = loop.create_future()
        self._pending_actions[echo] = future

        try:
            await ws.send_json({"action": action, "params": params, "echo": echo})
            response = await asyncio.wait_for(future, timeout=timeout)
            return response if isinstance(response, dict) else None
        except Exception:
            logger.exception("OneBot V11 action 调用失败: {}", action)
            return None
        finally:
            self._pending_actions.pop(echo, None)

    async def _handle_event(self, worker: NotificationWorker, session, ws, event: dict) -> None:
        texts = _onebot_v11_event_to_texts(event, worker.settings.get("User_QQ", ""))
        if not texts:
            return

        if is_notifications_muted():
            return

        result = worker.processor.process_notification(texts)
        if result and isinstance(result, dict):
            result.update(await self._onebot_event_assets(session, ws, event))
            worker.notification_ready.emit(result)

    async def _onebot_event_assets(self, session, ws, event: dict) -> dict:
        assets: dict[str, str] = {}

        for segment in _iter_onebot_v11_segments(event):
            segment_type = str(segment.get("type", "")).strip()
            data = segment.get("data")
            data = data if isinstance(data, dict) else {}

            if segment_type == "image" and "Pic_Path" not in assets:
                pic_path = await self._resolve_onebot_image(session, ws, data)
                if pic_path:
                    assets["Pic_Path"] = pic_path
            elif segment_type == "file" and "file" not in assets:
                file_path = await self._resolve_onebot_file(session, data)
                if file_path:
                    assets["file"] = file_path
                    assets["icon_file"] = _file_icon_for_path(file_path)

        return assets

    async def _resolve_onebot_image(self, session, ws, data: dict) -> str | None:
        url = data.get("url")
        filename = data.get("file") or data.get("filename") or data.get("name")
        if _is_http_url(url):
            return await _download_url(session, str(url), "onebot_image", str(filename or "image"))

        file_ref = data.get("file")
        local_path = _local_path_from_ref(file_ref)
        if local_path:
            return local_path
        if _is_http_url(file_ref):
            return await _download_url(session, str(file_ref), "onebot_image", str(filename or "image"))

        if file_ref:
            response = await self._call_action(ws, "get_image", {"file": file_ref})
            response_data = response.get("data") if isinstance(response, dict) else None
            response_data = response_data if isinstance(response_data, dict) else {}
            image_path = _local_path_from_ref(response_data.get("file"))
            if image_path:
                return image_path

        return None

    async def _resolve_onebot_file(self, session, data: dict) -> str | None:
        filename = (
            data.get("name")
            or data.get("file_name")
            or data.get("filename")
            or data.get("file")
            or "attachment"
        )

        for key in ("url", "path", "file", "file_url"):
            value = data.get(key)
            local_path = _local_path_from_ref(value)
            if local_path:
                return local_path
            if _is_http_url(value):
                return await _download_url(session, str(value), "onebot_file", str(filename))

        return None


class WinSDKNotificationEngine(NotificationEngine):
    key = ENGINE_WINSDK
    display_name = "WinSDK 通知中心"
    required_packages = ("winsdk",)

    async def run(self, worker: NotificationWorker) -> None:
        try:
            import winsdk.windows.ui.notifications as notifications
            import winsdk.windows.ui.notifications.management as mgmt
        except Exception as exc:
            raise EngineUnavailable("WinSDK 加载失败") from exc

        try:
            listener = mgmt.UserNotificationListener.current
            if not listener:
                raise EngineUnavailable("无法获取通知监听器")

            status = await listener.request_access_async()

            if status != mgmt.UserNotificationListenerAccessStatus.ALLOWED:
                raise EngineUnavailable("未获得通知访问权限")

            known_ids: set[int] = set()
            try:
                initial_notifs = await listener.get_notifications_async(
                    notifications.NotificationKinds.TOAST
                )
                if initial_notifs:
                    known_ids = {n.id for n in initial_notifs if n and hasattr(n, "id")}
            except Exception:
                logger.exception("获取初始通知失败")

            while worker._running:
                try:
                    notifs = await listener.get_notifications_async(
                        notifications.NotificationKinds.TOAST
                    )

                    if not notifs:
                        await asyncio.sleep(worker.settings.scan_interval)
                        continue

                    current_ids = {n.id for n in notifs if n and hasattr(n, "id")}

                    for n in notifs:
                        if not n or not hasattr(n, "id"):
                            continue

                        if n.id in known_ids:
                            continue

                        try:
                            if worker.settings.qq_only:
                                app_name = ""
                                try:
                                    if n.app_info and n.app_info.display_info:
                                        app_name = n.app_info.display_info.display_name
                                except Exception:
                                    pass
                                if app_name != "QQ":
                                    known_ids.add(n.id)
                                    continue

                            if not n.notification:
                                known_ids.add(n.id)
                                continue

                            visual = n.notification.visual
                            if not visual:
                                known_ids.add(n.id)
                                continue

                            texts = []
                            try:
                                bindings = visual.bindings
                                if bindings:
                                    for b in bindings:
                                        if b:
                                            text_elements = b.get_text_elements()
                                            if text_elements:
                                                texts.extend(
                                                    [
                                                        t.text.strip()
                                                        for t in text_elements
                                                        if t and t.text
                                                    ]
                                                )
                            except Exception:
                                pass

                            if texts:
                                if is_notifications_muted():
                                    known_ids.add(n.id)
                                    continue

                                result = worker.processor.process_notification(texts)
                                if result and isinstance(result, dict):
                                    worker.notification_ready.emit(result)

                        except Exception:
                            logger.exception("处理通知异常")

                        known_ids.add(n.id)

                    known_ids &= current_ids

                except Exception:
                    logger.exception("WinSDK异常")

                await asyncio.sleep(worker.settings.scan_interval)

        except EngineUnavailable:
            raise
        except Exception as exc:
            raise EngineUnavailable("WinSDK 初始化失败") from exc


def _official_qq_event_body(payload: dict) -> tuple[str, dict] | None:
    event_type = str(
        payload.get("t", "") or payload.get("event_type", "") or payload.get("type", "")
    ).strip()
    data = payload.get("d")
    if not isinstance(data, dict):
        data = payload.get("data")
    if event_type and isinstance(data, dict):
        return event_type, data

    if "group_openid" in payload and "content" in payload:
        return "GROUP_AT_MESSAGE_CREATE", payload

    return None


def _official_qq_attachment_label(attachment: dict) -> str:
    raw_type = str(
        attachment.get("type")
        or attachment.get("file_type")
        or attachment.get("content_type")
        or ""
    ).lower()
    filename = str(attachment.get("filename") or attachment.get("file_name") or "").lower()

    if "image" in raw_type or Path(filename).suffix in IMAGE_EXTENSIONS:
        return "[图片]"
    if "voice" in raw_type or "audio" in raw_type:
        return "[语音]"
    if "video" in raw_type:
        return "[视频]"
    return "[文件]"


async def _official_qq_assets(session, event: dict) -> dict:
    assets: dict[str, str] = {}
    attachments = event.get("attachments")
    if not isinstance(attachments, list):
        return assets

    for attachment in attachments:
        if not isinstance(attachment, dict):
            continue

        url = attachment.get("url") or attachment.get("voice_wav_url")
        filename = attachment.get("filename") or attachment.get("file_name") or "attachment"
        local_path = _local_path_from_ref(url)
        if not local_path and _is_http_url(url):
            local_path = await _download_url(session, str(url), "qq_official", str(filename))

        if not local_path:
            continue

        label = _official_qq_attachment_label(attachment)
        if label == "[图片]" and "Pic_Path" not in assets:
            assets["Pic_Path"] = local_path
        elif "file" not in assets:
            assets["file"] = local_path
            assets["icon_file"] = _file_icon_for_path(local_path)

    return assets


def _official_qq_event_to_texts(payload: dict) -> list[str] | None:
    event_info = _official_qq_event_body(payload)
    if not event_info:
        return None

    event_type, event = event_info
    if event_type != "GROUP_AT_MESSAGE_CREATE":
        return None

    content = str(event.get("content", "") or "").strip()
    attachment_labels: list[str] = []
    attachments = event.get("attachments")
    if isinstance(attachments, list):
        for attachment in attachments:
            if isinstance(attachment, dict):
                label = _official_qq_attachment_label(attachment)
                name = attachment.get("filename") or attachment.get("file_name") or ""
                attachment_labels.append(f"{label} {name}".strip())

    message_parts = [part for part in [content, *attachment_labels] if part]
    message = "\n".join(message_parts).strip()
    if not message:
        return None

    return ["新通知", message]


async def _onebot_v11_push_assets(session, event: dict) -> dict:
    assets: dict[str, str] = {}

    for segment in _iter_onebot_v11_segments(event):
        segment_type = str(segment.get("type", "")).strip()
        data = segment.get("data")
        data = data if isinstance(data, dict) else {}

        if segment_type == "image" and "Pic_Path" not in assets:
            pic_path = await _resolve_pushed_image(session, data, "onebot_image")
            if pic_path:
                assets["Pic_Path"] = pic_path
        elif segment_type == "file" and "file" not in assets:
            file_path = await _resolve_pushed_file(session, data, "onebot_file")
            if file_path:
                assets["file"] = file_path
                assets["icon_file"] = _file_icon_for_path(file_path)

    return assets


async def _resolve_pushed_image(session, data: dict, category: str) -> str | None:
    filename = data.get("file") or data.get("filename") or data.get("name") or "image"

    for key in ("url", "file", "path", "file_url"):
        value = data.get(key)
        local_path = _local_path_from_ref(value)
        if local_path:
            return local_path
        if _is_http_url(value):
            return await _download_url(session, str(value), category, str(filename))

    return None


async def _resolve_pushed_file(session, data: dict, category: str) -> str | None:
    filename = (
        data.get("name")
        or data.get("file_name")
        or data.get("filename")
        or data.get("file")
        or "attachment"
    )

    for key in ("url", "file_url", "path", "file"):
        value = data.get(key)
        local_path = _local_path_from_ref(value)
        if local_path:
            return local_path
        if _is_http_url(value):
            return await _download_url(session, str(value), category, str(filename))

    return None


async def _plain_http_push_assets(session, payload: dict) -> dict:
    assets: dict[str, str] = {}

    image_data = {
        "url": payload.get("image_url") or payload.get("Pic_URL") or payload.get("pic_url"),
        "file": payload.get("Pic_Path") or payload.get("pic_path") or payload.get("image_path"),
        "name": payload.get("image_name") or "image",
    }
    pic_path = await _resolve_pushed_image(session, image_data, "http_push_image")
    if pic_path:
        assets["Pic_Path"] = pic_path

    file_data = {
        "url": payload.get("file_url"),
        "file": payload.get("file") or payload.get("file_path"),
        "name": payload.get("file_name") or payload.get("filename") or "attachment",
    }
    file_path = await _resolve_pushed_file(session, file_data, "http_push_file")
    if file_path:
        assets["file"] = file_path
        assets["icon_file"] = _file_icon_for_path(file_path)

    return assets


class HTTPPushNotificationEngine(NotificationEngine):
    key = ENGINE_HTTP_PUSH
    display_name = "HTTP Push（调试）"
    required_packages = ("aiohttp",)
    windows_only = False

    async def run(self, worker: NotificationWorker) -> None:
        try:
            import aiohttp
            from aiohttp import web
        except Exception as exc:
            raise EngineUnavailable("aiohttp.web 加载失败") from exc

        host = worker.settings.http_push_host
        port = worker.settings.http_push_port
        path = worker.settings.http_push_path

        timeout = aiohttp.ClientTimeout(total=30)
        async with aiohttp.ClientSession(timeout=timeout) as session:
            app = web.Application()
            app.router.add_post(path, self._build_handler(worker, web, session))
            runner = web.AppRunner(app)

            try:
                await runner.setup()
                site = web.TCPSite(runner, host, port)
                await site.start()
                logger.info("HTTP Push 调试引擎已启动: http://{}:{}{}", host, port, path)

                while worker._running:
                    await asyncio.sleep(max(worker.settings.scan_interval, 1.0))
            except OSError as exc:
                raise EngineUnavailable(f"HTTP Push 监听失败: {exc}") from exc
            finally:
                await runner.cleanup()

    def _build_handler(self, worker: NotificationWorker, web, session):
        async def handle(request):
            token = worker.settings.http_push_token.strip()
            if token:
                auth = request.headers.get("Authorization", "")
                header_token = request.headers.get("X-QQListener-Token", "")
                query_token = request.query.get("token", "")
                expected = f"Bearer {token}"
                if auth != expected and header_token != token and query_token != token:
                    return web.json_response({"ok": False, "error": "unauthorized"}, status=401)

            try:
                payload = await request.json()
            except Exception:
                return web.json_response({"ok": False, "error": "invalid json"}, status=400)

            texts = self._payload_to_texts(payload, worker.settings.get("User_QQ", ""))
            if not texts:
                return web.json_response({"ok": False, "error": "empty message"}, status=400)

            if is_notifications_muted():
                return web.json_response({"ok": True, "pushed": False})

            result = worker.processor.process_notification(texts)
            pushed = bool(result and isinstance(result, dict))
            if pushed:
                result.update(await self._payload_assets(session, payload))
                worker.notification_ready.emit(result)

            return web.json_response({"ok": True, "pushed": pushed})

        return handle

    def _payload_to_texts(self, payload: object, user_qq: str = "") -> list[str] | None:
        if not isinstance(payload, dict):
            return None

        official_texts = _official_qq_event_to_texts(payload)
        if official_texts:
            return official_texts

        if payload.get("post_type") == "message":
            return _onebot_v11_event_to_texts(payload, user_qq)

        texts = payload.get("texts")
        if isinstance(texts, list):
            normalized = [str(item) for item in texts if item is not None and str(item).strip()]
            return normalized if normalized else None

        sender = (
            payload.get("Sender")
            or payload.get("sender")
            or payload.get("title")
            or payload.get("group")
            or "HTTP Push"
        )
        message = (
            payload.get("Message")
            or payload.get("message")
            or payload.get("text")
            or payload.get("content")
        )
        if message is None:
            return None
        if isinstance(message, list):
            message_text = "\n".join(str(item) for item in message if item is not None)
        elif isinstance(message, dict):
            message_text = json.dumps(message, ensure_ascii=False)
        else:
            message_text = str(message)

        return [str(sender), message_text] if message_text.strip() else None

    async def _payload_assets(self, session, payload: object) -> dict:
        if not isinstance(payload, dict):
            return {}

        official_event = _official_qq_event_body(payload)
        if official_event:
            event_type, event = official_event
            if event_type == "GROUP_AT_MESSAGE_CREATE":
                return await _official_qq_assets(session, event)

        if payload.get("post_type") == "message":
            return await _onebot_v11_push_assets(session, payload)

        return await _plain_http_push_assets(session, payload)


ENGINE_REGISTRY: dict[str, type[NotificationEngine]] = {
    ENGINE_WINSDK: WinSDKNotificationEngine,
    ENGINE_UIA: UIAutomationNotificationEngine,
    ENGINE_ONEBOT_V11: OneBotV11ForwardNotificationEngine,
    ENGINE_HTTP_PUSH: HTTPPushNotificationEngine,
    ENGINE_IDLE: IdleNotificationEngine,
}


def build_engine_candidates(preferred: str) -> list[type[NotificationEngine]]:
    key = normalize_notification_engine(preferred)

    if key == ENGINE_IDLE:
        return [IdleNotificationEngine]

    if key == ENGINE_AUTO:
        return [WinSDKNotificationEngine, UIAutomationNotificationEngine, IdleNotificationEngine]

    engine_cls = ENGINE_REGISTRY.get(key)
    if not engine_cls:
        return [IdleNotificationEngine]

    return [engine_cls, IdleNotificationEngine]
