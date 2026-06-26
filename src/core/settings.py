import json
import os
import time
from pathlib import Path
from typing import Any

from loguru import logger

from src.core.notification_engines import normalize_notification_engine


class Settings:
    _instance: "Settings | None" = None
    _initialized: bool = False

    def __new__(cls, *args: Any, **kwargs: Any) -> "Settings":
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance

    def __init__(self, settings_file: str = "setting.json") -> None:
        if Settings._initialized:
            return

        self._settings_file: str = settings_file
        self._data: dict[str, Any] = {}
        self._load()
        Settings._initialized = True

    def _load(self) -> None:
        if not self._settings_file:
            self._data = {}
            return

        if os.path.exists(self._settings_file):
            try:
                with open(self._settings_file, encoding="utf-8") as f:
                    loaded_data = json.load(f)
                    self._data = loaded_data if isinstance(loaded_data, dict) else {}
            except (json.JSONDecodeError, OSError):
                logger.exception("加载设置失败")
                self._data = {}
        else:
            self._data = {}

    def save(self) -> bool:
        if not self._settings_file:
            return False

        try:
            with open(self._settings_file, "w", encoding="utf-8") as f:
                json.dump(self._data, f, indent=4, ensure_ascii=False)
            return True
        except OSError:
            logger.exception("保存设置失败")
            return False

    def reload(self) -> bool:
        """从磁盘重新加载设置，返回配置是否发生变化。"""
        if not self._settings_file:
            return False

        old_data = self.get_all()
        try:
            with open(self._settings_file, encoding="utf-8") as f:
                loaded_data = json.load(f)
        except (json.JSONDecodeError, OSError):
            logger.exception("重新加载设置失败")
            return False

        self._data = loaded_data if isinstance(loaded_data, dict) else {}
        return self.get_all() != old_data

    def get(self, key: str, default: Any = None) -> Any:
        if not key or not isinstance(key, str):
            return default
        return self._data.get(key, default)

    def set(self, key: str, value: Any) -> None:
        """设置值"""
        if not key or not isinstance(key, str):
            return
        self._data[key] = value

    def update(self, data: dict[str, Any] | None) -> None:
        """批量更新设置"""
        if data and isinstance(data, dict):
            self._data.update(data)

    def get_all(self) -> dict[str, Any]:
        """获取所有设置"""
        return self._data.copy() if self._data else {}

    @property
    def settings_file(self) -> str:
        return self._settings_file

    def is_first_run(self) -> bool:
        """检查是否是首次运行（未保存过设置）"""
        return bool(self._data.get("Green_Hand", True))

    def mark_configured(self) -> None:
        """标记已配置"""
        self._data["Green_Hand"] = False

    @staticmethod
    def _expand_path(value: Any) -> Path | None:
        if value is None:
            return None

        text = str(value).strip()
        if not text:
            return None

        return Path(os.path.expandvars(os.path.expanduser(text)))

    @staticmethod
    def _is_dir(path: Path | None) -> bool:
        if path is None:
            return False
        try:
            return path.is_dir()
        except OSError:
            return False

    @classmethod
    def _is_usable_qq_user_dir(cls, path: Path | None) -> bool:
        if not cls._is_dir(path):
            return False
        return cls._is_dir(path / "nt_qq") if path else False

    @staticmethod
    def _mtime(path: Path) -> float:
        try:
            return path.stat().st_mtime
        except OSError:
            return 0.0

    @classmethod
    def _latest_numeric_dir(cls, base_path: Path | None) -> Path | None:
        if not cls._is_dir(base_path):
            return None

        try:
            numeric_dirs = [
                child
                for child in base_path.iterdir()
                if child.name.isdigit() and cls._is_dir(child)
            ]
        except OSError:
            return None

        if not numeric_dirs:
            return None

        usable_dirs = [path for path in numeric_dirs if cls._is_usable_qq_user_dir(path)]
        return max(usable_dirs or numeric_dirs, key=cls._mtime)

    @classmethod
    def _candidate_tencent_files_roots(cls, configured_path: Path | None) -> list[Path]:
        roots: list[Path] = []
        seen: set[str] = set()

        def add(path: Path | None) -> None:
            if path is None:
                return
            key = str(path)
            if key not in seen:
                roots.append(path)
                seen.add(key)

        if configured_path:
            if configured_path.name.isdigit():
                add(configured_path.parent)
            elif configured_path.name.lower() in {"tencent files", "腾讯文件"}:
                add(configured_path)
            else:
                add(configured_path / "Tencent Files")

        for env_name in ("USERPROFILE", "USERPATH", "HOME"):
            home = cls._expand_path(os.environ.get(env_name))
            if home:
                add(home / "Documents" / "Tencent Files")

        add(Path.home() / "Documents" / "Tencent Files")
        return roots

    def _resolve_qq_user_dir(self) -> Path | None:
        configured_path = self._expand_path(self.get("Tencent_Files_Path", ""))
        user_qq = str(self.get("User_QQ", "") or "").strip()
        configured_existing_dir: Path | None = None

        configured_candidates: list[Path] = []
        if configured_path:
            if user_qq:
                configured_candidates.append(configured_path / user_qq)
            configured_candidates.append(configured_path)

        for candidate in configured_candidates:
            if self._is_usable_qq_user_dir(candidate):
                return candidate

        for candidate in configured_candidates:
            if candidate.name.isdigit() and self._is_dir(candidate):
                configured_existing_dir = candidate
                break

        for base_path in self._candidate_tencent_files_roots(configured_path):
            latest_dir = self._latest_numeric_dir(base_path)
            if latest_dir:
                return latest_dir

        return configured_existing_dir

    @property
    def qq_user_dir(self) -> str | None:
        resolved = self._resolve_qq_user_dir()
        return str(resolved) if resolved else None

    @property
    def thumb_path(self) -> str | None:
        qq_user_dir = self._resolve_qq_user_dir()
        if qq_user_dir:
            return str(
                qq_user_dir / "nt_qq" / "nt_data" / "Pic" / time.strftime("%Y-%m") / "Thumb"
            )
        return None

    @property
    def important_persons(self) -> list[str]:
        result = self.get("Important_Persons", [])
        return result if isinstance(result, list) else []

    @property
    def important_keywords(self) -> list[str]:
        result = self.get("Important_Keywords", [])
        return result if isinstance(result, list) else []

    @property
    def blacklist(self) -> list[str]:
        result = self.get("BlackList", [])
        return result if isinstance(result, list) else []

    @property
    def whitelist(self) -> list[str]:
        result = self.get("WhiteList", [])
        return result if isinstance(result, list) else []

    @property
    def scan_interval(self) -> float:
        result = self.get("ScanInterval", 0.3)
        return float(result) if isinstance(result, (int, float)) else 0.3

    @property
    def cooldown(self) -> int:
        result = self.get("Cooldown", 3)
        return int(result) if isinstance(result, (int, float)) else 3

    @property
    def auto_start(self) -> bool:
        return bool(self.get("Auto_Start", False))

    @property
    def uia_mode(self) -> bool:
        return bool(self.get("UIAMode", False))

    @property
    def notification_engine(self) -> str:
        return normalize_notification_engine(
            self.get("NotificationEngine", None),
            legacy_uia=self.uia_mode,
        )

    @property
    def onebot_v11_ws_url(self) -> str:
        result = self.get("OneBotV11_WS_URL", "ws://127.0.0.1:8080/event")
        return str(result).strip() if result else ""

    @property
    def onebot_v11_token(self) -> str:
        result = self.get("OneBotV11_Access_Token", "")
        return str(result) if result else ""

    @property
    def http_push_enabled(self) -> bool:
        return bool(self.get("HTTPPush_Enabled", False))

    @property
    def http_push_host(self) -> str:
        result = self.get("HTTPPush_Host", "127.0.0.1")
        return str(result).strip() if result else "127.0.0.1"

    @property
    def http_push_port(self) -> int:
        result = self.get("HTTPPush_Port", 8765)
        return int(result) if isinstance(result, (int, float)) else 8765

    @property
    def http_push_path(self) -> str:
        result = self.get("HTTPPush_Path", "/push")
        path = str(result).strip() if result else "/push"
        return path if path.startswith("/") else f"/{path}"

    @property
    def http_push_token(self) -> str:
        result = self.get("HTTPPush_Token", "")
        return str(result) if result else ""

    @property
    def qq_only(self) -> bool:
        return bool(self.get("QQ_Only", False))

    @property
    def auto_show_thumb(self) -> bool:
        return bool(self.get("Auto_Show_Thumb", False))

    @property
    def someone_at_me(self) -> bool:
        return bool(self.get("Someone_At_Me", True))

    @property
    def calling_enabled(self) -> bool:
        return bool(self.get("Calling", True))

    @property
    def calling_keyword(self) -> str:
        result = self.get("Calling_Keyword", "呼叫")
        return str(result) if result else "呼叫"

    @property
    def calling_duration(self) -> int:
        result = self.get("Calling_Duration", 600000)
        return int(result) if isinstance(result, (int, float)) else 600000

    @property
    def calling_animation(self) -> bool:
        return bool(self.get("Calling_Animation", True))

    @property
    def calling_bpm(self) -> int:
        result = self.get("Calling_BPM", 30)
        return int(result) if isinstance(result, (int, float)) else 30

    @property
    def tts_enabled(self) -> bool:
        return bool(self.get("TTS", True))

    @property
    def edge_tts_enabled(self) -> bool:
        return bool(self.get("Edge_TTS", True))

    @property
    def edge_voice(self) -> str:
        result = self.get("Edge_Voice", "zh-CN-XiaoyiNeural")
        return str(result) if result else "zh-CN-XiaoyiNeural"

    @property
    def edge_rate(self) -> str:
        result = self.get("Edge_Rate", "+0%")
        return str(result) if result else "+0%"

    @property
    def edge_pitch(self) -> str:
        result = self.get("Edge_Pitch", "+10Hz")
        return str(result) if result else "+10Hz"

    @property
    def edge_volume(self) -> str:
        result = self.get("Edge_Volume", "+0%")
        return str(result) if result else "+0%"

    @property
    def duration_everyone(self) -> int:
        result = self.get("Duration_Everyone", 5000)
        return int(result) if isinstance(result, (int, float)) else 5000

    @property
    def duration_important(self) -> int:
        result = self.get("Duration_Important", 10000)
        return int(result) if isinstance(result, (int, float)) else 10000

    @property
    def max_wait_thumb_time(self) -> int:
        result = self.get("Max_Wait_Thumb_Time", 5)
        return int(result) if isinstance(result, (int, float)) else 5

    @property
    def always_on_top(self) -> bool:
        return bool(self.get("Always_On_Top", False))

    @property
    def notify_shadow(self) -> bool:
        return bool(self.get("Notify_Shadow", True))

    @property
    def notify_animation(self) -> bool:
        return bool(self.get("Notify_Animation", True))

    @property
    def notify_mask(self) -> bool:
        return bool(self.get("Notify_Mask", True))

    @property
    def show_status_ball(self) -> bool:
        return bool(self.get("Show_Status_Ball", True))

    @property
    def notify_label(self) -> str:
        result = self.get("Notify_Label", "xxtsoft QQListener")
        return str(result) if result else "xxtsoft QQListener"

    @property
    def sound_normal(self) -> str:
        result = self.get("Sound_Effect_Normal", "asset/notify_sound.mp3")
        return str(result) if result else "asset/notify_sound.mp3"

    @property
    def sound_important(self) -> str:
        result = self.get("Sound_Effect_Important", "asset/important_sound.mp3")
        return str(result) if result else "asset/important_sound.mp3"

    @property
    def language(self) -> str:
        result = self.get("Language", "zh-CN")
        return str(result) if result else "zh-CN"

    @property
    def icon_ok(self) -> str:
        result = self.get("icon_ok", "asset/icon_ok.png")
        return str(result) if result else "asset/icon_ok.png"

    @property
    def icon_cancel(self) -> str:
        result = self.get("icon_cancel", "asset/icon_cancel.png")
        return str(result) if result else "asset/icon_cancel.png"

    @property
    def notify_title_font(self) -> str:
        result = self.get("Notify_Title_Font", "asset/Font/JingNanBoBoHei-Bold-2.ttf")
        return str(result) if result else "asset/Font/JingNanBoBoHei-Bold-2.ttf"

    @property
    def notify_message_font(self) -> str:
        result = self.get("Notify_Message_Font", "asset/Font/FZLanTYK.ttf")
        return str(result) if result else "asset/Font/FZLanTYK.ttf"


def get_settings() -> Settings:
    """获取设置单例实例"""
    return Settings()
