from src.core.core_controller import CoreState
from src.core.core_service import CoreService
from src.native.injector import MapHandle


def _handle(pid: int) -> MapHandle:
    return MapHandle(base=pid * 0x1000, entry=pid * 0x1000 + 0x100, exception_table=0, size=0x3000)


def test_reconcile_running_injects_each_pid_once():
    injected = []
    service = CoreService(
        enumerate_pids=lambda: [11, 22],
        find_binary=lambda: "C:/native/hook.dll",
        inject_func=lambda pid, path: injected.append((pid, path)) or _handle(pid),
        unload_func=lambda *_: None,
        hook_active=lambda _pid: False,
        supported=True,
    )

    service.reconcile(CoreState.RUNNING)
    service.reconcile(CoreState.RUNNING)

    assert injected == [(11, "C:/native/hook.dll"), (22, "C:/native/hook.dll")]
    assert service.handles == {11: _handle(11), 22: _handle(22)}


def test_reconcile_paused_preserves_loaded_handles():
    unloaded = []
    service = CoreService(
        enumerate_pids=lambda: [11],
        find_binary=lambda: "hook.dll",
        inject_func=lambda pid, _path: _handle(pid),
        unload_func=lambda pid, handle: unloaded.append((pid, handle)),
        hook_active=lambda _pid: False,
        supported=True,
    )
    service.reconcile(CoreState.RUNNING)

    service.reconcile(CoreState.PAUSED)

    assert service.handles == {11: _handle(11)}
    assert unloaded == []


def test_reconcile_detached_unloads_owned_handles_only():
    unloaded = []
    service = CoreService(
        enumerate_pids=lambda: [11, 22],
        find_binary=lambda: "hook.dll",
        inject_func=lambda pid, _path: _handle(pid),
        unload_func=lambda pid, handle: unloaded.append((pid, handle)),
        hook_active=lambda pid: pid == 22,
        supported=True,
    )
    service.reconcile(CoreState.RUNNING)

    service.reconcile(CoreState.DETACHED)

    assert unloaded == [(11, _handle(11))]
    assert service.handles == {}


def test_reconcile_drops_handle_after_process_exits_without_unload():
    pids = [11]
    unloaded = []
    service = CoreService(
        enumerate_pids=lambda: pids,
        find_binary=lambda: "hook.dll",
        inject_func=lambda pid, _path: _handle(pid),
        unload_func=lambda pid, handle: unloaded.append((pid, handle)),
        hook_active=lambda _pid: False,
        supported=True,
    )
    service.reconcile(CoreState.RUNNING)
    pids.clear()

    service.reconcile(CoreState.RUNNING)

    assert service.handles == {}
    assert unloaded == []


def test_enumeration_failure_preserves_previous_snapshot():
    should_fail = False

    def enumerate_pids():
        if should_fail:
            raise OSError("snapshot failed")
        return [11]

    service = CoreService(
        enumerate_pids=enumerate_pids,
        find_binary=lambda: "hook.dll",
        inject_func=lambda pid, _path: _handle(pid),
        unload_func=lambda *_: None,
        hook_active=lambda _pid: False,
        supported=True,
    )
    service.reconcile(CoreState.RUNNING)
    should_fail = True

    try:
        service.reconcile(CoreState.RUNNING)
    except OSError:
        pass
    else:
        raise AssertionError("enumeration failure should propagate")

    assert service.handles == {11: _handle(11)}


def test_external_hook_is_reused_and_never_unloaded():
    injected = []
    unloaded = []
    service = CoreService(
        enumerate_pids=lambda: [33],
        find_binary=lambda: "hook.dll",
        inject_func=lambda pid, path: injected.append((pid, path)) or _handle(pid),
        unload_func=lambda pid, handle: unloaded.append((pid, handle)),
        hook_active=lambda _pid: True,
        supported=True,
    )

    service.reconcile(CoreState.RUNNING)
    service.reconcile(CoreState.DETACHED)

    assert injected == []
    assert unloaded == []


def test_running_without_binary_raises_clear_error():
    service = CoreService(
        enumerate_pids=lambda: [11],
        find_binary=lambda: None,
        inject_func=lambda pid, path: _handle(pid),
        unload_func=lambda *_: None,
        hook_active=lambda _pid: False,
        supported=True,
    )

    try:
        service.reconcile(CoreState.RUNNING)
    except FileNotFoundError as exc:
        assert "snowluma-win32-x64.dll" in str(exc)
    else:
        raise AssertionError("missing hook binary should fail")


def test_unsupported_platform_is_a_noop():
    service = CoreService(
        enumerate_pids=lambda: (_ for _ in ()).throw(AssertionError("should not enumerate")),
        find_binary=lambda: (_ for _ in ()).throw(AssertionError("should not locate")),
        supported=False,
    )

    service.reconcile(CoreState.RUNNING)
    assert service.handles == {}
