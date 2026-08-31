"""Windows x64 PE 手动映射装载器。

模块本身可在非 Windows 平台导入，以便测试 PE 映像构建逻辑；只有 ``inject`` / ``unload``
会触碰 Windows API。装载流程不把目标 DLL 交给 LoadLibrary，只使用 LoadLibraryW 确保目标
进程具备 DLL 所依赖的系统模块。
"""

from __future__ import annotations

import contextlib
import os
import struct
import sys
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

IMAGE_FILE_MACHINE_AMD64 = 0x8664
IMAGE_NT_OPTIONAL_HDR64_MAGIC = 0x20B
IMAGE_FILE_DLL = 0x2000

IMAGE_DIRECTORY_ENTRY_EXCEPTION = 3
IMAGE_DIRECTORY_ENTRY_BASERELOC = 5
IMAGE_DIRECTORY_ENTRY_TLS = 9

IMAGE_REL_BASED_ABSOLUTE = 0
IMAGE_REL_BASED_HIGHLOW = 3
IMAGE_REL_BASED_DIR64 = 10

IMAGE_SCN_MEM_EXECUTE = 0x20000000
IMAGE_SCN_MEM_READ = 0x40000000
IMAGE_SCN_MEM_WRITE = 0x80000000

PAGE_NOACCESS = 0x01
PAGE_READONLY = 0x02
PAGE_READWRITE = 0x04
PAGE_EXECUTE = 0x10
PAGE_EXECUTE_READ = 0x20
PAGE_EXECUTE_READWRITE = 0x40

DLL_PROCESS_DETACH = 0
DLL_PROCESS_ATTACH = 1

_RUNTIME_FUNCTION_SIZE = 12
_MAX_IMAGE_SIZE = 1024 * 1024 * 1024
_MAX_TLS_CALLBACKS = 4096


class InjectionError(RuntimeError):
    """装载器无法安全完成操作。"""


class RemoteCallTimeout(InjectionError):
    """远程线程仍可能执行；调用方不得释放其代码或相关映像。"""


@dataclass(frozen=True)
class MapHandle:
    base: int
    entry: int
    exception_table: int
    size: int


@dataclass(frozen=True)
class SectionMapping:
    address: int
    size: int
    protection: int


@dataclass(frozen=True)
class PreparedImage:
    data: bytes
    entry: int
    exception_table: int
    exception_count: int
    sections: tuple[SectionMapping, ...]
    tls_callbacks: tuple[int, ...]


ImportResolver = Callable[[str, bytes | None, int | None], int]


def _checked_range(offset: int, size: int, limit: int, label: str) -> None:
    if offset < 0 or size < 0 or offset > limit or size > limit - offset:
        raise InjectionError(f"{label} 超出 PE 映像范围")


def _directory(pe: Any, index: int) -> Any | None:
    directories = getattr(pe.OPTIONAL_HEADER, "DATA_DIRECTORY", ())
    if index >= len(directories):
        return None
    directory = directories[index]
    if not int(getattr(directory, "VirtualAddress", 0)):
        return None
    return directory


def _section_protection(characteristics: int) -> int:
    executable = bool(characteristics & IMAGE_SCN_MEM_EXECUTE)
    readable = bool(characteristics & IMAGE_SCN_MEM_READ)
    writable = bool(characteristics & IMAGE_SCN_MEM_WRITE)
    if executable:
        if writable:
            return PAGE_EXECUTE_READWRITE
        if readable:
            return PAGE_EXECUTE_READ
        return PAGE_EXECUTE
    if writable:
        return PAGE_READWRITE
    if readable:
        return PAGE_READONLY
    return PAGE_NOACCESS


def _copy_image(pe: Any, raw: bytes) -> bytearray:
    image_size = int(pe.OPTIONAL_HEADER.SizeOfImage)
    header_size = int(pe.OPTIONAL_HEADER.SizeOfHeaders)
    if image_size <= 0 or image_size > _MAX_IMAGE_SIZE:
        raise InjectionError(f"异常的 PE SizeOfImage: {image_size}")
    if header_size <= 0 or header_size > len(raw):
        raise InjectionError("PE 头被截断")
    _checked_range(0, header_size, image_size, "PE 头")

    image = bytearray(image_size)
    image[:header_size] = raw[:header_size]
    for section in pe.sections:
        virtual_address = int(section.VirtualAddress)
        raw_size = int(section.SizeOfRawData)
        raw_offset = int(section.PointerToRawData)
        if raw_size == 0:
            continue
        _checked_range(raw_offset, raw_size, len(raw), "PE 节原始数据")
        _checked_range(virtual_address, raw_size, image_size, "PE 节虚拟数据")
        image[virtual_address : virtual_address + raw_size] = raw[
            raw_offset : raw_offset + raw_size
        ]
    return image


