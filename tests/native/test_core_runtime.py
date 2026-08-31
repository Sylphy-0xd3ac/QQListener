from src.core.core_controller import CoreState
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


def test_worker_marks_runtime_connected_only_when_recv_pipe_connects():
    set_core_runtime(CoreRuntimeState.WAITING, "waiting", 33)

    NotificationWorker._on_capture_connected(33)

    snapshot = get_core_runtime()
    assert snapshot.state == CoreRuntimeState.CONNECTED
    assert snapshot.pid == 33
