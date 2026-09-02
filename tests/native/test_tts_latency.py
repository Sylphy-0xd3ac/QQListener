"""TTS 不能拖慢通知：音量提升不在 UI 线程、EdgeTTS 有超时、stop() 不阻塞。"""

import asyncio
import os
import tempfile
import types

import pytest

from src.utils import tts as tts_module
from src.utils.tts import TTSManager, TTSThread


class _Signal:
    def __init__(self):
        self.emitted: list[str] = []

    def emit(self, value):
        self.emitted.append(value)


def _thread_stub(*, edge=True, timeout=5.0, text="播报内容"):
    """借 TTSThread 的方法但不建真 QThread。"""
    return types.SimpleNamespace(
        text=text,
        timeout=timeout,
        settings=types.SimpleNamespace(
            edge_tts_enabled=edge,
            edge_voice="zh-CN-XiaoyiNeural",
            edge_rate="+0%",
            edge_pitch="+0Hz",
            edge_volume="+0%",
            force_system_volume=True,
            playback_volume=50,
        ),
        finished_signal=_Signal(),
        _run_edge_tts=lambda: None,
        _run_system_tts=lambda: None,
        _remove_file=TTSThread._remove_file,
    )


def test_volume_boost_runs_inside_the_worker_not_on_the_ui_thread(monkeypatch):
    """以前它在 speak() 里同步跑，而 speak() 在通知窗口构造函数里——窗口就出不来。"""
    calls = []
    monkeypatch.setattr(tts_module, "set_system_volume_max", lambda *a, **k: calls.append(1))

    stub = _thread_stub()
    TTSThread.run(stub)

    assert calls == [1]


def test_speak_does_not_touch_the_volume_api(monkeypatch):
    calls = []
    monkeypatch.setattr(tts_module, "set_system_volume_max", lambda *a, **k: calls.append(1))

    manager = TTSManager.__new__(TTSManager)
    manager.settings = types.SimpleNamespace(tts_enabled=False)

    assert manager.speak("你好") is False
    assert calls == []


def test_volume_boost_is_throttled(monkeypatch):
    applied = []
    monkeypatch.setattr(tts_module.sys, "platform", "linux")
    monkeypatch.setattr(tts_module.shutil, "which", lambda name: name == "amixer")
    monkeypatch.setattr(tts_module.subprocess, "run", lambda *a, **k: applied.append(1))
    monkeypatch.setattr(tts_module, "_last_volume_boost", 0.0)

    tts_module.set_system_volume_max()
    tts_module.set_system_volume_max()

    assert len(applied) == 1  # 第二次在冷却期内


def test_volume_boost_can_skip_the_throttle(monkeypatch):
    applied = []
    monkeypatch.setattr(tts_module.sys, "platform", "linux")
    monkeypatch.setattr(tts_module.shutil, "which", lambda name: name == "amixer")
    monkeypatch.setattr(tts_module.subprocess, "run", lambda *a, **k: applied.append(1))

    tts_module.set_system_volume_max(throttle=False)
    tts_module.set_system_volume_max(throttle=False)

    assert len(applied) == 2


class _HangingCommunicate:
    def __init__(self, **_kw):
        pass

    async def save(self, _path):
        await asyncio.sleep(30)


def test_edge_tts_gives_up_instead_of_hanging_forever(monkeypatch):
    """校园网慢时 communicate.save() 会一直挂着；卡住期间通知既不播报也不自动关。"""
    monkeypatch.setattr(tts_module.edge_tts, "Communicate", _HangingCommunicate)

    stub = _thread_stub(timeout=0.05)
    TTSThread._run_edge_tts(stub)

    assert stub.finished_signal.emitted == [""]  # 空路径 = 本条不播报，但流程继续


def test_edge_tts_timeout_cleans_up_its_temp_file(monkeypatch):
    created = []
    real_mkstemp = tempfile.mkstemp

    def spy(*args, **kwargs):
        fd, path = real_mkstemp(*args, **kwargs)
        created.append(path)
        return fd, path

    monkeypatch.setattr(tts_module.tempfile, "mkstemp", spy)
    monkeypatch.setattr(tts_module.edge_tts, "Communicate", _HangingCommunicate)

    stub = _thread_stub(timeout=0.05)
    TTSThread._run_edge_tts(stub)

    assert created and not os.path.exists(created[0])


def test_empty_text_short_circuits_without_synthesis(monkeypatch):
    monkeypatch.setattr(tts_module, "set_system_volume_max", lambda *a, **k: None)
    stub = _thread_stub(text="")

    TTSThread.run(stub)

    assert stub.finished_signal.emitted == [""]


@pytest.mark.parametrize("still_running", [True, False])
def test_stop_never_blocks_on_a_stuck_worker(still_running):
    """以前这里 wait(1000)：上一条播报卡在网络里时，每来一条新通知就冻 UI 一秒。"""
    waits = []
    disconnected = []

    class _Thread:
        finished_signal = None

        def isRunning(self):
            return still_running

        def requestInterruption(self):
            pass

        def wait(self, ms):
            waits.append(ms)
            return True

        def quit(self):
            pass

    thread = _Thread()
    thread.finished_signal = types.SimpleNamespace(
        disconnect=lambda _cb: disconnected.append(1),
        connect=lambda _cb: None,
    )

    manager = TTSManager.__new__(TTSManager)
    manager._active = False
    manager._playback_timer = types.SimpleNamespace(stop=lambda: None)
    manager._current_channel = None
    manager._current_sound = None
    manager._playback_file = None
    manager._current_thread = thread

    manager.stop(emit_finished=False)

    assert waits == []
    if still_running:
        assert disconnected == [1]
        assert manager._current_thread is None


# ---------- 音量 ----------


def test_playback_volume_defaults_to_half_not_full():
    from src.core.settings import Settings

    fresh = Settings.__new__(Settings)
    fresh._data = {}
    assert fresh.playback_volume == 50
    assert fresh.force_system_volume is False


def test_playback_volume_is_clamped_and_type_safe():
    from src.core.settings import Settings

    fresh = Settings.__new__(Settings)
    for raw, expected in ((0, 0), (100, 100), (250, 100), (-5, 0), ("大声", 50), (None, 50)):
        fresh._data = {"Playback_Volume": raw}
        assert fresh.playback_volume == expected


def test_system_volume_is_left_alone_unless_explicitly_enabled(monkeypatch):
    """默认不该盖掉用户自己调好的系统音量。"""
    calls = []
    monkeypatch.setattr(tts_module, "set_system_volume_max", lambda *a, **k: calls.append(1))

    stub = _thread_stub()
    stub.settings.force_system_volume = False
    TTSThread.run(stub)
    assert calls == []

    stub.settings.force_system_volume = True
    TTSThread.run(stub)
    assert calls == [1]


def test_volume_failures_are_visible_not_swallowed(monkeypatch, caplog):
    monkeypatch.setattr(tts_module.sys, "platform", "win32")
    monkeypatch.setattr(
        tts_module,
        "_set_windows_volume_percent",
        lambda _p: (_ for _ in ()).throw(OSError("COM 未初始化")),
    )
    monkeypatch.setattr(tts_module, "_last_volume_boost", 0.0)

    messages = []
    handler_id = tts_module.logger.add(lambda m: messages.append(m), level="WARNING")
    try:
        tts_module.set_system_volume_max(throttle=False)
    finally:
        tts_module.logger.remove(handler_id)

    assert any("设置系统音量失败" in str(m) for m in messages)
