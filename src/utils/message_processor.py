import hashlib
import time

from src.core.settings import get_settings
from src.native.model import CapturedMessage, Segment, message_text, quoted_message, segments_text
from src.utils.downloads import is_image_name, is_video_name
from src.utils.media import file_icon_for_path


def segment_payload(seg: Segment) -> dict:
    """把 Segment 压成通知层可用的纯 dict（UI 不依赖 native 模型）。"""
    kind = seg.type
    name = seg.name or ""
    if kind == "file" and name:
        # 群/私聊文件里混着图片和视频，按扩展名分流到对应渲染器。
        if is_image_name(name):
            kind = "image"
        elif is_video_name(name):
            kind = "video"
    payload = {
        "type": kind,
        "text": seg.text,
        "url": seg.url,
        "name": name,
        "md5": seg.md5,
        "target_id": seg.target_id,
        "size": int(seg.extra.get("size", 0) or 0),
        "local_path": str(seg.extra.get("local_path", "") or ""),
    }
    if kind in {"file", "video"}:
        payload["icon_file"] = file_icon_for_path(name)
    if kind == "image":
        payload["width"] = int(seg.extra.get("width", 0) or 0)
        payload["height"] = int(seg.extra.get("height", 0) or 0)
    return payload


def segments_payload(segments: list[Segment]) -> list[dict]:
    return [segment_payload(seg) for seg in segments if seg.type != "reply"]


class MessageProcessor:
    def __init__(self) -> None:
        self.settings = get_settings()
        self.seen: dict[str, float] = {}

    @staticmethod
    def _id_set(values: object) -> set[str]:
        if not isinstance(values, list):
            return set()
        return {str(value).strip() for value in values if str(value).strip()}

    def _is_blacklisted(self, msg: CapturedMessage) -> bool:
        if not self.settings.blacklist_enabled:
            return False
        person_ids = self._id_set(self.settings.blacklist_person_qqs)
        if msg.sender_id in person_ids:
            return True
        if msg.scene == "group":
            return msg.peer_id in self._id_set(self.settings.blacklist_groups)
        return msg.peer_id in person_ids

    def _is_whitelisted(self, msg: CapturedMessage) -> bool:
        if not self.settings.whitelist_enabled:
            return True
        person_ids = self._id_set(self.settings.whitelist_person_qqs)
        if msg.sender_id in person_ids:
            return True
        if msg.scene == "group":
            return msg.peer_id in self._id_set(self.settings.whitelist_groups)
        return msg.peer_id in person_ids

    @staticmethod
    def display_name(msg: CapturedMessage) -> str:
        """备注 > 群名片 > 昵称。"""
        if msg.scene == "group":
            candidates = (
                msg.sender_remark,
                msg.sender_group_card,
                msg.sender_nickname,
                msg.sender_name,
            )
        else:
            candidates = (msg.sender_remark, msg.sender_nickname, msg.sender_name)
        for candidate in candidates:
            if candidate:
                return candidate
        return msg.sender_id

    def _sender_label(self, msg: CapturedMessage) -> str:
        """默认只显示名字与群名，不带号码。"""
        name = self.display_name(msg)
        if msg.scene != "group":
            return name
        peer = msg.peer_name or msg.peer_id
        return f"{name}（{peer}）" if peer else name

    @staticmethod
    def _sender_detail(msg: CapturedMessage) -> str:
        """点击发送者行才展开的号码信息。"""
        parts: list[str] = []
        if msg.scene == "group" and msg.peer_id:
            parts.append(f"群号: {msg.peer_id}")
        if msg.sender_id:
            parts.append(f"发送者QQ号: {msg.sender_id}")
        elif msg.scene != "group" and msg.peer_id:
            parts.append(f"QQ号: {msg.peer_id}")
        return "　".join(parts)

    def _captured_mentions_me(self, msg: CapturedMessage) -> bool:
        user_qq = str(self.settings.get("User_QQ", "") or "").strip()
        for seg in msg.segments:
            if seg.type != "at":
                continue
            if seg.target_id == "all" or (user_qq and seg.target_id == user_qq):
                return True
        return False

    @staticmethod
    def _quote_payload(msg: CapturedMessage) -> dict | None:
        quoted = quoted_message(msg)
        if quoted is None:
            return None
        body = segments_text(quoted.segments)
        if not body and not quoted.sender_id:
            return None
        return {
            "sender": quoted.sender_name or quoted.sender_id or "对方",
            "detail": f"QQ号: {quoted.sender_id}" if quoted.sender_id else "",
            "text": body,
            "segments": segments_payload(quoted.segments),
        }

    def process_captured(
        self,
        msg: CapturedMessage,
        image_path: str | None = None,
        file_path: str | None = None,
        reply_route: dict | None = None,
    ) -> dict | None:
        body = message_text(msg)
        quote = self._quote_payload(msg)
        segments = segments_payload(msg.segments)
        has_media = any(seg["type"] != "text" for seg in segments)
        if not body and not has_media and not image_path and not file_path and quote is None:
            return None

        sender_label = self._sender_label(msg)
        key_data = f"{msg.scene}|{msg.peer_id}|{msg.sender_id}|{body}|{msg.raw_seq}"
        key = hashlib.md5(key_data.encode()).hexdigest()
        now = time.time()
        dedupe_window = max(float(self.settings.cooldown), 1.0)
        if key in self.seen and now - self.seen[key] < dedupe_window:
            return None

        if self._is_blacklisted(msg) or not self._is_whitelisted(msg):
            return None

        self.seen[key] = now
        combined = f"{sender_label}\n{body}"

        important = False
        calling = False
        duration = self.settings.duration_everyone
        calling_keyword = self.settings.calling_keyword
        if self.settings.calling_enabled and calling_keyword and calling_keyword in combined:
            duration = self.settings.calling_duration
            important = True
            calling = True
        else:
            important_persons = self._id_set(self.settings.important_person_qqs)
            important_keywords = self.settings.important_keywords
            is_important_person = msg.sender_id in important_persons
            is_important_keyword = (
                important_keywords
                and isinstance(important_keywords, list)
                and any(k in body for k in important_keywords if k)
            )
            is_at_me = self.settings.someone_at_me and self._captured_mentions_me(msg)
            if is_important_person or is_important_keyword or is_at_me:
                duration = self.settings.duration_important
                important = True

        detail = self._sender_detail(msg)
        notify_data = {
            # 单条消息的老字段保留：测试通知、旧调用方还在用。
            "Sender": sender_label,
            "Sender_Detail": detail,
            "Message": body,
            "Segments": segments,
            "Quote": quote,
            # 通知窗口真正渲染的是这个列表；积压摘要会把多条拼进来。
            "Messages": [
                {
                    "sender": sender_label,
                    "detail": detail,
                    "text": body,
                    "segments": segments,
                    "quote": quote,
                }
            ],
            "Reply": reply_route or {},
            "Duration": duration,
            "Priority": 0 if important else 1,
            "Calling": calling,
        }
        if image_path:
            notify_data["Pic_Path"] = image_path
        if file_path:
            notify_data["file_target"] = file_path
        return notify_data


