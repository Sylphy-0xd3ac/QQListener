import pytest

from src.core import core_controller as cc
from src.core.core_controller import CoreState


@pytest.fixture(autouse=True)
def _reset_state():
    # 每个测试前重置为默认 RUNNING 且清空监听器
    cc._listeners.clear()
    cc.set_core_state(CoreState.RUNNING)
    yield
    cc._listeners.clear()
    cc.set_core_state(CoreState.RUNNING)


def test_default_is_running():
    assert cc.get_core_state() == CoreState.RUNNING
    assert cc.is_core_running() is True


def test_toggle_running_to_paused_and_back():
    assert cc.toggle_core() == CoreState.PAUSED
    assert cc.is_core_running() is False
    assert cc.toggle_core() == CoreState.RUNNING


def test_toggle_from_detached_starts_running():
    cc.set_core_state(CoreState.DETACHED)
    assert cc.toggle_core() == CoreState.RUNNING


def test_unload_goes_detached_then_toggle_restarts():
    assert cc.unload_core() == CoreState.DETACHED
    assert cc.get_core_state() == CoreState.DETACHED
    assert cc.toggle_core() == CoreState.RUNNING


def test_listener_fires_with_new_state():
    seen = []
    cc.add_core_state_listener(seen.append)
    cc.toggle_core()  # RUNNING -> PAUSED
    assert seen == [CoreState.PAUSED]


def test_no_emit_when_state_unchanged():
    seen = []
    cc.add_core_state_listener(seen.append)
    cc.set_core_state(CoreState.RUNNING)  # 已是 RUNNING
    assert seen == []


def test_set_invalid_type_raises():
    with pytest.raises(TypeError):
        cc.set_core_state("running")
