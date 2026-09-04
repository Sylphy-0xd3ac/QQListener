"""核心装载生命周期服务。

状态机仍保持纯状态；本服务在后台订阅状态并执行进程发现、幂等注入与卸载。
"""

from __future__ import annotations

import sys
import threading
from collections.abc import Callable

from loguru import logger

from src.core.core_controller import (
    CoreState,
    add_core_state_listener,
    get_core_state,
    remove_core_state_listener,
)
from src.core.core_runtime import (
    CoreRuntimeState,
    get_core_runtime,
    set_core_runtime,
)
from src.native.binary_locator import find_hook_binary, missing_binary_help
from src.native.capture import enumerate_qq_pids
from src.native.injector import MapHandle, inject, unload

_RECONCILE_INTERVAL_S = 2.0


def hook_pipe_available(pid: int) -> bool:
    """只探测 SnowLuma control 管道是否存在，不建立持久连接。"""
    if sys.platform != "win32":
        return False

    import ctypes
    from ctypes import wintypes

    wait_named_pipe = ctypes.WinDLL("kernel32", use_last_error=True).WaitNamedPipeW
    wait_named_pipe.argtypes = [wintypes.LPCWSTR, wintypes.DWORD]
    wait_named_pipe.restype = wintypes.BOOL
    pipe_name = rf"\\.\pipe\mojo.{pid}.control"
    if wait_named_pipe(pipe_name, 0):
        return True
    # ERROR_SEM_TIMEOUT / ERROR_PIPE_BUSY 都表示管道存在，只是当前没有空闲实例。
    return ctypes.get_last_error() in {121, 231}


class CoreService:
    def __init__(
        self,
        *,
        enumerate_pids: Callable[[], list[int]] = enumerate_qq_pids,
        find_binary: Callable[[], str | None] = find_hook_binary,
        inject_func: Callable[[int, str], MapHandle] = inject,
        unload_func: Callable[[int, MapHandle], None] = unload,
        hook_active: Callable[[int], bool] = hook_pipe_available,
        supported: bool | None = None,
    ) -> None:
        self._enumerate_pids = enumerate_pids
        self._find_binary = find_binary
        self._inject = inject_func
        self._unload = unload_func
        self._hook_active = hook_active
        self._supported = sys.platform == "win32" if supported is None else supported
        self._handles: dict[int, MapHandle] = {}
        self._external_pids: set[int] = set()
        self._lock = threading.RLock()
        self._stop_event = threading.Event()
        self._wake_event = threading.Event()
        self._thread: threading.Thread | None = None
        self._last_error = ""

    @property
    def handles(self) -> dict[int, MapHandle]:
        with self._lock:
            return dict(self._handles)

    def start(self) -> None:
        if not self._supported:
            logger.debug("当前平台不支持核心装载器")
            set_core_runtime(CoreRuntimeState.UNSUPPORTED, "当前平台不支持核心注入")
            return
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop_event.clear()
        self._wake_event.set()
        add_core_state_listener(self._on_state_changed)
        self._thread = threading.Thread(
            target=self._run,
            name="QQListener-CoreService",
            daemon=True,
        )
        self._thread.start()

    def stop(self, *, unload_owned: bool = True) -> None:
        remove_core_state_listener(self._on_state_changed)
        self._stop_event.set()
        self._wake_event.set()
        thread = self._thread
        if thread is not None and thread.is_alive():
            thread.join(timeout=5.0)
            if thread.is_alive():
                logger.warning("核心生命周期线程未在 5 秒内停止")
                return
        self._thread = None
        if unload_owned:
            try:
                self.reconcile(CoreState.DETACHED)
            except Exception:
                logger.exception("退出时卸载核心失败")

    def _on_state_changed(self, _state: CoreState) -> None:
        self._wake_event.set()

    def _run(self) -> None:
        while not self._stop_event.is_set():
            try:
                self.reconcile()
            except Exception as exc:
                error = f"{type(exc).__name__}: {exc}"
                set_core_runtime(CoreRuntimeState.ERROR, error)
                if error != self._last_error:
                    logger.error("核心生命周期同步失败: {}", error)
                    self._last_error = error
            else:
                self._last_error = ""
            self._wake_event.wait(_RECONCILE_INTERVAL_S)
            self._wake_event.clear()

    def reconcile(self, state: CoreState | None = None) -> None:
        """把目标状态与当前 QQ 进程同步；公开以便离线确定性测试。"""
        if not self._supported:
            return
        target_state = get_core_state() if state is None else state

        # 枚举异常直接向上传递，且在成功拿到新快照前不改任何现有句柄。
        current_pids = set(self._enumerate_pids())
        with self._lock:
            dead_owned = set(self._handles) - current_pids
            for pid in dead_owned:
                self._handles.pop(pid, None)
            self._external_pids.intersection_update(current_pids)

        if target_state == CoreState.DETACHED:
            self._unload_all(current_pids)
            with self._lock:
                self._external_pids.clear()
            set_core_runtime(CoreRuntimeState.DETACHED, "核心已卸载")
            return
        if target_state == CoreState.PAUSED:
            # 暂停态由 worker 统一发布（它知道积压了多少条）；两边都写会互相覆盖，
            # 界面就在"已暂停"和"接收管道已连接"之间来回跳。
            return

        if not current_pids:
            set_core_runtime(CoreRuntimeState.NO_QQ, "未找到正在运行的 QQ 主进程")
            return

        runtime = get_core_runtime()
        connected_runtime = (
            runtime.state == CoreRuntimeState.CONNECTED and runtime.pid in current_pids
        )

        with self._lock:
            candidates = sorted(current_pids - set(self._handles) - self._external_pids)
        if not candidates:
            if not connected_runtime:
                set_core_runtime(CoreRuntimeState.WAITING, "核心已映射，等待接收管道")
            return

        needs_injection: list[int] = []
        for pid in candidates:
            if self._hook_active(pid):
                with self._lock:
                    self._external_pids.add(pid)
                logger.info("复用 QQ pid={} 已存在的核心", pid)
            else:
                needs_injection.append(pid)
        if not needs_injection:
            if not connected_runtime:
                set_core_runtime(CoreRuntimeState.WAITING, "已发现核心，等待接收管道")
            return

        dll_path = self._find_binary()
        if dll_path is None:
            raise FileNotFoundError(missing_binary_help())
        for pid in needs_injection:
            handle = self._inject(pid, dll_path)
            with self._lock:
                self._handles[pid] = handle
            logger.info(
                "核心映像已映射，等待接收管道: pid={} base=0x{:x} size={}",
                pid,
                handle.base,
                handle.size,
            )
            if not connected_runtime:
                set_core_runtime(CoreRuntimeState.WAITING, "核心已映射，等待接收管道", pid)

    def _unload_all(self, current_pids: set[int]) -> None:
        with self._lock:
            owned = list(self._handles.items())
        for pid, handle in owned:
            if pid not in current_pids:
                with self._lock:
                    self._handles.pop(pid, None)
                continue
            self._unload(pid, handle)
            with self._lock:
                if self._handles.get(pid) == handle:
                    self._handles.pop(pid, None)
            logger.info("核心已卸载: pid={} base=0x{:x}", pid, handle.base)
