import json
import os
from pathlib import Path
from typing import Any

from loguru import logger

LEGACY_SETTING_KEYS = frozenset(
    {
        "App",
        "Version",
        "Tencent_Files_Path",
        "NotificationEngine",
        "UIAMode",
        "Important_Persons",
        "BlackList",
        "WhiteList",
        "QQ_Only",
        "OneBotV11_WS_URL",
        "OneBotV11_Access_Token",
        "HTTPPush_Enabled",
        "HTTPPush_Host",
        "HTTPPush_Port",
        "HTTPPush_Path",
        "HTTPPush_Token",
        "ScanInterval",
        "Max_Wait_Thumb_Time",
    }
)


class Settings:
    _instance: "Settings | None" = None
    _initialized: bool = False

    def __new__(cls, *args: Any, **kwargs: Any) -> "Settings":
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance

    def __init__(self, settings_file: str | None = None) -> None:
        if Settings._initialized:
            return

        self._settings_file: str = self._resolve_settings_file(settings_file)
        self._data: dict[str, Any] = {}
        self._load()
        Settings._initialized = True

    @staticmethod
    def _resolve_settings_file(settings_file: str | None) -> str:
        # 相对路径会随工作目录漂移：开机自启时 CWD 常是 system32，导致读不到/
        # 写不进 setting.json，表现为每次都当首次运行。锚定到应用根目录。
        if settings_file is None:
            from src.core.resources import app_root

            return str(app_root() / "setting.json")
        candidate = Path(settings_file)
        if candidate.is_absolute():
            return str(candidate)
        from src.core.resources import app_root

        return str(app_root() / candidate)

    def _load(self) -> None:
        if not self._settings_file:
            self._data = {}
            return

        if os.path.exists(self._settings_file):
            try:
                with open(self._settings_file, encoding="utf-8") as f:
                    loaded_data = json.load(f)
                    self._data = loaded_data if isinstance(loaded_data, dict) else {}
                    self._drop_legacy_keys()
            except (json.JSONDecodeError, OSError):
                logger.exception("加载设置失败")
                self._data = {}
        else:
            self._data = {}

    def save(self) -> bool:
        if not self._settings_file:
            return False

        try:
            self._drop_legacy_keys()
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
        self._drop_legacy_keys()
        return self.get_all() != old_data

    def _drop_legacy_keys(self) -> None:
        for key in LEGACY_SETTING_KEYS:
            self._data.pop(key, None)

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

    @property
    def important_person_qqs(self) -> list[str]:
        result = self.get("Important_Person_QQs", [])
        return result if isinstance(result, list) else []

    @property
    def important_keywords(self) -> list[str]:
        result = self.get("Important_Keywords", [])
        return result if isinstance(result, list) else []

    @property
    def whitelist_enabled(self) -> bool:
        return bool(self.get("Whitelist_Enabled", False))

    @property
    def blacklist_enabled(self) -> bool:
        return bool(self.get("Blacklist_Enabled", False))

    @property
    def whitelist_groups(self) -> list[str]:
        result = self.get("Whitelist_Groups", [])
        return result if isinstance(result, list) else []

    @property
    def blacklist_groups(self) -> list[str]:
        result = self.get("Blacklist_Groups", [])
        return result if isinstance(result, list) else []

    @property
    def whitelist_person_qqs(self) -> list[str]:
        result = self.get("Whitelist_Person_QQs", [])
        return result if isinstance(result, list) else []

    @property
    def blacklist_person_qqs(self) -> list[str]:
        result = self.get("Blacklist_Person_QQs", [])
        return result if isinstance(result, list) else []

    @property
    def cooldown(self) -> int:
        result = self.get("Cooldown", 3)
        return int(result) if isinstance(result, (int, float)) else 3

    @property
    def auto_start(self) -> bool:
        return bool(self.get("Auto_Start", False))

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
    def download_dir(self) -> str:
        """附件下载目录；留空表示用系统默认下载目录。"""
        result = self.get("Download_Dir", "")
        return str(result) if result else ""

    @property
    def lite_mode(self) -> bool:
        """性能模式：关掉遮罩/阴影/动画，低配机器上通知不再卡。"""
        return bool(self.get("Lite_Mode", False))

    @property
    def reply_enabled(self) -> bool:
        return bool(self.get("Reply_Enabled", True))

    @property
    def reply_default_text(self) -> str:
        result = self.get("Reply_Default_Text", "收到")
        return str(result) if result else "收到"

    @property
    def reply_quote_in_group(self) -> bool:
        """群聊里以引用回复的形式发送。"""
        return bool(self.get("Reply_Quote_In_Group", True))

    @property
    def ok_button_text(self) -> str:
        result = self.get("OK_btn", "确认")
        return str(result) if result else "确认"

    @property
    def cancel_button_text(self) -> str:
        result = self.get("Cancel_btn", "取消")
        return str(result) if result else "取消"

    @property
    def log_level(self) -> str:
        result = self.get("Log_Level", "INFO")
        return str(result) if result else "INFO"

    @property
    def playback_volume(self) -> int:
        """播报与提示音的播放音量（0-100）。

        不是系统音量——这是我们自己这一路音频的音量。默认 50，别一上来就顶格。
        """
        result = self.get("Playback_Volume", 50)
        if not isinstance(result, (int, float)):
            return 50
        return max(0, min(100, int(result)))

    @property
    def force_system_volume(self) -> bool:
        """是否顺带把系统主音量拉满。

        默认关：这是"教室里必须听见"的应急开关，不该是常态——它会盖掉用户
        自己调好的音量。
        """
        return bool(self.get("Force_System_Volume", False))

    @property
    def pause_queue_enabled(self) -> bool:
        """暂停期间把消息积压起来，恢复监听时合并弹出。"""
        return bool(self.get("Pause_Queue_Enabled", True))

    @property
    def pause_queue_max(self) -> int:
        result = self.get("Pause_Queue_Max", 50)
        return max(1, int(result)) if isinstance(result, (int, float)) else 50

    @property
    def show_ids_by_default(self) -> bool:
        """默认就把群号/QQ 号摊开显示（默认关闭，点一下才显示）。"""
        return bool(self.get("Show_IDs", False))

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
