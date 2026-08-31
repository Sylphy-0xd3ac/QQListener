"""Windows x64 傀儡进程集成测试；不接触 QQ 或 SnowLuma 二进制。"""

import struct
import subprocess
import sys
from pathlib import Path

import pytest

from src.native.injector import inject, unload

_IMAGE_BASE = 0x140000000
_ENTRY_RVA = 0x1000
_MARKER_RVA = 0x2000
_POINTER_RVA = 0x2008
_IMPORT_RVA = 0x3000
_IAT_RVA = 0x3060
_RELOC_RVA = 0x4000
_MARKER = 0xC0DEC0DE


def _probe_dll_bytes() -> bytes:
    """生成无 CRT 的最小 PE32+ DLL，避免测试依赖 MSVC/Build Tools。"""
    image = bytearray(0xC00)
    image[:2] = b"MZ"
    struct.pack_into("<I", image, 0x3C, 0x80)

    pe_offset = 0x80
    image[pe_offset : pe_offset + 4] = b"PE\0\0"
    coff = pe_offset + 4
    struct.pack_into(
        "<HHIIIHH",
        image,
        coff,
        0x8664,  # IMAGE_FILE_MACHINE_AMD64
        4,
        0,
        0,
        0,
        0xF0,
        0x2022,  # executable | large-address-aware | DLL
    )

    optional = coff + 20
    struct.pack_into("<HBB", image, optional, 0x20B, 14, 0)
    struct.pack_into("<III", image, optional + 4, 0x200, 0x600, 0)
    struct.pack_into("<II", image, optional + 16, _ENTRY_RVA, 0x1000)
    struct.pack_into("<Q", image, optional + 24, _IMAGE_BASE)
    struct.pack_into("<II", image, optional + 32, 0x1000, 0x200)
    struct.pack_into("<HHHHHH", image, optional + 40, 6, 0, 0, 0, 6, 0)
    struct.pack_into("<IIII", image, optional + 52, 0, 0x5000, 0x400, 0)
    struct.pack_into("<HH", image, optional + 68, 3, 0x160)
    struct.pack_into(
        "<QQQQII",
        image,
        optional + 72,
        0x100000,
        0x1000,
        0x100000,
        0x1000,
        0,
        16,
    )
    directories = optional + 112
    struct.pack_into("<II", image, directories + 1 * 8, _IMPORT_RVA, 40)
    struct.pack_into("<II", image, directories + 5 * 8, _RELOC_RVA, 12)

    sections = optional + 0xF0
    section_specs = (
        (b".text", 0x200, 0x1000, 0x200, 0x400, 0x60000020),
        (b".data", 0x200, 0x2000, 0x200, 0x600, 0xC0000040),
        (b".rdata", 0x200, 0x3000, 0x200, 0x800, 0x40000040),
        (b".reloc", 0x200, 0x4000, 0x200, 0xA00, 0x42000040),
    )
    for index, (name, virtual_size, rva, raw_size, raw_offset, flags) in enumerate(section_specs):
        struct.pack_into(
            "<8sIIIIIIHHI",
            image,
            sections + index * 40,
            name.ljust(8, b"\0"),
            virtual_size,
            rva,
            raw_size,
            raw_offset,
            0,
            0,
            0,
            0,
            flags,
        )

    # DllMain: PROCESS_ATTACH 时把 marker 写入 base + 0x2000，然后返回 TRUE。
    code = bytes.fromhex("83 FA 01 75 0A C7 81 00 20 00 00 DE C0 DE C0 B8 01 00 00 00 C3")
    image[0x400 : 0x400 + len(code)] = code
    struct.pack_into("<Q", image, 0x600 + 8, _IMAGE_BASE + _MARKER_RVA)

    # 一个未调用的 KERNEL32!GetCurrentProcessId 导入，验证真实远程 IAT 解析路径。
    struct.pack_into("<IIIII", image, 0x800, 0x3040, 0, 0, 0x3080, _IAT_RVA)
    struct.pack_into("<QQ", image, 0x840, 0x30A0, 0)
    struct.pack_into("<QQ", image, 0x860, 0x30A0, 0)
    image[0x880 : 0x880 + len(b"KERNEL32.dll\0")] = b"KERNEL32.dll\0"
    image[0x8A0:0x8A2] = b"\0\0"
    import_name = b"GetCurrentProcessId\0"
    image[0x8A2 : 0x8A2 + len(import_name)] = import_name

    # data+8 内的绝对指针需要 DIR64 重定位；第二项为 ABSOLUTE padding。
    struct.pack_into("<IIHH", image, 0xA00, 0x2000, 12, 0xA008, 0)
    return bytes(image)


def _build_probe_dll(tmp_path: Path) -> Path:
    dll = tmp_path / "manual_map_probe.dll"
    dll.write_bytes(_probe_dll_bytes())
    return dll


def _read_process_memory(pid: int, address: int, size: int) -> bytes | None:
    import ctypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.ReadProcessMemory.argtypes = [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.c_size_t,
        ctypes.POINTER(ctypes.c_size_t),
    ]
    kernel32.ReadProcessMemory.restype = ctypes.c_int
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]

    process = kernel32.OpenProcess(0x0010 | 0x0400, False, pid)
    if not process:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        buffer = ctypes.create_string_buffer(size)
        read_size = ctypes.c_size_t()
        if not kernel32.ReadProcessMemory(
            process,
            ctypes.c_void_p(address),
            buffer,
            size,
            ctypes.byref(read_size),
        ):
            return None
        return bytes(buffer.raw[: read_size.value])
    finally:
        kernel32.CloseHandle(process)


def test_generated_probe_is_pe32_plus_dll():
    image = _probe_dll_bytes()
    pe_offset = struct.unpack_from("<I", image, 0x3C)[0]
    optional = pe_offset + 24

    assert image[:2] == b"MZ"
    assert image[pe_offset : pe_offset + 4] == b"PE\0\0"
    assert struct.unpack_from("<H", image, pe_offset + 4)[0] == 0x8664
    assert struct.unpack_from("<H", image, optional)[0] == 0x20B
    assert struct.unpack_from("<I", image, optional + 16)[0] == _ENTRY_RVA


@pytest.mark.skipif(sys.platform != "win32", reason="仅在 Windows x64 运行")
def test_manual_map_roundtrip_against_puppet_process(tmp_path):
    dll = _build_probe_dll(tmp_path)
    host = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    handle = None
    try:
        handle = inject(host.pid, str(dll))
        assert handle.base > 0
        assert handle.entry == handle.base + _ENTRY_RVA

        marker = _read_process_memory(host.pid, handle.base + _MARKER_RVA, 4)
        relocated = _read_process_memory(host.pid, handle.base + _POINTER_RVA, 8)
        iat = _read_process_memory(host.pid, handle.base + _IAT_RVA, 8)
        assert marker is not None and struct.unpack("<I", marker)[0] == _MARKER
        assert relocated is not None
        assert struct.unpack("<Q", relocated)[0] == handle.base + _MARKER_RVA
        assert iat is not None and struct.unpack("<Q", iat)[0] != 0

        unloaded_base = handle.base
        unload(host.pid, handle)
        handle = None
        assert _read_process_memory(host.pid, unloaded_base, 2) is None
    finally:
        if handle is not None and host.poll() is None:
            unload(host.pid, handle)
        host.terminate()
        host.wait(timeout=10)