def _apply_relocations(pe: Any, image: bytearray, base: int) -> None:
    preferred_base = int(pe.OPTIONAL_HEADER.ImageBase)
    delta = base - preferred_base
    if delta == 0:
        return

    blocks = getattr(pe, "DIRECTORY_ENTRY_BASERELOC", ())
    if not blocks:
        raise InjectionError("目标地址偏离首选基址，但 PE 没有基址重定位表")

    for block in blocks:
        for relocation in block.entries:
            relocation_type = int(relocation.type)
            rva = int(relocation.rva)
            if relocation_type == IMAGE_REL_BASED_ABSOLUTE:
                continue
            if relocation_type == IMAGE_REL_BASED_DIR64:
                _checked_range(rva, 8, len(image), "DIR64 重定位")
                value = struct.unpack_from("<Q", image, rva)[0]
                struct.pack_into("<Q", image, rva, (value + delta) & 0xFFFFFFFFFFFFFFFF)
                continue
            if relocation_type == IMAGE_REL_BASED_HIGHLOW:
                _checked_range(rva, 4, len(image), "HIGHLOW 重定位")
                value = struct.unpack_from("<I", image, rva)[0]
                struct.pack_into("<I", image, rva, (value + delta) & 0xFFFFFFFF)
                continue
            raise InjectionError(f"不支持的 PE 重定位类型: {relocation_type}")


def _patch_imports(pe: Any, image: bytearray, resolver: ImportResolver) -> None:
    preferred_base = int(pe.OPTIONAL_HEADER.ImageBase)
    for descriptor in getattr(pe, "DIRECTORY_ENTRY_IMPORT", ()):
        try:
            module = bytes(descriptor.dll).decode("ascii")
        except (AttributeError, UnicodeDecodeError) as exc:
            raise InjectionError("PE 导入模块名无效") from exc
        for imported in descriptor.imports:
            name = bytes(imported.name) if imported.name is not None else None
            ordinal = int(imported.ordinal) if imported.ordinal is not None else None
            address = int(resolver(module, name, ordinal))
            if not address:
                symbol = name.decode("ascii", "replace") if name is not None else f"#{ordinal}"
                raise InjectionError(f"无法解析导入: {module}!{symbol}")
            iat_rva = int(imported.address) - preferred_base
            _checked_range(iat_rva, 8, len(image), "IAT 项")
            struct.pack_into("<Q", image, iat_rva, address)


def _tls_callbacks(pe: Any, image: bytearray, base: int) -> tuple[int, ...]:
    directory = _directory(pe, IMAGE_DIRECTORY_ENTRY_TLS)
    if directory is None:
        return ()

    tls_rva = int(directory.VirtualAddress)
    # IMAGE_TLS_DIRECTORY64.AddressOfCallBacks 位于偏移 24。
    _checked_range(tls_rva, 32, len(image), "TLS 目录")
    callbacks_va = struct.unpack_from("<Q", image, tls_rva + 24)[0]
    if callbacks_va == 0:
        return ()
    callbacks_rva = callbacks_va - base
    if callbacks_rva < 0:
        raise InjectionError("TLS 回调表地址不在映像内")

    callbacks: list[int] = []
    for index in range(_MAX_TLS_CALLBACKS):
        offset = callbacks_rva + index * 8
        _checked_range(offset, 8, len(image), "TLS 回调表")
        callback = struct.unpack_from("<Q", image, offset)[0]
        if callback == 0:
            return tuple(callbacks)
        if not base <= callback < base + len(image):
            raise InjectionError("TLS 回调地址不在映像内")
        callbacks.append(callback)
    raise InjectionError("TLS 回调表未终止")


