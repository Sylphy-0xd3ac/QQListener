import asyncio
import contextlib
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time

import edge_tts
import pygame
from loguru import logger

from src.core.settings import get_settings
from src.ui.qt_compat import QObject, QThread, QTimer, Signal
from src.utils.qt_tasks import keep_alive

# EdgeTTS 要连微软的服务器。校园网慢/被挡时这一步能卡很久，卡住期间通知不会
# 自动关闭、下一条通知还会等它——所以必须有超时。
EDGE_TTS_TIMEOUT_S = 8.0
# 通知一多，每条都去调一次系统音量接口是纯浪费（Windows 走 COM，macOS 起子进程）。
_VOLUME_THROTTLE_S = 10.0
_volume_lock = threading.Lock()
_last_volume_boost = 0.0


def _set_windows_volume_percent(percent: float) -> None:
    if not 0 <= percent <= 100:
        raise ValueError("音量百分比必须在 0 到 100 之间")

    from ctypes import POINTER, cast

    import comtypes
    from comtypes import CLSCTX_ALL
    from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume

    # pycaw 走 COM，工作线程必须自己初始化——主线程有 Qt 代劳，新线程没有，
    # 少了这一步 CoCreateInstance 直接失败（表现就是"调音量没反应"）。
    comtypes.CoInitialize()
    try:
        devices = AudioUtilities.GetSpeakers()
        interface = devices.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
        volume = cast(interface, POINTER(IAudioEndpointVolume))
        volume.SetMute(0, None)
        volume.SetMasterVolumeLevelScalar(percent / 100.0, None)
    finally:
        comtypes.CoUninitialize()


def _set_windows_process_volume(percent: float) -> None:
    """把**本进程**在音量合成器里的那一路拉上来并取消静音。

    Windows 的每个应用有独立音量滑块。主音量拉满也盖不过自己这一路被调低——
    "调大音量没用"很多时候就是这个。
    """
    import os
    from ctypes import POINTER, cast

    import comtypes
    from pycaw.pycaw import AudioUtilities, ISimpleAudioVolume

    comtypes.CoInitialize()
    try:
        pid = os.getpid()
        for session in AudioUtilities.GetAllSessions():
            if session.Process is None or session.Process.pid != pid:
                continue
            volume = cast(
                session._ctl.QueryInterface(ISimpleAudioVolume), POINTER(ISimpleAudioVolume)
            )
            volume.SetMute(0, None)
            volume.SetMasterVolume(percent / 100.0, None)
    finally:
        comtypes.CoUninitialize()


def set_system_volume_max(throttle: bool = True) -> None:
    """尽力把系统音量拉满。

    **只能在后台线程调用**：Windows 上要过 COM（`AudioUtilities.GetSpeakers()`
    在冷机器上能耗几百毫秒到数秒），macOS 上要起 `osascript` 子进程。以前它在
    `TTSManager.speak()` 里同步跑，而 speak() 又在通知窗口的构造函数里——
    结果就是"消息来了窗口半天不出来"。
    """
    global _last_volume_boost
    if throttle:
        with _volume_lock:
            now = time.monotonic()
            if now - _last_volume_boost < _VOLUME_THROTTLE_S:
                return
            _last_volume_boost = now
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
            # 主音量之外，还要把自己这一路从合成器里拉上来。
            try:
                _set_windows_process_volume(100)
            except Exception as exc:
                logger.warning("设置本进程音量失败: {}", exc)
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
        logger.warning("设置系统音量超时")
    except Exception as exc:
        # 以前这里是 debug，失败完全不可见——"调音量没用"就查不出原因。
        logger.warning("设置系统音量失败: {}", exc)


class TTSThread(QThread):
    finished_signal = Signal(str)

    def __init__(self, text: str, parent=None, timeout: float = EDGE_TTS_TIMEOUT_S):
        super().__init__(parent)
        self.text = text if text and isinstance(text, str) else ""
        self.settings = get_settings()
        self.timeout = timeout

    def run(self):
        """执行TTS"""
        if not self.text:
            self.finished_signal.emit("")
            return

        # 放在这里而不是 speak()：这是阻塞调用，绝不能压在 UI 线程上。
        # 而且默认不动系统音量——播放音量由 Playback_Volume 控制就够了。
        if self.settings.force_system_volume:
            set_system_volume_max()
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
                # 没有超时的话，网络一慢就是无限期挂起。
                await asyncio.wait_for(communicate.save(output_file), timeout=self.timeout)
                self.finished_signal.emit(output_file)
            except (TimeoutError, asyncio.TimeoutError):
                logger.warning(
                    "Edge TTS {}秒未返回，跳过本条播报（网络慢或被拦截时可关掉 Edge_TTS）",
                    self.timeout,
                )
                self._remove_file(output_file)
                self.finished_signal.emit("")
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
            engine.setProperty("volume", self.settings.playback_volume / 100.0)
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
        self._current_thread = TTSThread(text)
        # 不设 parent：TTSManager 挂在通知窗口上，窗口先关就会连着还在跑的线程一起析构。
        # 存活交给 keep_alive 登记，线程跑完自己注销。
        keep_alive(self._current_thread)
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
            self._current_sound.set_volume(self.settings.playback_volume / 100.0)
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

        # 旧线程可能正卡在 EdgeTTS 的网络调用里，quit() 打断不了它。
        # 这里绝不能 wait()——每来一条新通知就冻结 UI 一秒。改成"断信号后放生"，
        # 它跑完自己会 deleteLater。
        # 注意：放生前它必须已经登记在 keep_alive 里，否则这里一置 None 就没人持有，
        # GC 析构一个还在跑的 QThread = 进程 abort（且没有 Python traceback）。
        thread = self._current_thread
        if thread is not None and thread.isRunning():
            with contextlib.suppress(RuntimeError, TypeError):
                thread.finished_signal.disconnect(self._on_tts_ready)
            thread.finished_signal.connect(TTSThread._remove_file)
            thread.requestInterruption()
            self._current_thread = None

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
