"""Windows x64 傀儡进程集成测试；不接触 QQ 或 SnowLuma 二进制。"""

import shutil
import subprocess
import sys
from pathlib import Path

import pytest

from src.native.injector import inject, unload

pytestmark = [
    pytest.mark.skipif(sys.platform != "win32", reason="仅在 Windows x64 运行"),
    pytest.mark.skipif(shutil.which("cl") is None, reason="需要 MSVC Developer Command Prompt"),
]

_EVENT_NAME = "Local\\QQListenerManualMapProbe"


def _event_exists() -> bool:
    import ctypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenEventW.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_wchar_p]
    kernel32.OpenEventW.restype = ctypes.c_void_p
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel32.OpenEventW(0x00100000, False, _EVENT_NAME)  # SYNCHRONIZE
    if not handle:
        return False
    kernel32.CloseHandle(handle)
    return True


def _build_probe_dll(tmp_path: Path) -> Path:
    source = Path(__file__).parents[1] / "windows" / "manual_map_probe.c"
    obj = tmp_path / "manual_map_probe.obj"
    dll = tmp_path / "manual_map_probe.dll"
    subprocess.run(
        ["cl", "/nologo", "/c", "/O2", "/GS-", str(source), f"/Fo{obj}"],
        check=True,
        cwd=tmp_path,
    )
    subprocess.run(
        [
            "link",
            "/DLL",
            "/NOLOGO",
            "/NODEFAULTLIB",
            "/ENTRY:DllMain",
            f"/OUT:{dll}",
            str(obj),
            "kernel32.lib",
        ],
        check=True,
        cwd=tmp_path,
    )
    return dll


def test_manual_map_roundtrip_against_puppet_process(tmp_path):
    assert not _event_exists(), "probe event from an earlier failed run still exists"
    dll = _build_probe_dll(tmp_path)
    host = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    handle = None
    try:
        handle = inject(host.pid, str(dll))
        assert handle.base > 0
        assert handle.entry >= handle.base
        assert _event_exists()

        unload(host.pid, handle)
        handle = None
        assert not _event_exists()
    finally:
        if handle is not None and host.poll() is None:
            unload(host.pid, handle)
        host.terminate()
        host.wait(timeout=10)