def _prepare_image(pe: Any, raw: bytes, base: int, resolver: ImportResolver) -> PreparedImage:
    """构建与目标基址匹配的完整内存映像；不调用任何平台 API。"""
    image = _copy_image(pe, raw)
    _apply_relocations(pe, image, base)
    _patch_imports(pe, image, resolver)

    entry_rva = int(pe.OPTIONAL_HEADER.AddressOfEntryPoint)
    if entry_rva:
        _checked_range(entry_rva, 1, len(image), "PE 入口点")
    entry = base + entry_rva if entry_rva else 0

    exception_table = 0
    exception_count = 0
    exception_directory = _directory(pe, IMAGE_DIRECTORY_ENTRY_EXCEPTION)
    if exception_directory is not None:
        exception_rva = int(exception_directory.VirtualAddress)
        exception_size = int(exception_directory.Size)
        _checked_range(exception_rva, exception_size, len(image), "x64 异常表")
        if exception_size % _RUNTIME_FUNCTION_SIZE:
            raise InjectionError("x64 异常表大小不是 RUNTIME_FUNCTION 的整数倍")
        exception_table = base + exception_rva
        exception_count = exception_size // _RUNTIME_FUNCTION_SIZE

    sections: list[SectionMapping] = []
    for section in pe.sections:
        size = max(int(section.Misc_VirtualSize), int(section.SizeOfRawData))
        if size <= 0:
            continue
        rva = int(section.VirtualAddress)
        _checked_range(rva, size, len(image), "PE 节保护范围")
        sections.append(
            SectionMapping(
                address=base + rva,
                size=size,
                protection=_section_protection(int(section.Characteristics)),
            )
        )

    return PreparedImage(
        data=bytes(image),
        entry=entry,
        exception_table=exception_table,
        exception_count=exception_count,
        sections=tuple(sections),
        tls_callbacks=_tls_callbacks(pe, image, base),
    )


def _load_pe(raw: bytes) -> Any:
    try:
        import pefile
    except ImportError as exc:  # pragma: no cover - Windows 安装环境异常
        raise InjectionError("缺少 pefile 依赖，请先运行 uv sync") from exc
    try:
        return pefile.PE(data=raw, fast_load=False)
    except pefile.PEFormatError as exc:
        raise InjectionError(f"无效的 PE 文件: {exc}") from exc


def _validate_pe(pe: Any) -> None:
    if int(pe.FILE_HEADER.Machine) != IMAGE_FILE_MACHINE_AMD64:
        raise InjectionError("只支持 x64 (AMD64) DLL")
    if int(pe.OPTIONAL_HEADER.Magic) != IMAGE_NT_OPTIONAL_HDR64_MAGIC:
        raise InjectionError("目标文件不是 PE32+ x64 映像")
    if not int(pe.FILE_HEADER.Characteristics) & IMAGE_FILE_DLL:
        raise InjectionError("目标 PE 不是 DLL")


