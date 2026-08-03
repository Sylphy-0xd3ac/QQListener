import hashlib
import time

from src.core.settings import get_settings
from src.native.model import CapturedMessage, message_text


class MessageProcessor:
    def __init__(self) -> None:
        self.settings = get_settings()
        self.seen: dict[str, float] = {}
        self.active_toasts: set[str] = set()

    def _captured_sender_label(self, msg: CapturedMessage) -> str:
        sender = msg.sender_name or msg.sender_id
        if msg.scene == "group":
            peer = msg.peer_name or msg.peer_id
            return f"{peer}·{sender}" if peer else sender
        return sender

    def _captured_mentions_me(self, msg: CapturedMessage) -> bool:
        user_qq = str(self.settings.get("User_QQ", "") or "").strip()
        for seg in msg.segments:
            if seg.type != "at":
                continue
            if seg.target_id == "all" or (user_qq and seg.target_id == user_qq):
                return True
        return False

    def process_captured(self, msg: CapturedMessage, image_path: str | None = None) -> dict | None:
        body = message_text(msg)
        if not body and not image_path:
            return None

        sender_label = self._captured_sender_label(msg)
        key = hashlib.md5(f"{sender_label}|{body}|{msg.raw_seq}".encode()).hexdigest()
        now = time.time()
        if key in self.active_toasts:
            return None
        if key in self.seen and now - self.seen[key] < self.settings.cooldown:
            return None

        self.seen[key] = now
        self.active_toasts.add(key)

        combined = f"{sender_label}\n{body}"
        blacklist = self.settings.blacklist
        if blacklist and isinstance(blacklist, list) and any(k in combined for k in blacklist if k):
            return None

        whitelist = self.settings.whitelist
        if (
            whitelist
            and isinstance(whitelist, list)
            and not any(k in combined for k in whitelist if k)
        ):
            return None

        important = False
        calling = False
        duration = self.settings.duration_everyone
        calling_keyword = self.settings.calling_keyword
        if self.settings.calling_enabled and calling_keyword and calling_keyword in combined:
            duration = self.settings.calling_duration
            important = True
            calling = True
        else:
            important_persons = self.settings.important_persons
            important_keywords = self.settings.important_keywords
            is_important_person = (
                important_persons
                and isinstance(important_persons, list)
                and any(p in sender_label for p in important_persons if p)
            )
            is_important_keyword = (
                important_keywords
                and isinstance(important_keywords, list)
                and any(k in body for k in important_keywords if k)
            )
            is_at_me = self.settings.someone_at_me and self._captured_mentions_me(msg)
            if is_important_person or is_important_keyword or is_at_me:
                duration = self.settings.duration_important
                important = True

        notify_data = {
            "Sender": sender_label,
            "Message": body,
            "Duration": duration,
            "Priority": 0 if important else 1,
            "Calling": calling,
            "icon_file": "asset/pdf.png",
        }
        if image_path:
            notify_data["Pic_Path"] = image_path
        return notify_data

