import hashlib
import time

from src.core.settings import get_settings
from src.native.model import CapturedMessage, message_text
from src.utils.media import file_icon_for_path


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

    def process_captured(
        self,
        msg: CapturedMessage,
        image_path: str | None = None,
        file_path: str | None = None,
    ) -> dict | None:
        body = message_text(msg)
        if not body and not image_path and not file_path:
            return None

        sender_label = self._captured_sender_label(msg)
        key = hashlib.md5(f"{sender_label}|{body}|{msg.raw_seq}".encode()).hexdigest()
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
        if file_path:
            notify_data["file_target"] = file_path
        file_seg = next((s for s in msg.segments if s.type == "file"), None)
        if file_seg is not None:
            notify_data["file_name"] = file_seg.name or "QQ 文件"
            if not notify_data.get("file_target") and file_seg.url:
                notify_data["file_target"] = file_seg.url
            icon_ref = file_seg.name or file_path or ""
            notify_data["icon_file"] = file_icon_for_path(icon_ref)
        return notify_data
