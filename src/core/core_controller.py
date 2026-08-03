"""核心运行状态机。

核心（注入 + 捕获）有三态：
- DETACHED  未注入：钩子不在 QQ 里，没有管道。
- RUNNING   运行中：钩子已注入且捕获连通。
- PAUSED    已暂停：钩子仍在 QQ 里，但停止读管道（轻量、可秒恢复）。

交互约定（由 UI 层触发）：
- 单击 → toggle_core()：RUNNING⇄PAUSED；DETACHED→RUNNING（重新启动）。
- 长按 → unload_core()：任意态 → DETACHED（把钩子从 QQ 拔出，需二次确认）。

本模块只管状态与监听器；真正的注入/卸载/连管道副作用由订阅者
（app 层，后续 Plan B/C）在收到状态变化时执行。
"""

from __future__ import annotations

import contextlib
from collections.abc import Callable
from enum import Enum


class CoreState(Enum):
    DETACHED = "detached"
    RUNNING = "running"
    PAUSED = "paused"


_state: CoreState = CoreState.RUNNING
_listeners: list[Callable[[CoreState], None]] = []


def get_core_state() -> CoreState:
    return _state


def set_core_state(state: CoreState) -> None:
    global _state
    if not isinstance(state, CoreState):
        raise TypeError(f"expected CoreState, got {type(state)!r}")
    if _state == state:
        return
    _state = state
    _emit(state)


def toggle_core() -> CoreState:
    """单击：运行⇄暂停；未注入→运行。返回新状态。"""
    if _state == CoreState.RUNNING:
        set_core_state(CoreState.PAUSED)
    else:
        set_core_state(CoreState.RUNNING)
    return _state


def unload_core() -> CoreState:
    """长按（已二次确认）：卸载核心 → 未注入。返回新状态。"""
    set_core_state(CoreState.DETACHED)
    return _state


def is_core_running() -> bool:
    return _state == CoreState.RUNNING


def add_core_state_listener(listener: Callable[[CoreState], None]) -> None:
    if listener not in _listeners:
        _listeners.append(listener)


def remove_core_state_listener(listener: Callable[[CoreState], None]) -> None:
    with contextlib.suppress(ValueError):
        _listeners.remove(listener)


def _emit(state: CoreState) -> None:
    failed: list[Callable[[CoreState], None]] = []
    for listener in list(_listeners):
        try:
            listener(state)
        except RuntimeError:
            failed.append(listener)
    for listener in failed:
        remove_core_state_listener(listener)