class _WindowsProcess:
    """一次 OpenProcess 会话及其远程调用辅助。"""

    PROCESS_ACCESS = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400
    MEM_COMMIT = 0x1000
    MEM_RESERVE = 0x2000
    MEM_RELEASE = 0x8000
    WAIT_OBJECT_0 = 0
    WAIT_TIMEOUT = 0x102
    INFINITE_TIMEOUT_MS = 30_000
    TH32CS_SNAPMODULE = 0x00000008
    TH32CS_SNAPMODULE32 = 0x00000010
    GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004
    GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x00000002

    def __init__(self, pid: int) -> None:
        if sys.platform != "win32":  # pragma: no cover - 由 public API 提前拦截
            raise OSError("手动映射装载器仅支持 Windows x64")
        if struct.calcsize("P") != 8:
            raise InjectionError("注入 x64 进程需要 64 位 Python")

        import ctypes
        from ctypes import wintypes

        self.ctypes = ctypes
        self.wintypes = wintypes
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.ntdll = ctypes.WinDLL("ntdll", use_last_error=True)
        self._configure_api()
        self.pid = pid
        self.handle = self.kernel32.OpenProcess(self.PROCESS_ACCESS, False, pid)
        if not self.handle:
            self._winerror(f"OpenProcess({pid})")
        self._local_modules: list[int] = []
        try:
            self._validate_target_architecture()
        except Exception:
            self.close()
            raise

    def _configure_api(self) -> None:
        c = self.ctypes
        w = self.wintypes
        k = self.kernel32
        k.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
        k.OpenProcess.restype = w.HANDLE
        k.CloseHandle.argtypes = [w.HANDLE]
        k.CloseHandle.restype = w.BOOL
        k.VirtualAllocEx.argtypes = [w.HANDLE, c.c_void_p, c.c_size_t, w.DWORD, w.DWORD]
        k.VirtualAllocEx.restype = c.c_void_p
        k.VirtualFreeEx.argtypes = [w.HANDLE, c.c_void_p, c.c_size_t, w.DWORD]
        k.VirtualFreeEx.restype = w.BOOL
        k.VirtualProtectEx.argtypes = [
            w.HANDLE,
            c.c_void_p,
            c.c_size_t,
            w.DWORD,
            c.POINTER(w.DWORD),
        ]
        k.VirtualProtectEx.restype = w.BOOL
        k.WriteProcessMemory.argtypes = [
            w.HANDLE,
            c.c_void_p,
            c.c_void_p,
            c.c_size_t,
            c.POINTER(c.c_size_t),
        ]
        k.WriteProcessMemory.restype = w.BOOL
        k.ReadProcessMemory.argtypes = [
            w.HANDLE,
            c.c_void_p,
            c.c_void_p,
            c.c_size_t,
            c.POINTER(c.c_size_t),
        ]
        k.ReadProcessMemory.restype = w.BOOL
        k.FlushInstructionCache.argtypes = [w.HANDLE, c.c_void_p, c.c_size_t]
        k.FlushInstructionCache.restype = w.BOOL
        k.CreateRemoteThread.argtypes = [
            w.HANDLE,
            c.c_void_p,
            c.c_size_t,
            c.c_void_p,
            c.c_void_p,
            w.DWORD,
            c.POINTER(w.DWORD),
        ]
        k.CreateRemoteThread.restype = w.HANDLE
        k.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
        k.WaitForSingleObject.restype = w.DWORD
        k.GetExitCodeThread.argtypes = [w.HANDLE, c.POINTER(w.DWORD)]
        k.GetExitCodeThread.restype = w.BOOL
        k.GetModuleHandleW.argtypes = [w.LPCWSTR]
        k.GetModuleHandleW.restype = c.c_void_p
        k.LoadLibraryW.argtypes = [w.LPCWSTR]
        k.LoadLibraryW.restype = c.c_void_p
        k.FreeLibrary.argtypes = [c.c_void_p]
        k.FreeLibrary.restype = w.BOOL
        k.GetProcAddress.argtypes = [c.c_void_p, c.c_void_p]
        k.GetProcAddress.restype = c.c_void_p
        k.GetModuleHandleExW.argtypes = [w.DWORD, c.c_void_p, c.POINTER(c.c_void_p)]
        k.GetModuleHandleExW.restype = w.BOOL
        k.GetModuleFileNameW.argtypes = [c.c_void_p, w.LPWSTR, w.DWORD]
        k.GetModuleFileNameW.restype = w.DWORD
        k.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
        k.CreateToolhelp32Snapshot.restype = w.HANDLE

    def _winerror(self, operation: str) -> None:
        error = self.ctypes.get_last_error()
        raise OSError(error, f"{operation}: {self.ctypes.FormatError(error)}")

    def _validate_target_architecture(self) -> None:
        c = self.ctypes
        process_machine = c.c_ushort()
        native_machine = c.c_ushort()
        is_wow64_process2 = getattr(self.kernel32, "IsWow64Process2", None)
        if is_wow64_process2 is not None:
            is_wow64_process2.argtypes = [
                self.wintypes.HANDLE,
                c.POINTER(c.c_ushort),
                c.POINTER(c.c_ushort),
            ]
            is_wow64_process2.restype = self.wintypes.BOOL
            if not is_wow64_process2(
                self.handle, c.byref(process_machine), c.byref(native_machine)
            ):
                self._winerror("IsWow64Process2")
            machine = process_machine.value or native_machine.value
            if machine != IMAGE_FILE_MACHINE_AMD64:
                raise InjectionError(f"目标进程不是 x64 (machine=0x{machine:04x})")

    def close(self) -> None:
        if getattr(self, "handle", None):
            self.kernel32.CloseHandle(self.handle)
            self.handle = None
        for module in getattr(self, "_local_modules", ()):
            self.kernel32.FreeLibrary(module)
        self._local_modules = []

    def __enter__(self) -> _WindowsProcess:
        return self

    def __exit__(self, *_exc: object) -> None:
        self.close()

    def alloc(self, size: int, *, address: int = 0, protection: int = PAGE_READWRITE) -> int:
        result = self.kernel32.VirtualAllocEx(
            self.handle,
            self.ctypes.c_void_p(address) if address else None,
            size,
            self.MEM_COMMIT | self.MEM_RESERVE,
            protection,
        )
        return int(result or 0)

    def free(self, address: int) -> None:
        if not self.kernel32.VirtualFreeEx(
            self.handle, self.ctypes.c_void_p(address), 0, self.MEM_RELEASE
        ):
            self._winerror("VirtualFreeEx")

    def write(self, address: int, data: bytes) -> None:
        if not data:
            return
        buffer = self.ctypes.create_string_buffer(data)
        written = self.ctypes.c_size_t()
        if not self.kernel32.WriteProcessMemory(
            self.handle,
            self.ctypes.c_void_p(address),
            buffer,
            len(data),
            self.ctypes.byref(written),
        ):
            self._winerror("WriteProcessMemory")
        if written.value != len(data):
            raise InjectionError(f"WriteProcessMemory 短写: {written.value}/{len(data)}")

    def read(self, address: int, size: int) -> bytes:
        buffer = self.ctypes.create_string_buffer(size)
        read_size = self.ctypes.c_size_t()
        if not self.kernel32.ReadProcessMemory(
            self.handle,
            self.ctypes.c_void_p(address),
            buffer,
            size,
            self.ctypes.byref(read_size),
        ):
            self._winerror("ReadProcessMemory")
        if read_size.value != size:
            raise InjectionError(f"ReadProcessMemory 短读: {read_size.value}/{size}")
        return bytes(buffer.raw)

    def protect(self, address: int, size: int, protection: int) -> None:
        previous = self.wintypes.DWORD()
        if not self.kernel32.VirtualProtectEx(
            self.handle,
            self.ctypes.c_void_p(address),
            size,
            protection,
            self.ctypes.byref(previous),
        ):
            self._winerror("VirtualProtectEx")

    def flush(self, address: int, size: int) -> None:
        if not self.kernel32.FlushInstructionCache(
            self.handle, self.ctypes.c_void_p(address), size
        ):
            self._winerror("FlushInstructionCache")

    def remote_call(self, function: int, args: tuple[int, ...]) -> int:
        if not function:
            raise InjectionError("远程调用地址为空")
        if len(args) > 4:
            raise ValueError("x64 远程调用最多支持四个寄存器参数")
        padded = (*args, *(0 for _ in range(4 - len(args))))

        allocation = self.alloc(0x1000, protection=PAGE_EXECUTE_READWRITE)
        if not allocation:
            self._winerror("VirtualAllocEx(remote call)")
        result_address = allocation + 0x200
        registers = (b"\x48\xb9", b"\x48\xba", b"\x49\xb8", b"\x49\xb9")
        code = bytearray(b"\x48\x83\xec\x28")  # sub rsp, 0x28 (shadow space + alignment)
        for opcode, value in zip(registers, padded, strict=True):
            code.extend(opcode)
            code.extend(struct.pack("<Q", int(value)))
        code.extend(b"\x48\xb8")  # mov rax, function
        code.extend(struct.pack("<Q", function))
        code.extend(b"\xff\xd0")  # call rax
        code.extend(b"\x49\xbb")  # mov r11, result_address
        code.extend(struct.pack("<Q", result_address))
        code.extend(b"\x49\x89\x03")  # mov [r11], rax
        code.extend(b"\x48\x83\xc4\x28\xc3")

        thread = None
        completed = False
        try:
            self.write(allocation, bytes(code))
            self.flush(allocation, len(code))
            thread_id = self.wintypes.DWORD()
            thread = self.kernel32.CreateRemoteThread(
                self.handle,
                None,
                0,
                self.ctypes.c_void_p(allocation),
                None,
                0,
                self.ctypes.byref(thread_id),
            )
            if not thread:
                self._winerror("CreateRemoteThread")
            wait_result = self.kernel32.WaitForSingleObject(thread, self.INFINITE_TIMEOUT_MS)
            if wait_result == self.WAIT_TIMEOUT:
                # 线程仍可能在执行，不能释放它正在运行的 trampoline。
                raise RemoteCallTimeout("远程调用 30 秒未返回；保留调用页以避免目标进程崩溃")
            if wait_result != self.WAIT_OBJECT_0:
                self._winerror("WaitForSingleObject")
            completed = True
            return struct.unpack("<Q", self.read(result_address, 8))[0]
        finally:
            if thread:
                self.kernel32.CloseHandle(thread)
            if completed or thread is None:
                self.free(allocation)

    def _module_entries(self) -> dict[str, tuple[int, str]]:
        c = self.ctypes
        w = self.wintypes

        class MODULEENTRY32W(c.Structure):
            _fields_ = [
                ("dwSize", w.DWORD),
                ("th32ModuleID", w.DWORD),
                ("th32ProcessID", w.DWORD),
                ("GlblcntUsage", w.DWORD),
                ("ProccntUsage", w.DWORD),
                ("modBaseAddr", c.c_void_p),
                ("modBaseSize", w.DWORD),
                ("hModule", c.c_void_p),
                ("szModule", w.WCHAR * 256),
                ("szExePath", w.WCHAR * 260),
            ]

        first = self.kernel32.Module32FirstW
        next_module = self.kernel32.Module32NextW
        first.argtypes = [w.HANDLE, c.POINTER(MODULEENTRY32W)]
        first.restype = w.BOOL
        next_module.argtypes = [w.HANDLE, c.POINTER(MODULEENTRY32W)]
        next_module.restype = w.BOOL
        snapshot = self.kernel32.CreateToolhelp32Snapshot(
            self.TH32CS_SNAPMODULE | self.TH32CS_SNAPMODULE32, self.pid
        )
        invalid_handle = c.c_void_p(-1).value
        if not snapshot or int(snapshot) == invalid_handle:
            self._winerror("CreateToolhelp32Snapshot")
        try:
            entry = MODULEENTRY32W()
            entry.dwSize = c.sizeof(entry)
            if not first(snapshot, c.byref(entry)):
                self._winerror("Module32FirstW")
            modules: dict[str, tuple[int, str]] = {}
            while True:
                modules[str(entry.szModule).lower()] = (
                    int(entry.modBaseAddr or 0),
                    str(entry.szExePath),
                )
                if not next_module(snapshot, c.byref(entry)):
                    break
            return modules
        finally:
            self.kernel32.CloseHandle(snapshot)

    def _local_owner(self, address: int) -> tuple[int, str]:
        module = self.ctypes.c_void_p()
        flags = (
            self.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS
            | self.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT
        )
        if not self.kernel32.GetModuleHandleExW(
            flags, self.ctypes.c_void_p(address), self.ctypes.byref(module)
        ):
            self._winerror("GetModuleHandleExW")
        path_buffer = self.ctypes.create_unicode_buffer(32768)
        length = self.kernel32.GetModuleFileNameW(module, path_buffer, len(path_buffer))
        if not length:
            self._winerror("GetModuleFileNameW")
        return int(module.value or 0), path_buffer.value

    def _remote_equivalent(self, local_address: int) -> int:
        local_base, owner_path = self._local_owner(local_address)
        owner_name = os.path.basename(owner_path).lower()
        remote = self._module_entries().get(owner_name)
        if remote is None:
            raise InjectionError(f"目标进程未加载解析导入所需模块: {owner_name}")
        return remote[0] + (local_address - local_base)

    def _load_library_address(self) -> int:
        kernel32 = self.kernel32.GetModuleHandleW("kernel32.dll")
        if not kernel32:
            self._winerror("GetModuleHandleW(kernel32.dll)")
        symbol = self.ctypes.c_char_p(b"LoadLibraryW")
        local_address = self.kernel32.GetProcAddress(
            kernel32, self.ctypes.cast(symbol, self.ctypes.c_void_p)
        )
        if not local_address:
            self._winerror("GetProcAddress(LoadLibraryW)")
        return self._remote_equivalent(int(local_address))

    def ensure_module(self, module_name: str) -> int:
        existing = self._module_entries().get(os.path.basename(module_name).lower())
        if existing is not None:
            return existing[0]

        encoded = (module_name + "\0").encode("utf-16-le")
        remote_string = self.alloc(len(encoded))
        if not remote_string:
            self._winerror("VirtualAllocEx(import name)")
        try:
            self.write(remote_string, encoded)
            loaded = self.remote_call(self._load_library_address(), (remote_string,))
        finally:
            self.free(remote_string)
        if not loaded:
            raise InjectionError(f"目标进程 LoadLibraryW 失败: {module_name}")
        refreshed = self._module_entries().get(os.path.basename(module_name).lower())
        # API-set 名称会被加载器重定向，不一定作为同名模块出现在快照里；LoadLibraryW
        # 的返回值仍是目标进程里的有效模块句柄。后续实际函数地址按 owner 模块换算。
        return refreshed[0] if refreshed is not None else loaded

    def resolve_import(self, module: str, name: bytes | None, ordinal: int | None) -> int:
        self.ensure_module(module)
        local_module = self.kernel32.LoadLibraryW(module)
        if not local_module:
            self._winerror(f"LoadLibraryW({module})")
        self._local_modules.append(int(local_module))
        if name is not None:
            name_buffer = self.ctypes.c_char_p(name)
            symbol_pointer = self.ctypes.cast(name_buffer, self.ctypes.c_void_p)
        elif ordinal is not None:
            symbol_pointer = self.ctypes.c_void_p(ordinal)
        else:
            raise InjectionError(f"导入项缺少名称和序号: {module}")
        local_address = self.kernel32.GetProcAddress(local_module, symbol_pointer)
        if not local_address:
            symbol = name.decode("ascii", "replace") if name is not None else f"#{ordinal}"
            raise InjectionError(f"GetProcAddress 失败: {module}!{symbol}")
        return self._remote_equivalent(int(local_address))

    def target_api(self, module: str, symbol: bytes) -> int:
        return self.resolve_import(module, symbol, None)


