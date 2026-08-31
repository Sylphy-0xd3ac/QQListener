from typing import Protocol


class PipeTransport(Protocol):
    def read(self, n: int) -> bytes: ...
    def write(self, data: bytes) -> None: ...
    def close(self) -> None: ...


class Win32NamedPipeTransport:
    """Windows 命名管道客户端（同步阻塞读）。用于 recv 管道字节流。"""

    def __init__(self, pipe_name: str) -> None:
        import win32file  # 延迟导入：非 win32 环境不应加载

        self._win32file = win32file
        self._handle = win32file.CreateFile(
            pipe_name,
            win32file.GENERIC_READ | win32file.GENERIC_WRITE,
            0,
            None,
            win32file.OPEN_EXISTING,
            0,
            None,
        )

    def read(self, n: int) -> bytes:
        _, data = self._win32file.ReadFile(self._handle, n)
        return bytes(data)

    def write(self, data: bytes) -> None:
        self._win32file.WriteFile(self._handle, bytes(data))

    def close(self) -> None:
        if self._handle is not None:
            self._win32file.CloseHandle(self._handle)
            self._handle = None


class FakeTransport:
    """测试用：按预置 chunk 顺序返回字节，耗尽后返回 b''（模拟管道关闭）。"""

    def __init__(self, chunks: list[bytes]) -> None:
        self._chunks = list(chunks)
        self._closed = False
        self.writes: list[bytes] = []

    def read(self, n: int) -> bytes:
        if self._closed or not self._chunks:
            return b""
        return self._chunks.pop(0)

    def write(self, data: bytes) -> None:
        if self._closed:
            raise OSError("transport is closed")
        self.writes.append(bytes(data))

    def close(self) -> None:
        self._closed = True
