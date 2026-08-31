from __future__ import annotations

import threading
from dataclasses import dataclass
from enum import Enum


class CoreRuntimeState(Enum):
    UNSUPPORTED = "unsupported"
    DETACHED = "detached"
    PAUSED = "paused"
    NO_QQ = "no_qq"
    WAITING = "waiting"
    CONNECTED = "connected"
    ERROR = "error"


@dataclass(frozen=True)
class CoreRuntimeSnapshot:
    state: CoreRuntimeState
    detail: str = ""
    pid: int = 0


_lock = threading.RLock()
_snapshot = CoreRuntimeSnapshot(CoreRuntimeState.WAITING, "等待核心服务启动")


def get_core_runtime() -> CoreRuntimeSnapshot:
    with _lock:
        return _snapshot


def set_core_runtime(state: CoreRuntimeState, detail: str = "", pid: int = 0) -> None:
    global _snapshot
    next_snapshot = CoreRuntimeSnapshot(state, detail, pid)
    with _lock:
        _snapshot = next_snapshot