def _require_windows() -> None:
    if sys.platform != "win32":
        raise OSError("手动映射装载器仅支持 Windows x64")


def _remote_tls_callbacks(process: _WindowsProcess, handle: MapHandle) -> tuple[int, ...]:
    """从仍在目标进程内的 PE 头恢复 TLS 回调，供对称卸载使用。"""
    e_lfanew = struct.unpack("<I", process.read(handle.base + 0x3C, 4))[0]
    _checked_range(e_lfanew, 24, handle.size, "远程 PE NT 头")
    if process.read(handle.base + e_lfanew, 4) != b"PE\0\0":
        raise InjectionError("远程映像的 PE 签名无效")

    optional_header = e_lfanew + 24
    magic = struct.unpack("<H", process.read(handle.base + optional_header, 2))[0]
    if magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC:
        raise InjectionError("远程映像不是 PE32+ x64")

    # PE32+ OptionalHeader 的数据目录从偏移 112 开始，每项 16 字节。
    tls_entry = optional_header + 112 + IMAGE_DIRECTORY_ENTRY_TLS * 16
    _checked_range(tls_entry, 16, handle.size, "远程 TLS 数据目录")
    tls_rva, tls_size = struct.unpack("<II", process.read(handle.base + tls_entry, 8))
    if tls_rva == 0 or tls_size == 0:
        return ()
    _checked_range(tls_rva, 32, handle.size, "远程 TLS 目录")
    callbacks_va = struct.unpack("<Q", process.read(handle.base + tls_rva + 24, 8))[0]
    if callbacks_va == 0:
        return ()
    callbacks_rva = callbacks_va - handle.base
    if callbacks_rva < 0:
        raise InjectionError("远程 TLS 回调表地址不在映像内")

    callbacks: list[int] = []
    for index in range(_MAX_TLS_CALLBACKS):
        offset = callbacks_rva + index * 8
        _checked_range(offset, 8, handle.size, "远程 TLS 回调表")
        callback = struct.unpack("<Q", process.read(handle.base + offset, 8))[0]
        if callback == 0:
            return tuple(callbacks)
        if not handle.base <= callback < handle.base + handle.size:
            raise InjectionError("远程 TLS 回调地址不在映像内")
        callbacks.append(callback)
    raise InjectionError("远程 TLS 回调表未终止")