# 摘要窗口最多念几条，剩下的只报数——不然暂停一小时能念到天亮。
_DIGEST_TTS_LIMIT = 3


def digest_entries(payload: dict) -> list[dict]:
    """从通知载荷里取出消息列表；老载荷（只有 Sender/Segments）也能兼容。"""
    messages = payload.get("Messages")
    if isinstance(messages, list) and messages:
        return list(messages)
    return [
        {
            "sender": payload.get("Sender", ""),
            "detail": payload.get("Sender_Detail", ""),
            "text": payload.get("Message", ""),
            "segments": list(payload.get("Segments") or []),
            "quote": payload.get("Quote"),
        }
    ]


def _digest_tts_text(entries: list[dict], dropped: int) -> str:
    head = f"暂停期间收到{len(entries)}条消息。"
    spoken = [
        f"{entry.get('sender', '')}：{entry.get('text', '')}".strip("：")
        for entry in entries[:_DIGEST_TTS_LIMIT]
        if entry.get("text") or entry.get("sender")
    ]
    tail = (
        f"另有{len(entries) - _DIGEST_TTS_LIMIT}条未念。"
        if len(entries) > _DIGEST_TTS_LIMIT
        else ""
    )
    overflow = f"更早的{dropped}条已超出上限。" if dropped else ""
    return head + "".join(f"{line}。" for line in spoken) + tail + overflow


def _priority_of(payload: dict) -> int:
    value = payload.get("Priority")
    return int(value) if isinstance(value, (int, float)) else 2


def build_digest_payload(payloads: list[dict], dropped: int = 0) -> dict | None:
    """把积压的多条通知合成一个摘要窗口的载荷。"""
    if not payloads:
        return None

    entries: list[dict] = []
    for payload in payloads:
        entries.extend(digest_entries(payload))
    if not entries:
        return None

    if len(entries) == 1 and not dropped:
        return payloads[0]

    title = f"暂停期间的 {len(entries)} 条消息"
    if dropped:
        title += f"（更早的 {dropped} 条已丢弃）"

    return {
        "Sender": title,
        "Sender_Detail": "",
        "Message": "\n".join(
            f"{entry.get('sender', '')}: {entry.get('text', '')}" for entry in entries
        ),
        "TTS_Text": _digest_tts_text(entries, dropped),
        "Segments": [],
        "Quote": None,
        "Messages": entries,
        # 回复目标取最后一条：摘要里的消息可能跨会话，窗口会把目标明写出来。
        "Reply": payloads[-1].get("Reply") or {},
        "Duration": max(int(p.get("Duration", 0) or 0) for p in payloads),
        # 注意别写成 `p.get("Priority", 2) or 2`：重要消息的 Priority 是 0，会被吞掉。
        "Priority": min(_priority_of(p) for p in payloads),
        "Calling": any(bool(p.get("Calling")) for p in payloads),
        "Digest": True,
    }
