import pytest

from src.core.core_controller import CoreState, get_core_state, set_core_state
from src.core.core_runtime import (
    CoreRuntimeState,
    get_core_runtime,
    set_core_runtime,
)
from src.core.core_service import CoreService
from src.core.worker import NotificationWorker
from src.native.injector import MapHandle


def test_core_service_reports_no_qq_instead_of_running():
    service = CoreService(enumerate_pids=lambda: [], supported=True)

    service.reconcile(CoreState.RUNNING)

    snapshot = get_core_runtime()
    assert snapshot.state == CoreRuntimeState.NO_QQ
    assert "QQ" in snapshot.detail


def test_core_service_reports_waiting_after_image_mapping():
    handle = MapHandle(base=0x1000, entry=0x1100, exception_table=0, size=0x3000)
    service = CoreService(
        enumerate_pids=lambda: [22],
        find_binary=lambda: "hook.dll",
        inject_func=lambda _pid, _path: handle,
        hook_active=lambda _pid: False,
        supported=True,
    )

    service.reconcile(CoreState.RUNNING)

    snapshot = get_core_runtime()
    assert snapshot.state == CoreRuntimeState.WAITING
    assert snapshot.pid == 22


@pytest.fixture
def _running_core():
    previous = get_core_state()
    set_core_state(CoreState.RUNNING)
    yield
    set_core_state(previous)


def test_worker_marks_runtime_connected_only_when_recv_pipe_connects(_running_core):
    set_core_runtime(CoreRuntimeState.WAITING, "waiting", 33)

    NotificationWorker._on_capture_connected(33)

    snapshot = get_core_runtime()
    assert snapshot.state == CoreRuntimeState.CONNECTED
    assert snapshot.pid == 33


def test_worker_stops_reporting_runtime_while_paused():
    """暂停时捕获仍在跑，但 worker 不能再写运行态。

    否则它和 core_service 会抢着写同一份快照，界面就在"已暂停"和
    "接收管道已连接/未找到 QQ"之间来回跳。
    """
    previous = get_core_state()
    set_core_state(CoreState.PAUSED)
    try:
        NotificationWorker._publish_paused(77)
        NotificationWorker._on_capture_connected(77)
        NotificationWorker._on_capture_disconnected(77)
        NotificationWorker._publish_runtime(CoreRuntimeState.NO_QQ, "未找到 QQ")

        snapshot = get_core_runtime()
        assert snapshot.state == CoreRuntimeState.PAUSED
        assert "暂停" in snapshot.detail
    finally:
        set_core_state(previous)


def test_paused_detail_reports_the_backlog_size():
    from src.core.pending_queue import clear_pending, push_pending

    previous = get_core_state()
    set_core_state(CoreState.PAUSED)
    clear_pending()
    try:
        NotificationWorker._publish_paused()
        assert "核心仍保留" in get_core_runtime().detail

        for i in range(3):
            push_pending({"Sender": str(i)})
        NotificationWorker._publish_paused()
        assert "已积压 3 条" in get_core_runtime().detail
    finally:
        clear_pending()
        set_core_state(previous)


def test_core_service_leaves_the_paused_snapshot_to_the_worker():
    """两边都写 PAUSED 会用不同文案互相覆盖，状态球每 2 秒重绘一次。"""
    previous = get_core_state()
    set_core_state(CoreState.PAUSED)
    try:
        NotificationWorker._publish_paused(9)
        service = CoreService(enumerate_pids=lambda: [9], supported=True)
        service.reconcile(CoreState.PAUSED)

        snapshot = get_core_runtime()
        assert snapshot.state == CoreRuntimeState.PAUSED
        assert snapshot.pid == 9  # 仍是 worker 写的那份
    finally:
        set_core_state(previous)


def test_backoff_wakes_up_immediately_when_the_core_state_changes():
    """否则发完 pause/start 要等满一个退避周期界面才更新，用起来像"指令没生效"。"""
    import asyncio
    import time
    import types

    worker = types.SimpleNamespace(_running=True)
    previous = get_core_state()
    set_core_state(CoreState.RUNNING)

    async def scenario():
        task = asyncio.create_task(NotificationWorker._sleep_or_state_change(worker, 5.0))
        await asyncio.sleep(0.05)
        started = time.monotonic()
        set_core_state(CoreState.PAUSED)
        await task
        return time.monotonic() - started

    try:
        elapsed = asyncio.run(scenario())
    finally:
        set_core_state(previous)

    assert elapsed < 1.0, f"退避没有及时唤醒，等了 {elapsed:.2f}s"


def test_backoff_still_ends_on_its_own_without_a_state_change():
    import asyncio
    import types

    worker = types.SimpleNamespace(_running=True)
    previous = get_core_state()
    set_core_state(CoreState.RUNNING)
    try:
        asyncio.run(NotificationWorker._sleep_or_state_change(worker, 0.4))
    finally:
        set_core_state(previous)


def test_backoff_stops_when_the_worker_is_shutting_down():
    import asyncio
    import time
    import types

    worker = types.SimpleNamespace(_running=False)
    started = time.monotonic()
    asyncio.run(NotificationWorker._sleep_or_state_change(worker, 5.0))

    assert time.monotonic() - started < 1.0