def inject(pid: int, dll_path: str) -> MapHandle:
    """把 x64 DLL 手动映射进指定进程并返回可卸载句柄。"""
    _require_windows()
    if not isinstance(pid, int) or pid <= 0:
        raise ValueError("pid 必须是正整数")
    path = Path(dll_path)
    if not path.is_file():
        raise FileNotFoundError(path)
    raw = path.read_bytes()
    pe = _load_pe(raw)
    _validate_pe(pe)

    image_size = int(pe.OPTIONAL_HEADER.SizeOfImage)
    preferred_base = int(pe.OPTIONAL_HEADER.ImageBase)
    with _WindowsProcess(pid) as process:
        base = process.alloc(image_size, address=preferred_base)
        if not base:
            base = process.alloc(image_size)
        if not base:
            process._winerror("VirtualAllocEx(image)")

        exception_registered = False
        entry_attached = False
        attached_tls_callbacks: list[int] = []
        prepared: PreparedImage | None = None
        try:
            prepared = _prepare_image(pe, raw, base, process.resolve_import)
            process.write(base, prepared.data)
            process.protect(base, int(pe.OPTIONAL_HEADER.SizeOfHeaders), PAGE_READONLY)
            for section in prepared.sections:
                process.protect(section.address, section.size, section.protection)
            process.flush(base, image_size)

            if prepared.exception_table:
                add_function_table = process.target_api("ntdll.dll", b"RtlAddFunctionTable")
                result = process.remote_call(
                    add_function_table,
                    (prepared.exception_table, prepared.exception_count, base),
                )
                if not result:
                    raise InjectionError("RtlAddFunctionTable 返回失败")
                exception_registered = True

            for callback in prepared.tls_callbacks:
                process.remote_call(callback, (base, DLL_PROCESS_ATTACH, 0))
                attached_tls_callbacks.append(callback)

            if prepared.entry:
                if not process.remote_call(prepared.entry, (base, DLL_PROCESS_ATTACH, 0)):
                    raise InjectionError("DllMain(DLL_PROCESS_ATTACH) 返回失败")
                entry_attached = True

            return MapHandle(
                base=base,
                entry=prepared.entry,
                exception_table=prepared.exception_table,
                size=image_size,
            )
        except RemoteCallTimeout:
            # 目标线程仍可能位于 TLS/DllMain/ntdll 中。此时释放映像会造成确定性崩溃，
            # 因而宁可保留目标内存并把错误交给上层提示重启 QQ。
            raise
        except Exception:
            # 只回滚已确认完成的阶段；远程调用超时会由 remote_call 保留 trampoline。
            if entry_attached and prepared is not None and prepared.entry:
                with contextlib.suppress(Exception):
                    process.remote_call(prepared.entry, (base, DLL_PROCESS_DETACH, 0))
            for callback in reversed(attached_tls_callbacks):
                with contextlib.suppress(Exception):
                    process.remote_call(callback, (base, DLL_PROCESS_DETACH, 0))
            if exception_registered and prepared is not None:
                with contextlib.suppress(Exception):
                    delete_function_table = process.target_api(
                        "ntdll.dll", b"RtlDeleteFunctionTable"
                    )
                    process.remote_call(delete_function_table, (prepared.exception_table,))
            with contextlib.suppress(Exception):
                process.free(base)
            raise


def unload(pid: int, handle: MapHandle) -> None:
    """反向调用入口、注销 x64 异常表并释放手动映射映像。"""
    _require_windows()
    if not isinstance(pid, int) or pid <= 0:
        raise ValueError("pid 必须是正整数")
    if not isinstance(handle, MapHandle):
        raise TypeError(f"expected MapHandle, got {type(handle)!r}")
    if handle.base <= 0 or handle.size <= 0:
        raise ValueError("无效的手动映射句柄")

    with _WindowsProcess(pid) as process:
        tls_callbacks = _remote_tls_callbacks(process, handle)
        if handle.entry:
            process.remote_call(handle.entry, (handle.base, DLL_PROCESS_DETACH, 0))
        for callback in reversed(tls_callbacks):
            process.remote_call(callback, (handle.base, DLL_PROCESS_DETACH, 0))
        if handle.exception_table:
            delete_function_table = process.target_api("ntdll.dll", b"RtlDeleteFunctionTable")
            if not process.remote_call(delete_function_table, (handle.exception_table,)):
                raise InjectionError("RtlDeleteFunctionTable 返回失败；为安全起见未释放映像")
        process.free(handle.base)
