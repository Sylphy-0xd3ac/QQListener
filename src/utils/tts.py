import asyncio
import os
import shutil
import subprocess
import sys
import tempfile

import edge_tts
import pygame
from loguru import logger
from PySide2.QtCore import QObject, QThread, QTimer, Signal

from src.core.settings import get_settings


def _set_windows_volume_percent(percent: float) -> None:
    if not 0 <= percent <= 100:
        raise ValueError("音量百分比必须在 0 到 100 之间")

    from ctypes import POINTER, cast

    from comtypes import CLSCTX_ALL
    from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume

    devices = AudioUtilities.GetSpeakers()
    interface = devices.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
    volume = cast(interface, POINTER(IAudioEndpointVolume))
    volume.SetMute(0, None)
    volume.SetMasterVolumeLevelScalar(percent / 100.0, None)


def set_system_volume_max() -> None:
    """Best-effort system output volume boost before TTS playback."""
    try:
        if sys.platform == "darwin":
            subprocess.run(
                [
                    "osascript",
                    "-e",
                    "set volume output volume 100",
                    "-e",
                    "set volume without output muted",
                ],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
            )
        elif sys.platform == "win32":
            _set_windows_volume_percent(100)
        elif shutil.which("pactl"):
            subprocess.run(
                ["pactl", "set-sink-mute", "@DEFAULT_SINK@", "false"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=2,
            )
            subprocess.run(
                ["pactl", "set-sink-volume", "@DEFAULT_SINK@", "100%"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=2,
            )
        elif shutil.which("amixer"):
            subprocess.run(
                ["amixer", "-q", "sset", "Master", "100%", "unmute"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=2,
            )
    except subprocess.TimeoutExpired:
        logger.debug("设置系统音量超时")
    except Exception as exc:
        logger.debug("设置系统音量失败: {}", exc)


class TTSThread(QThread):
    finished_signal = Signal(str)

    def __init__(self, text: str, parent=None):
        super().__init__(parent)
        self.text = text if text and isinstance(text, str) else ""
        self.settings = get_settings()

    def run(self):
        """执行TTS"""
        if not self.text:
            self.finished_signal.emit("")
            return

        try:
            if self.settings.edge_tts_enabled:
                self._run_edge_tts()
            else:
                self._run_system_tts()
        except Exception:
            logger.exception("TTS错误")
            self.finished_signal.emit("")

    def _run_edge_tts(self):
        """使用Edge TTS (模块调用)"""
        fd, output_file = tempfile.mkstemp(prefix="qqlistener-tts-", suffix=".mp3")
        os.close(fd)

        voice = self.settings.edge_voice or "zh-CN-XiaoyiNeural"
        rate = self.settings.edge_rate
        pitch = self.settings.edge_pitch
        volume = self.settings.edge_volume

        safe_text = self.text.replace('"', "'") if self.text else ""
        if not safe_text:
            self._remove_file(output_file)
            self.finished_signal.emit("")
            return

        async def run_tts():
            try:
                communicate = edge_tts.Communicate(
                    text=safe_text,
                    voice=voice,
                    rate=rate,
                    pitch=pitch,
                    volume=volume,
                )
                await communicate.save(output_file)
                self.finished_signal.emit(output_file)
            except Exception:
                logger.exception("Edge TTS执行失败")
                self._remove_file(output_file)
                self.finished_signal.emit("")

        asyncio.run(run_tts())

    @staticmethod
    def _remove_file(file_path: str | None) -> None:
        if not file_path:
            return
        try:
            if os.path.exists(file_path):
                os.remove(file_path)
        except OSError:
            logger.exception("清理TTS临时文件失败: {}", file_path)

    def _run_system_tts(self):
        import pyttsx3

        try:
            engine = pyttsx3.init()
            engine.setProperty(
                "voice",
                r"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech\Voices\Tokens\TTS_MS_ZH-CN_HUIHUI_11.0",
            )
            engine.setProperty("volume", 1)
            engine.say(self.text)
            engine.runAndWait()
            self.finished_signal.emit("")
        except Exception:
            logger.exception("系统TTS失败")
            self.finished_signal.emit("")


class TTSManager(QObject):
    started = Signal()
    finished = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.settings = get_settings()
        self._current_thread: TTSThread | None = None
        self._current_sound = None
        self._current_channel = None
        self._playback_file: str | None = None
        self._active = False

        self._playback_timer = QTimer(self)
        self._playback_timer.setInterval(200)
        self._playback_timer.timeout.connect(self._check_playback_finished)

    @property
    def is_active(self) -> bool:
        return self._active

    def speak(self, text: str) -> bool:
        """播放语音"""
        if not self.settings.tts_enabled or not text or not isinstance(text, str):
            return False

        self.stop(emit_finished=False)
        self._active = True
        self.started.emit()
        set_system_volume_max()
        self._current_thread = TTSThread(text)
        self._current_thread.finished_signal.connect(self._on_tts_ready)
        self._current_thread.finished.connect(self._on_thread_finished)
        self._current_thread.finished.connect(self._current_thread.deleteLater)
        self._current_thread.start()
        return True

    def _on_tts_ready(self, file_path: str):
        if not self._active:
            self._remove_file(file_path)
            return

        if not file_path or not os.path.exists(file_path):
            self._finish()
            return

        try:
            self._playback_file = file_path
            self._current_sound = pygame.mixer.Sound(file_path)
            self._current_sound.set_volume(1.0)
            self._current_channel = self._current_sound.play()
            if self._current_channel is None:
                self._finish()
                return
            self._playback_timer.start()
        except Exception:
            logger.exception("播放TTS音频失败")
            self._finish()

    def _on_thread_finished(self):
        self._current_thread = None

    def _check_playback_finished(self):
        if self._current_channel and self._current_channel.get_busy():
            return
        self._finish()

    def _finish(self):
        if not self._active:
            return

        self._active = False
        self._playback_timer.stop()
        self._current_channel = None
        self._current_sound = None
        self._cleanup_playback_file()
        self.finished.emit()

    def stop(self, emit_finished: bool = True) -> None:
        """停止当前TTS"""
        was_active = self._active
        self._active = False
        self._playback_timer.stop()

        if self._current_channel:
            try:
                self._current_channel.stop()
            except Exception:
                logger.exception("停止TTS播放失败")
        self._current_channel = None
        self._current_sound = None
        self._cleanup_playback_file()

        if self._current_thread and self._current_thread.isRunning():
            self._current_thread.requestInterruption()
            self._current_thread.quit()
            self._current_thread.wait(1000)

        if emit_finished and was_active:
            self.finished.emit()

    def _cleanup_playback_file(self) -> None:
        file_path = self._playback_file
        self._playback_file = None
        self._remove_file(file_path)

    @staticmethod
    def _remove_file(file_path: str | None) -> None:
        if not file_path:
            return
        try:
            if os.path.exists(file_path):
                os.remove(file_path)
        except OSError:
            logger.exception("清理TTS临时文件失败: {}", file_path)
