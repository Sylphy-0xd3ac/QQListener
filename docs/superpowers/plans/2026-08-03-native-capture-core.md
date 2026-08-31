# 原生捕获内核实现计划（Plan A：Phase 0 探针 + Phase 1 解码栈）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用 Python 实现「连 QQ 的 SnowLuma recv 命名管道 → 解 HQP1 帧 → 解 QQ wire protobuf → 产出结构化 `CapturedMessage`（含图片/文件 URL）」这条捕获链，并用一个探针脚本在真机上验证。

**Architecture:** 忠实复刻 SnowLuma 的管道协议与 protobuf 字段号。分层：`hqp1`（帧编解码，纯字节）→ `proto`（手写 protobuf wire reader + 元素/消息 schema）→ `sso_decode`（SSO 包 → Segment）→ `hook_client`（asyncio recv 会话，可注入假传输）→ `capture`（枚举 QQ + 编排）。注入本身本计划**不做**——沿用手动借 SnowLuma `.node` 把 `.dll` 塞进 QQ（Plan C 再用 Python 复刻 manual-map 注入器）。

**Tech Stack:** Python 3.10、asyncio、pywin32（Windows 命名管道）、psutil（进程枚举）、pytest（测试）。protobuf 解码**手写 wire reader**，不引入 protobuf/protoc（这是选择性只读解码，schema 已知，手写更轻更可测）。

## Global Constraints

以下为项目级约束，每个任务都隐含遵守，数值逐字来自 spec 与 `pyproject.toml`：

- Python 版本：`>=3.10,<3.11`。
- Ruff：`line-length = 100`（E501 已忽略）、`quote-style = "double"`、`target-version = "py310"`、isort `known-first-party = ["src"]`。每次提交前跑 `uv run ruff check --fix .` 与 `uv run ruff format .`。
- **绝不提交任何 SnowLuma 二进制**（`snowluma-*.dll` / `*.node` / `*.so`）——LICENSE §5 专有、不授权再分发。这些路径必须进 `.gitignore`。
- 所有 HQP1 帧整数**小端**；magic `0x31504851`；头固定 40 字节。
- 本版**只收不发**：不实现 control 管道的 `sendRequest`。
- 本版**无降级路径**（Plan B 才动 worker，本计划不碰旧引擎）。
- 平台：仅 Windows x64（命名管道用 `\\.\pipe\...`）。

## 精确协议参照（已从 SnowLuma 源码核实）

HQP1 帧头（40 字节，小端，来自 `qq-hook-client.ts` `encodeFrame`/`FrameReader`）：

| 偏移 | 类型 | 字段 |
| --- | --- | --- |
| 0 | u32 | magic `0x31504851` |
| 4 | u16 | version = 1 |
| 6 | u16 | op |
| 8 | u32 | requestId |
| 12 | i32 | status |
| 16 | u32 | flags |
| 20 | u32 | cmdLen |
| 24 | u32 | msgLen |
| 28 | u32 | bodyLen |
| 32 | u64 | value0 |
| 40 | bytes | cmd(cmdLen) + msg(msgLen) + body(bodyLen) |

op：`hello=1, sendRequest=2, sendAck=3, sendReply=4, error=5, recvPacket=6, loginState=7`。
flags：`WantReply=1<<0, LoggedIn=1<<2`。

**op=6 (recvPacket) → 逻辑包**（来自 `handleRecvFrame`）：
`seq = value0`、`error = status`、`cmd = cmd字段`、`uin = msg字段`、`body = body字段`。

**op=7 (loginState)**（来自 `applyLoginStateFrame`）：
`loggedIn = (flags & (1<<2)) != 0 或 status != 0`、`uinNumber = value0`、`uin = msg 或 str(value0)`。

**op=1 (hello)**（来自 `resolveHello`）：`pipeName = msg`、`pid = value0`。

管道名（Windows，来自 `mojoPipeName`）：`\\.\pipe\mojo.<pid>.recv` 与 `\\.\pipe\mojo.<pid>.control`。

> ⚠️ 真机待验证项（Phase 0 探针必须核对，后续 proto 任务以真帧为准）：
> ① 群消息推送的 `cmd` 字符串真实值；② 群图片实际承载元素（`NotOnlineImage` vs `CustomFace` vs `commonElem`）；③ `NotOnlineImage` 各字段号在当前 QQ 版本是否一致。

---

## 文件结构

- `src/native/__init__.py` — 空包标记。
- `src/native/hqp1.py` — 帧 dataclass、`encode_frame`、`FrameReader`、`packet_from_frame`。
- `src/native/proto/__init__.py` — 空包标记。
- `src/native/proto/wire.py` — 手写 protobuf wire reader：`decode_fields(bytes) -> dict[int, list[WireValue]]`。
- `src/native/proto/element.py` — 元素级 schema 访问（`NotOnlineImage`/`TextElem` 等字段号常量 + 提取函数）。
- `src/native/proto/message.py` — `PushMsg → RichText → Elem` 结构解析。
- `src/native/model.py` — `Segment`、`CapturedMessage` dataclass。
- `src/native/sso_decode.py` — `decode_message_push(packet) -> CapturedMessage | None`。
- `src/native/pipe_transport.py` — `PipeTransport` 抽象 + `Win32NamedPipeTransport` + 测试用 `FakeTransport`。
- `src/native/hook_client.py` — `RecvHookClient`：消费 transport，解帧，异步产出包与登录态。
- `src/native/binary_locator.py` — 定位用户放置的 `.dll`/`.node`（不入库）。
- `src/native/capture.py` — 编排：`enumerate_qq_pids()` + `RecvCapture`。
- `scripts/probe_recv.py` — Phase 0 探针脚本（真机，转储帧、存 fixture）。
- `tests/native/` — 全部单测；`tests/native/fixtures/` — 真帧 fixture。
- `tests/conftest.py` — pytest 根配置（`sys.path` 保证 `import src.*`）。

---

## Task 1: 开发工具链 + HQP1 帧编解码

**Files:**
- Modify: `pyproject.toml`（加 dev 依赖 pytest；把 pywin32、psutil 纳入运行依赖）
- Create: `tests/conftest.py`
- Create: `src/native/__init__.py`
- Create: `src/native/hqp1.py`
- Test: `tests/native/__init__.py`, `tests/native/test_hqp1.py`

**Interfaces:**
- Produces:
  - `PIPE_MAGIC = 0x31504851`, `PIPE_VERSION = 1`, `HEADER_SIZE = 40`
  - `class PipeOp(IntEnum)`: `hello=1, send_request=2, send_ack=3, send_reply=4, error=5, recv_packet=6, login_state=7`
  - `FLAG_WANT_REPLY = 1 << 0`, `FLAG_LOGGED_IN = 1 << 2`
  - `@dataclass class Frame: op:int; request_id:int; status:int; flags:int; value0:int; cmd:str; msg:str; body:bytes`
  - `encode_frame(op:int, *, request_id:int=0, status:int=0, flags:int=0, value0:int=0, cmd:str="", msg:str="", body:bytes=b"") -> bytes`
  - `class FrameReader: def push(self, chunk: bytes) -> list[Frame]`（流式，缓冲不足返回空）
  - `@dataclass class RecvPacket: seq:int; error:int; cmd:str; uin:str; body:bytes`
  - `packet_from_frame(frame: Frame) -> RecvPacket | None`（仅 op=6 返回，否则 None）

- [ ] **Step 1: 加依赖与 pytest 配置**

改 `pyproject.toml`：把 `psutil` 保持在 `dependencies`；将 `pywin32` 从可选提到运行依赖（带平台标记）。文件末尾追加 PEP 735 dev 组：

```toml
# 在 [project] dependencies 列表中确保存在（psutil 已在，补 pywin32）：
#     "pywin32>=311; sys_platform == 'win32'",

[dependency-groups]
dev = [
    "pytest>=8.0",
]
```

创建 `tests/conftest.py`：

```python
import sys
from pathlib import Path

# 保证 `import src.*` 可用（仓库根加入 sys.path）
ROOT = Path(__file__).resolve().parent.parent
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
```

创建空文件 `src/native/__init__.py` 与 `tests/native/__init__.py`。

- [ ] **Step 2: 写失败测试**

`tests/native/test_hqp1.py`：

```python
import struct

from src.native.hqp1 import (
    FLAG_LOGGED_IN,
    HEADER_SIZE,
    PIPE_MAGIC,
    PIPE_VERSION,
    Frame,
    FrameReader,
    PipeOp,
    RecvPacket,
    encode_frame,
    packet_from_frame,
)


def test_encode_frame_header_layout():
    data = encode_frame(PipeOp.recv_packet, status=3, flags=5, value0=42, cmd="ab", msg="c", body=b"xy")
    assert len(data) == HEADER_SIZE + 2 + 1 + 2
    assert struct.unpack_from("<I", data, 0)[0] == PIPE_MAGIC
    assert struct.unpack_from("<H", data, 4)[0] == PIPE_VERSION
    assert struct.unpack_from("<H", data, 6)[0] == int(PipeOp.recv_packet)
    assert struct.unpack_from("<i", data, 12)[0] == 3
    assert struct.unpack_from("<I", data, 16)[0] == 5
    assert struct.unpack_from("<I", data, 20)[0] == 2  # cmdLen
    assert struct.unpack_from("<I", data, 24)[0] == 1  # msgLen
    assert struct.unpack_from("<I", data, 28)[0] == 2  # bodyLen
    assert struct.unpack_from("<Q", data, 32)[0] == 42
    assert data[HEADER_SIZE:] == b"abcxy"


def test_frame_reader_roundtrip_and_split():
    frame_bytes = encode_frame(PipeOp.recv_packet, value0=7, cmd="cmd", msg="123", body=b"\x01\x02")
    reader = FrameReader()
    # 分两半喂入，验证跨块缓冲
    assert reader.push(frame_bytes[:10]) == []
    frames = reader.push(frame_bytes[10:])
    assert len(frames) == 1
    f = frames[0]
    assert f.op == int(PipeOp.recv_packet)
    assert f.value0 == 7 and f.cmd == "cmd" and f.msg == "123" and f.body == b"\x01\x02"


def test_frame_reader_bad_magic_raises():
    reader = FrameReader()
    bad = bytes(HEADER_SIZE)  # 全 0，magic 错
    try:
        reader.push(bad)
    except ValueError as exc:
        assert "magic" in str(exc)
    else:
        raise AssertionError("expected ValueError")


def test_packet_from_frame_maps_op6():
    f = Frame(op=int(PipeOp.recv_packet), request_id=0, status=-1, flags=0, value0=99, cmd="svc", msg="10001", body=b"pb")
    pkt = packet_from_frame(f)
    assert pkt == RecvPacket(seq=99, error=-1, cmd="svc", uin="10001", body=b"pb")


def test_packet_from_frame_ignores_non_recv():
    f = Frame(op=int(PipeOp.login_state), request_id=0, status=0, flags=FLAG_LOGGED_IN, value0=1, cmd="", msg="", body=b"")
    assert packet_from_frame(f) is None
```

- [ ] **Step 3: 跑测试确认失败**

Run: `uv run pytest tests/native/test_hqp1.py -v`
Expected: FAIL（`ModuleNotFoundError: src.native.hqp1`）

- [ ] **Step 4: 实现 `src/native/hqp1.py`**

```python
import struct
from dataclasses import dataclass
from enum import IntEnum

PIPE_MAGIC = 0x31504851
PIPE_VERSION = 1
HEADER_SIZE = 40

FLAG_WANT_REPLY = 1 << 0
FLAG_LOGGED_IN = 1 << 2


class PipeOp(IntEnum):
    hello = 1
    send_request = 2
    send_ack = 3
    send_reply = 4
    error = 5
    recv_packet = 6
    login_state = 7


@dataclass
class Frame:
    op: int
    request_id: int
    status: int
    flags: int
    value0: int
    cmd: str
    msg: str
    body: bytes


@dataclass
class RecvPacket:
    seq: int
    error: int
    cmd: str
    uin: str
    body: bytes


def encode_frame(
    op: int,
    *,
    request_id: int = 0,
    status: int = 0,
    flags: int = 0,
    value0: int = 0,
    cmd: str = "",
    msg: str = "",
    body: bytes = b"",
) -> bytes:
    cmd_b = cmd.encode("utf-8")
    msg_b = msg.encode("utf-8")
    header = struct.pack(
        "<IHHIiIIIIQ",
        PIPE_MAGIC,
        PIPE_VERSION,
        int(op),
        request_id & 0xFFFFFFFF,
        status,
        flags & 0xFFFFFFFF,
        len(cmd_b),
        len(msg_b),
        len(body),
        value0 & 0xFFFFFFFFFFFFFFFF,
    )
    return header + cmd_b + msg_b + bytes(body)


class FrameReader:
    def __init__(self) -> None:
        self._buf = bytearray()

    def push(self, chunk: bytes) -> list[Frame]:
        self._buf.extend(chunk)
        frames: list[Frame] = []
        while len(self._buf) >= HEADER_SIZE:
            magic, version = struct.unpack_from("<IH", self._buf, 0)
            if magic != PIPE_MAGIC or version != PIPE_VERSION:
                raise ValueError(f"bad frame header magic=0x{magic:x} version={version}")
            op, request_id, status, flags = struct.unpack_from("<HIiI", self._buf, 6)
            cmd_len, msg_len, body_len = struct.unpack_from("<III", self._buf, 20)
            (value0,) = struct.unpack_from("<Q", self._buf, 32)
            total = HEADER_SIZE + cmd_len + msg_len + body_len
            if len(self._buf) < total:
                break
            off = HEADER_SIZE
            cmd = self._buf[off : off + cmd_len].decode("utf-8", "replace")
            off += cmd_len
            msg = self._buf[off : off + msg_len].decode("utf-8", "replace")
            off += msg_len
            body = bytes(self._buf[off : off + body_len])
            del self._buf[:total]
            frames.append(
                Frame(op=op, request_id=request_id, status=status, flags=flags, value0=value0, cmd=cmd, msg=msg, body=body)
            )
        return frames


def packet_from_frame(frame: Frame) -> RecvPacket | None:
    if frame.op != int(PipeOp.recv_packet):
        return None
    return RecvPacket(seq=frame.value0, error=frame.status, cmd=frame.cmd, uin=frame.msg, body=frame.body)
```

- [ ] **Step 5: 跑测试确认通过**

Run: `uv run pytest tests/native/test_hqp1.py -v`
Expected: PASS（5 passed）

- [ ] **Step 6: lint + 提交**

```bash
uv run ruff check --fix . && uv run ruff format .
git add pyproject.toml tests/conftest.py tests/native/__init__.py tests/native/test_hqp1.py src/native/__init__.py src/native/hqp1.py
git commit -m "feat(native): HQP1 frame codec and recv-packet mapping"
```

---

## Task 2: Phase 0 探针脚本（真机 spike，转储帧、存 fixture）

**Files:**
- Create: `src/native/pipe_transport.py`（本任务只需 `Win32NamedPipeTransport` 的同步读；`FakeTransport` 在 Task 6 补）
- Create: `scripts/probe_recv.py`
- Modify: `.gitignore`（忽略 `*.node`、`*.dll`、`*.so`、`tests/native/fixtures/*.bin`）

**Interfaces:**
- Consumes: `FrameReader`, `packet_from_frame`, `PipeOp`（Task 1）
- Produces:
  - `class Win32NamedPipeTransport: def __init__(self, pipe_name: str); def read(self, n: int) -> bytes; def close(self) -> None`
  - 探针脚本命令行：`python scripts/probe_recv.py <pid> [--dump-dir tests/native/fixtures]`

> 本任务是**探针/spike**，不是 TDD 单元——它需要真机、真 QQ、已由 SnowLuma `.node` 手动注入的 `.dll`。它的产物是「一条真实群消息被打印出来」+ 存下的 `.bin` fixture，供后续 proto 任务做 golden test。**验收靠人工观察，不靠断言。**

- [ ] **Step 1: 实现 Win32 命名管道同步传输**

`src/native/pipe_transport.py`：

```python
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

    def close(self) -> None:
        if self._handle is not None:
            self._win32file.CloseHandle(self._handle)
            self._handle = None
```

- [ ] **Step 2: 写探针脚本**

`scripts/probe_recv.py`：

```python
"""Phase 0 探针：连 QQ 的 SnowLuma recv 命名管道，转储帧。

前置：QQ 已运行，且已用 SnowLuma 的 .node 注入器把 .dll 塞进该 QQ 进程
（本脚本不做注入）。用法：python scripts/probe_recv.py <pid>
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from src.native.hqp1 import FrameReader, PipeOp, packet_from_frame  # noqa: E402
from src.native.pipe_transport import Win32NamedPipeTransport  # noqa: E402


def main() -> None:
    if len(sys.argv) < 2:
        print("用法: python scripts/probe_recv.py <pid> [--dump-dir DIR]")
        raise SystemExit(2)
    pid = int(sys.argv[1])
    dump_dir = None
    if "--dump-dir" in sys.argv:
        dump_dir = Path(sys.argv[sys.argv.index("--dump-dir") + 1])
        dump_dir.mkdir(parents=True, exist_ok=True)

    pipe_name = rf"\\.\pipe\mojo.{pid}.recv"
    print(f"连接: {pipe_name}")
    transport = Win32NamedPipeTransport(pipe_name)
    reader = FrameReader()
    count = 0
    try:
        while True:
            chunk = transport.read(65536)
            if not chunk:
                print("管道关闭")
                break
            for frame in reader.push(chunk):
                op_name = PipeOp(frame.op).name if frame.op in PipeOp._value2member_map_ else str(frame.op)
                print(f"[{op_name}] status={frame.status} flags={frame.flags} value0={frame.value0} cmd={frame.cmd!r} msg={frame.msg!r} bodyLen={len(frame.body)}")
                pkt = packet_from_frame(frame)
                if pkt and dump_dir is not None:
                    out = dump_dir / f"recv_{count:04d}_{pkt.cmd.replace('.', '_')[:40]}.bin"
                    out.write_bytes(pkt.body)
                    print(f"  已存: {out}")
                    count += 1
    finally:
        transport.close()


if __name__ == "__main__":
    main()
```

- [ ] **Step 3: 更新 `.gitignore`**

在 `.gitignore` 追加：

```gitignore
# SnowLuma 专有二进制，绝不入库（LICENSE §5）
*.node
*.dll
*.so
# 真帧 fixture（含账号数据），本地留存不入库
tests/native/fixtures/*.bin
```

- [ ] **Step 4: 真机运行探针（人工验收）**

在 Windows 上：启动 QQ 并登录 → 用 SnowLuma 的 `.node` 注入器把 `snowluma-win32-x64.dll` 注入该 QQ（借用其 `loadModuleManual`，本计划不复刻）→ `uv run python scripts/probe_recv.py <pid> --dump-dir tests/native/fixtures`。
在群里发一条文本 + 一张图片。**验收：**
- 观察到 `[login_state]` 帧，`value0` = 你的 QQ 号。
- 观察到 `[recv_packet]` 帧，记录**群消息的真实 `cmd` 字符串**（回填到 spec 待验证项①）。
- `tests/native/fixtures/` 下出现 `.bin`。挑出「文本消息」和「图片消息」两个 body，另存为**不含隐私、可入库**的最小样本 `tests/native/fixtures/group_text.bin`、`tests/native/fixtures/group_image.bin`（从 `.gitignore` 的忽略里显式豁免这两个：追加 `!tests/native/fixtures/group_text.bin` 等）。

- [ ] **Step 5: 提交（脚本 + 传输 + gitignore + 两个脱敏 fixture）**

```bash
git add scripts/probe_recv.py src/native/pipe_transport.py .gitignore tests/native/fixtures/group_text.bin tests/native/fixtures/group_image.bin
git commit -m "feat(native): Phase 0 recv-pipe probe script and sanitized fixtures"
```

> 若 Step 4 揭示 `cmd`/元素承载与 spec 假设不符，**在此处更新 spec 的待验证项**，后续 Task 3–5 以真帧为准。

---

## Task 3: protobuf wire reader（手写，纯字节）

**Files:**
- Create: `src/native/proto/__init__.py`
- Create: `src/native/proto/wire.py`
- Test: `tests/native/test_wire.py`

**Interfaces:**
- Produces:
  - `WIRE_VARINT=0, WIRE_I64=1, WIRE_LEN=2, WIRE_I32=5`
  - `@dataclass class WireValue: wire_type:int; raw:bytes`（`raw` 是该字段原始负载：varint 存其 int 的最小小端；LEN 存内容字节；I32/I64 存定长字节）
  - `decode_fields(data: bytes) -> dict[int, list[WireValue]]`（同号字段聚合成列表，保序）
  - `as_int(v: WireValue) -> int`、`as_str(v: WireValue) -> str`、`as_bytes(v: WireValue) -> bytes`
  - `read_varint(data: bytes, pos: int) -> tuple[int, int]`（返回值 + 新 pos）

- [ ] **Step 1: 写失败测试**

`tests/native/test_wire.py`：

```python
from src.native.proto.wire import (
    WIRE_LEN,
    WIRE_VARINT,
    as_bytes,
    as_int,
    as_str,
    decode_fields,
    read_varint,
)


def test_read_varint_multibyte():
    # 300 = 0xAC 0x02
    val, pos = read_varint(b"\xac\x02", 0)
    assert val == 300 and pos == 2


def test_decode_field_varint():
    # field 2 varint 300: tag=(2<<3)|0=0x10
    fields = decode_fields(b"\x10\xac\x02")
    assert as_int(fields[2][0]) == 300
    assert fields[2][0].wire_type == WIRE_VARINT


def test_decode_field_string():
    # field 1 LEN "hi": tag=0x0A len=2
    fields = decode_fields(b"\x0a\x02hi")
    assert fields[1][0].wire_type == WIRE_LEN
    assert as_str(fields[1][0]) == "hi"
    assert as_bytes(fields[1][0]) == b"hi"


def test_decode_field_15_string():
    # field 15 LEN "u": tag=(15<<3)|2=0x7a len=1
    fields = decode_fields(b"\x7a\x01u")
    assert as_str(fields[15][0]) == "u"


def test_repeated_fields_aggregate_in_order():
    # field 1 varint 1, then field 1 varint 2
    fields = decode_fields(b"\x08\x01\x08\x02")
    assert [as_int(v) for v in fields[1]] == [1, 2]


def test_nested_message_via_as_bytes_then_decode():
    # field 3 LEN wraps {field 1 LEN "x"}
    inner = b"\x0a\x01x"
    outer = bytes([(3 << 3) | 2, len(inner)]) + inner
    fields = decode_fields(outer)
    sub = decode_fields(as_bytes(fields[3][0]))
    assert as_str(sub[1][0]) == "x"
```

- [ ] **Step 2: 跑测试确认失败**

Run: `uv run pytest tests/native/test_wire.py -v`
Expected: FAIL（`ModuleNotFoundError`）

- [ ] **Step 3: 实现 `src/native/proto/wire.py`**

```python
from dataclasses import dataclass

WIRE_VARINT = 0
WIRE_I64 = 1
WIRE_LEN = 2
WIRE_I32 = 5


@dataclass
class WireValue:
    wire_type: int
    raw: bytes


def read_varint(data: bytes, pos: int) -> tuple[int, int]:
    result = 0
    shift = 0
    while True:
        if pos >= len(data):
            raise ValueError("truncated varint")
        byte = data[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if not (byte & 0x80):
            return result, pos
        shift += 7


def decode_fields(data: bytes) -> dict[int, list[WireValue]]:
    fields: dict[int, list[WireValue]] = {}
    pos = 0
    n = len(data)
    while pos < n:
        tag, pos = read_varint(data, pos)
        field_no = tag >> 3
        wire_type = tag & 0x07
        if wire_type == WIRE_VARINT:
            value, pos = read_varint(data, pos)
            raw = value.to_bytes((value.bit_length() + 7) // 8 or 1, "little")
        elif wire_type == WIRE_I64:
            raw = data[pos : pos + 8]
            pos += 8
        elif wire_type == WIRE_LEN:
            length, pos = read_varint(data, pos)
            raw = data[pos : pos + length]
            pos += length
        elif wire_type == WIRE_I32:
            raw = data[pos : pos + 4]
            pos += 4
        else:
            raise ValueError(f"unsupported wire_type={wire_type} field={field_no}")
        fields.setdefault(field_no, []).append(WireValue(wire_type, raw))
    return fields


def as_int(v: WireValue) -> int:
    return int.from_bytes(v.raw, "little")


def as_str(v: WireValue) -> str:
    return v.raw.decode("utf-8", "replace")


def as_bytes(v: WireValue) -> bytes:
    return v.raw
```

- [ ] **Step 4: 跑测试确认通过**

Run: `uv run pytest tests/native/test_wire.py -v`
Expected: PASS（6 passed）

- [ ] **Step 5: lint + 提交**

```bash
uv run ruff check --fix . && uv run ruff format .
git add src/native/proto/__init__.py src/native/proto/wire.py tests/native/test_wire.py
git commit -m "feat(native): hand-written protobuf wire reader"
```

---

## Task 4: 元素级 schema（NotOnlineImage / TextElem 等）

**Files:**
- Create: `src/native/proto/element.py`
- Test: `tests/native/test_element.py`

**Interfaces:**
- Consumes: `decode_fields`, `as_int`, `as_str`, `as_bytes`（Task 3）
- Produces（字段号照抄 `proto-defs/src/element.ts`；`Elem` 字段号照抄 `message.ts`，见 Task 5 参照）：
  - `@dataclass class ImageElem: orig_url:str; big_url:str; thumb_url:str; md5:str; file_size:int; name:str`
  - `parse_not_online_image(body: bytes) -> ImageElem`（字段：15=origUrl,14=bigUrl,12=thumbUrl,7=picMd5(bytes→hex),2=fileLen,1=filePath 作 name 回退）
  - `parse_text_elem(body: bytes) -> str`（字段 1=str）
  - `@dataclass class FileElem: name:str; url:str; md5:str; size:int` 与 `parse_trans_elem(body: bytes) -> FileElem`（占位：字段号 Phase 0 待核，先按 name=常见文件名字段实现并在测试中用合成字节）

> ⚠️ `ImageElem.orig_url` 等字段号来自 SnowLuma 源码；若 Task 2 探针显示群图走别的元素，在此调整并用真 fixture 补测。

- [ ] **Step 1: 写失败测试**

`tests/native/test_element.py`：

```python
from src.native.proto.element import ImageElem, parse_not_online_image, parse_text_elem


def _len_field(field_no: int, payload: bytes) -> bytes:
    tag = (field_no << 3) | 2
    return bytes([tag, len(payload)]) + payload


def _varint_field(field_no: int, value: int) -> bytes:
    out = bytearray([(field_no << 3) | 0])
    while True:
        b = value & 0x7F
        value >>= 7
        out.append(b | (0x80 if value else 0))
        if not value:
            return bytes(out)


def test_parse_text_elem():
    body = _len_field(1, "明天带这个".encode("utf-8"))
    assert parse_text_elem(body) == "明天带这个"


def test_parse_not_online_image_extracts_urls():
    body = (
        _len_field(15, b"http://orig/u")
        + _len_field(12, b"http://thumb/u")
        + _len_field(7, b"\xde\xad\xbe\xef")
        + _varint_field(2, 2048)
    )
    img = parse_not_online_image(body)
    assert isinstance(img, ImageElem)
    assert img.orig_url == "http://orig/u"
    assert img.thumb_url == "http://thumb/u"
    assert img.md5 == "deadbeef"
    assert img.file_size == 2048


def test_parse_not_online_image_missing_fields_default_empty():
    img = parse_not_online_image(b"")
    assert img.orig_url == "" and img.thumb_url == "" and img.md5 == "" and img.file_size == 0
```

- [ ] **Step 2: 跑测试确认失败**

Run: `uv run pytest tests/native/test_element.py -v`
Expected: FAIL

- [ ] **Step 3: 实现 `src/native/proto/element.py`**

```python
from dataclasses import dataclass

from src.native.proto.wire import as_bytes, as_int, as_str, decode_fields


@dataclass
class ImageElem:
    orig_url: str = ""
    big_url: str = ""
    thumb_url: str = ""
    md5: str = ""
    file_size: int = 0
    name: str = ""


@dataclass
class FileElem:
    name: str = ""
    url: str = ""
    md5: str = ""
    size: int = 0


def _first_str(fields: dict, field_no: int) -> str:
    vals = fields.get(field_no)
    return as_str(vals[0]) if vals else ""


def _first_int(fields: dict, field_no: int) -> int:
    vals = fields.get(field_no)
    return as_int(vals[0]) if vals else 0


def _first_hex(fields: dict, field_no: int) -> str:
    vals = fields.get(field_no)
    return as_bytes(vals[0]).hex() if vals else ""


def parse_text_elem(body: bytes) -> str:
    return _first_str(decode_fields(body), 1)


def parse_not_online_image(body: bytes) -> ImageElem:
    f = decode_fields(body)
    return ImageElem(
        orig_url=_first_str(f, 15),
        big_url=_first_str(f, 14),
        thumb_url=_first_str(f, 12),
        md5=_first_hex(f, 7),
        file_size=_first_int(f, 2),
        name=_first_str(f, 1),
    )
```

- [ ] **Step 4: 跑测试确认通过**

Run: `uv run pytest tests/native/test_element.py -v`
Expected: PASS

- [ ] **Step 5: lint + 提交**

```bash
uv run ruff check --fix . && uv run ruff format .
git add src/native/proto/element.py tests/native/test_element.py
git commit -m "feat(native): element decoders (image/text)"
```

---

## Task 5: 数据模型 + 消息结构解析（PushMsg → RichText → Elem）

**Files:**
- Create: `src/native/model.py`
- Create: `src/native/proto/message.py`
- Test: `tests/native/test_model.py`, `tests/native/test_message.py`

**Interfaces:**
- Consumes: `decode_fields`, `as_str`, `as_bytes`, `as_int`（Task 3）；`parse_not_online_image`, `parse_text_elem`, `ImageElem`（Task 4）
- Produces:
  - `model.py`:
    - `@dataclass class Segment: type:str; text:str=""; url:str=""; name:str=""; md5:str=""; target_id:str=""; extra:dict=field(default_factory=dict)`
    - `@dataclass class CapturedMessage: scene:str; peer_id:str; peer_name:str; sender_id:str; sender_name:str; segments:list[Segment]; raw_seq:int`
    - `def message_text(msg: CapturedMessage) -> str`（把 segments 渲染成人读文本，图片→`[图片]`、文件→`[文件] name`、at→`[@name]`，供过滤/去重用）
  - `message.py`:
    - `ELEM_TEXT=1, ELEM_NOT_ONLINE_IMAGE=2`（按 `message.ts` Elem 字段号；**Phase 0 待核**，先按 SnowLuma 值实现）
    - `def parse_elems(rich_text_body: bytes) -> list[Segment]`（遍历 RichText.elems，每个 Elem 里按存在的子字段号分派到对应 parser）

> ⚠️ Elem 各子类型字段号、RichText/PushMsg 外层字段号来自 SnowLuma `message.ts`。真实结构以 Task 2 fixture 为准；本任务先用**合成字节**通过 TDD，Task 7 再用真 fixture 加 golden test。

- [ ] **Step 1: 写失败测试（model）**

`tests/native/test_model.py`：

```python
from src.native.model import CapturedMessage, Segment, message_text


def test_message_text_renders_segments():
    msg = CapturedMessage(
        scene="group",
        peer_id="123",
        peer_name="高三2班",
        sender_id="1001",
        sender_name="张三",
        segments=[
            Segment(type="text", text="明天带这个"),
            Segment(type="image", url="http://x"),
            Segment(type="file", name="作业.docx"),
            Segment(type="at", text="李四", target_id="1002"),
        ],
        raw_seq=7,
    )
    assert message_text(msg) == "明天带这个[图片][文件] 作业.docx[@李四]"
```

- [ ] **Step 2: 写失败测试（message）**

`tests/native/test_message.py`：

```python
from src.native.proto.message import ELEM_NOT_ONLINE_IMAGE, ELEM_TEXT, parse_elems


def _len_field(field_no: int, payload: bytes) -> bytes:
    return bytes([(field_no << 3) | 2, len(payload)]) + payload


def test_parse_elems_text_then_image():
    text_elem = _len_field(ELEM_TEXT, _len_field(1, "hi".encode()))
    img_inner = _len_field(15, b"http://orig")
    image_elem = _len_field(ELEM_NOT_ONLINE_IMAGE, img_inner)
    # RichText.elems: 假设 elem 列表字段号为 2（message.ts 待核；测试与实现一致即可）
    rich = _len_field(2, text_elem) + _len_field(2, image_elem)
    segs = parse_elems(rich)
    assert [s.type for s in segs] == ["text", "image"]
    assert segs[0].text == "hi"
    assert segs[1].url == "http://orig"
```

- [ ] **Step 3: 跑测试确认失败**

Run: `uv run pytest tests/native/test_model.py tests/native/test_message.py -v`
Expected: FAIL

- [ ] **Step 4: 实现 `src/native/model.py`**

```python
from dataclasses import dataclass, field


@dataclass
class Segment:
    type: str
    text: str = ""
    url: str = ""
    name: str = ""
    md5: str = ""
    target_id: str = ""
    extra: dict = field(default_factory=dict)


@dataclass
class CapturedMessage:
    scene: str
    peer_id: str
    peer_name: str
    sender_id: str
    sender_name: str
    segments: list[Segment]
    raw_seq: int


def message_text(msg: "CapturedMessage") -> str:
    parts: list[str] = []
    for seg in msg.segments:
        if seg.type == "text":
            parts.append(seg.text)
        elif seg.type == "at":
            parts.append(f"[@{seg.text}]" if seg.text else "[@]")
        elif seg.type == "image":
            parts.append("[图片]")
        elif seg.type == "record":
            parts.append("[语音]")
        elif seg.type == "video":
            parts.append("[视频]")
        elif seg.type == "file":
            parts.append(f"[文件] {seg.name}".rstrip())
        elif seg.type == "reply":
            parts.append("[回复]")
        else:
            parts.append(f"[{seg.type}]")
    return "".join(parts)
```

- [ ] **Step 5: 实现 `src/native/proto/message.py`**

```python
from src.native.model import Segment
from src.native.proto.element import parse_not_online_image, parse_text_elem
from src.native.proto.wire import as_bytes, decode_fields

# Elem 子字段号（照抄 SnowLuma message.ts；Phase 0 真帧待核）
ELEM_TEXT = 1
ELEM_NOT_ONLINE_IMAGE = 2

# RichText.elems 的字段号（message.ts 待核；须与解析一致）
RICHTEXT_ELEMS = 2


def _elem_to_segment(elem_body: bytes) -> Segment | None:
    f = decode_fields(elem_body)
    if ELEM_TEXT in f:
        return Segment(type="text", text=parse_text_elem(as_bytes(f[ELEM_TEXT][0])))
    if ELEM_NOT_ONLINE_IMAGE in f:
        img = parse_not_online_image(as_bytes(f[ELEM_NOT_ONLINE_IMAGE][0]))
        return Segment(type="image", url=img.orig_url or img.big_url or img.thumb_url, md5=img.md5, name=img.name)
    return None


def parse_elems(rich_text_body: bytes) -> list[Segment]:
    fields = decode_fields(rich_text_body)
    segments: list[Segment] = []
    for elem in fields.get(RICHTEXT_ELEMS, []):
        seg = _elem_to_segment(as_bytes(elem))
        if seg is not None:
            segments.append(seg)
    return segments
```

- [ ] **Step 6: 跑测试确认通过**

Run: `uv run pytest tests/native/test_model.py tests/native/test_message.py -v`
Expected: PASS

- [ ] **Step 7: lint + 提交**

```bash
uv run ruff check --fix . && uv run ruff format .
git add src/native/model.py src/native/proto/message.py tests/native/test_model.py tests/native/test_message.py
git commit -m "feat(native): message model and RichText elem parsing"
```

---

## Task 6: SSO 包 → CapturedMessage

**Files:**
- Create: `src/native/sso_decode.py`
- Test: `tests/native/test_sso_decode.py`

**Interfaces:**
- Consumes: `RecvPacket`（Task 1）；`parse_elems`（Task 5）；`Segment`, `CapturedMessage`（Task 5）
- Produces:
  - `MESSAGE_PUSH_CMDS: set[str]`（Phase 0 探针回填的真实群消息 cmd；先放 SnowLuma 常见值 `"trpc.msg.olpush.OlPushService.MsgPush"`）
  - `decode_message_push(packet: RecvPacket) -> CapturedMessage | None`（cmd 不在集合内返回 None；解外层 PushMsg 取 scene/peer/sender/seq，再调 `parse_elems`）

> ⚠️ PushMsg 外层字段号（群号、发送者、RichText 位置）来自 `message.ts`，**必须用 Task 2 真 fixture 校准**。本任务先用合成字节 TDD，Step 5 用 `group_text.bin` 做 golden。

- [ ] **Step 1: 写失败测试**

`tests/native/test_sso_decode.py`：

```python
from src.native.hqp1 import RecvPacket
from src.native.sso_decode import MESSAGE_PUSH_CMDS, decode_message_push


def test_non_message_cmd_returns_none():
    pkt = RecvPacket(seq=1, error=0, cmd="some.other.Cmd", uin="10001", body=b"")
    assert decode_message_push(pkt) is None


def test_message_push_cmd_is_recognized():
    assert "trpc.msg.olpush.OlPushService.MsgPush" in MESSAGE_PUSH_CMDS
```

（结构化断言在 Step 5 用真 fixture 补，因为外层字段号需真帧校准。）

- [ ] **Step 2: 跑测试确认失败**

Run: `uv run pytest tests/native/test_sso_decode.py -v`
Expected: FAIL

- [ ] **Step 3: 实现 `src/native/sso_decode.py`（外层解析用占位字段号，标注待核）**

```python
from src.native.hqp1 import RecvPacket
from src.native.model import CapturedMessage
from src.native.proto.message import parse_elems
from src.native.proto.wire import as_bytes, as_int, as_str, decode_fields

MESSAGE_PUSH_CMDS: set[str] = {
    "trpc.msg.olpush.OlPushService.MsgPush",
}

# —— PushMsg 外层字段号：占位，Phase 0 真帧校准 ——
_F_MSG = 1  # PushMsg.message
_F_ROUTING = 2  # message.routingHead（群号/好友号）
_F_CONTENT = 3  # message.contentHead（seq 等）
_F_BODY = 4  # message.messageBody
_F_RICHTEXT = 1  # messageBody.richText
_F_GROUP = 1  # routingHead.group -> groupCode
_F_SENDER_UIN = 1  # 发送者 uin


def decode_message_push(packet: RecvPacket) -> CapturedMessage | None:
    if packet.cmd not in MESSAGE_PUSH_CMDS:
        return None
    top = decode_fields(packet.body)
    msg_f = top.get(_F_MSG)
    if not msg_f:
        return None
    message = decode_fields(as_bytes(msg_f[0]))
    body_f = message.get(_F_BODY)
    if not body_f:
        return None
    body = decode_fields(as_bytes(body_f[0]))
    rich_f = body.get(_F_RICHTEXT)
    segments = parse_elems(as_bytes(rich_f[0])) if rich_f else []

    # peer / sender（占位路径；真帧校准后替换字段号）
    peer_id = ""
    sender_id = packet.uin
    routing_f = message.get(_F_ROUTING)
    if routing_f:
        routing = decode_fields(as_bytes(routing_f[0]))
        grp = routing.get(_F_GROUP)
        if grp:
            peer_id = str(as_int(grp[0]))

    raw_seq = 0
    content_f = message.get(_F_CONTENT)
    if content_f:
        content = decode_fields(as_bytes(content_f[0]))
        seq_f = content.get(_F_SENDER_UIN)
        if seq_f:
            raw_seq = as_int(seq_f[0])

    return CapturedMessage(
        scene="group" if peer_id else "c2c",
        peer_id=peer_id,
        peer_name="",
        sender_id=sender_id,
        sender_name="",
        segments=segments,
        raw_seq=raw_seq or packet.seq,
    )
```

- [ ] **Step 4: 跑测试确认通过**

Run: `uv run pytest tests/native/test_sso_decode.py -v`
Expected: PASS（2 passed）

- [ ] **Step 5: 用真 fixture 校准外层字段号（真机后回填）**

用 Task 2 存下的 `tests/native/fixtures/group_text.bin` 写 golden test：

```python
from pathlib import Path

from src.native.hqp1 import RecvPacket
from src.native.sso_decode import decode_message_push

FIX = Path(__file__).parent / "fixtures"


def test_group_text_fixture_decodes_to_text_segment():
    body = (FIX / "group_text.bin").read_bytes()
    # cmd 用 Task 2 探针记录的真实值
    pkt = RecvPacket(seq=1, error=0, cmd="trpc.msg.olpush.OlPushService.MsgPush", uin="10001", body=body)
    msg = decode_message_push(pkt)
    assert msg is not None
    assert msg.scene in ("group", "c2c")
    assert any(s.type == "text" and s.text for s in msg.segments)
```

跑 `uv run pytest tests/native/test_sso_decode.py -v`。**若失败**：用 `python -c "from src.native.proto.wire import decode_fields; ..."` 交互式剥 fixture 的层级，把 `_F_*` 占位字段号改成真值，直到文本段解出。图片 fixture 同法加 `test_group_image_fixture_extracts_url`（断言存在 `type=="image"` 且 `url` 非空）。

- [ ] **Step 6: lint + 提交**

```bash
uv run ruff check --fix . && uv run ruff format .
git add src/native/sso_decode.py tests/native/test_sso_decode.py
git commit -m "feat(native): decode message-push SSO packet to CapturedMessage"
```

---

## Task 7: 异步 recv 会话客户端（可注入假传输）

**Files:**
- Modify: `src/native/pipe_transport.py`（补 `PipeTransport` 协议 + `FakeTransport`）
- Create: `src/native/hook_client.py`
- Test: `tests/native/test_hook_client.py`

**Interfaces:**
- Consumes: `FrameReader`, `packet_from_frame`, `PipeOp`, `FLAG_LOGGED_IN`, `Frame`（Task 1）；`Win32NamedPipeTransport`（Task 2）
- Produces:
  - `pipe_transport.py`: `class PipeTransport(Protocol): def read(self, n:int)->bytes; def close(self)->None`；`class FakeTransport`（用预置 chunk 列表喂字节，耗尽后 `read` 返回 `b""`）
  - `hook_client.py`:
    - `@dataclass class LoginState: logged_in:bool; uin:str`
    - `class RecvHookClient:`
      - `def __init__(self, transport: PipeTransport)`
      - `async def run(self, on_packet: Callable[[RecvPacket], None], on_login: Callable[[LoginState], None] | None = None) -> None`（后台线程阻塞读 → `asyncio.Queue` → 解帧分派；`b""` 或取消时干净退出）
      - `def stop(self) -> None`

- [ ] **Step 1: 写失败测试**

`tests/native/test_hook_client.py`：

```python
import asyncio

from src.native.hqp1 import FLAG_LOGGED_IN, PipeOp, encode_frame
from src.native.hook_client import LoginState, RecvHookClient
from src.native.pipe_transport import FakeTransport


def test_recv_client_dispatches_packet_and_login():
    login_frame = encode_frame(PipeOp.login_state, flags=FLAG_LOGGED_IN, value0=10001, msg="10001")
    pkt_frame = encode_frame(PipeOp.recv_packet, status=0, value0=5, cmd="svc", msg="10001", body=b"pb")
    transport = FakeTransport([login_frame + pkt_frame])

    packets = []
    logins = []
    client = RecvHookClient(transport)

    async def go():
        await asyncio.wait_for(
            client.run(on_packet=packets.append, on_login=logins.append),
            timeout=2.0,
        )

    asyncio.run(go())

    assert logins and isinstance(logins[0], LoginState)
    assert logins[0].logged_in is True and logins[0].uin == "10001"
    assert len(packets) == 1
    assert packets[0].cmd == "svc" and packets[0].seq == 5 and packets[0].body == b"pb"
```

- [ ] **Step 2: 跑测试确认失败**

Run: `uv run pytest tests/native/test_hook_client.py -v`
Expected: FAIL

- [ ] **Step 3: 补 `FakeTransport` 到 `src/native/pipe_transport.py`**

在文件顶部与末尾追加：

```python
from typing import Protocol


class PipeTransport(Protocol):
    def read(self, n: int) -> bytes: ...
    def close(self) -> None: ...


class FakeTransport:
    """测试用：按预置 chunk 顺序返回字节，耗尽后返回 b''（模拟管道关闭）。"""

    def __init__(self, chunks: list[bytes]) -> None:
        self._chunks = list(chunks)
        self._closed = False

    def read(self, n: int) -> bytes:
        if self._closed or not self._chunks:
            return b""
        return self._chunks.pop(0)

    def close(self) -> None:
        self._closed = True
```

- [ ] **Step 4: 实现 `src/native/hook_client.py`**

```python
import asyncio
from collections.abc import Callable
from dataclasses import dataclass

from src.native.hqp1 import (
    FLAG_LOGGED_IN,
    FrameReader,
    PipeOp,
    RecvPacket,
    packet_from_frame,
)
from src.native.pipe_transport import PipeTransport


@dataclass
class LoginState:
    logged_in: bool
    uin: str


class RecvHookClient:
    def __init__(self, transport: PipeTransport) -> None:
        self._transport = transport
        self._stopped = False

    def stop(self) -> None:
        self._stopped = True
        self._transport.close()

    async def run(
        self,
        on_packet: Callable[[RecvPacket], None],
        on_login: Callable[[LoginState], None] | None = None,
    ) -> None:
        loop = asyncio.get_running_loop()
        reader = FrameReader()
        while not self._stopped:
            chunk = await loop.run_in_executor(None, self._transport.read, 65536)
            if not chunk:
                return
            for frame in reader.push(chunk):
                if frame.op == int(PipeOp.login_state):
                    if on_login is not None:
                        logged_in = bool(frame.flags & FLAG_LOGGED_IN) or frame.status != 0
                        on_login(LoginState(logged_in=logged_in, uin=frame.msg or str(frame.value0)))
                    continue
                pkt = packet_from_frame(frame)
                if pkt is not None:
                    on_packet(pkt)
```

- [ ] **Step 5: 跑测试确认通过**

Run: `uv run pytest tests/native/test_hook_client.py -v`
Expected: PASS

- [ ] **Step 6: lint + 提交**

```bash
uv run ruff check --fix . && uv run ruff format .
git add src/native/pipe_transport.py src/native/hook_client.py tests/native/test_hook_client.py
git commit -m "feat(native): async recv hook client with injectable transport"
```

---

## Task 8: 二进制定位器 + 捕获编排器

**Files:**
- Create: `src/native/binary_locator.py`
- Create: `src/native/capture.py`
- Test: `tests/native/test_binary_locator.py`, `tests/native/test_capture.py`

**Interfaces:**
- Consumes: `RecvHookClient`, `LoginState`（Task 7）；`Win32NamedPipeTransport`（Task 2）；`decode_message_push`（Task 6）；`CapturedMessage`（Task 5）
- Produces:
  - `binary_locator.py`:
    - `HOOK_DLL_NAME = "snowluma-win32-x64.dll"`
    - `find_hook_binary(search_dirs: list[str] | None = None) -> str | None`（在候选目录找 `.dll`，找不到返回 None）
    - `missing_binary_help() -> str`（多行指引：放置路径 + 从 SnowLuma release 获取）
  - `capture.py`:
    - `def enumerate_qq_pids() -> list[int]`（用 psutil 找进程名 `QQ.exe`）
    - `def recv_pipe_name(pid: int) -> str`（`\\.\pipe\mojo.<pid>.recv`）
    - `class RecvCapture:`
      - `def __init__(self, pid: int, on_message: Callable[[CapturedMessage], None], transport_factory=Win32NamedPipeTransport)`
      - `async def run(self) -> None`（连管道 → `RecvHookClient.run`，每个包过 `decode_message_push`，非 None 时回调 `on_message`）
      - `def stop(self) -> None`

- [ ] **Step 1: 写失败测试（locator）**

`tests/native/test_binary_locator.py`：

```python
from src.native.binary_locator import HOOK_DLL_NAME, find_hook_binary, missing_binary_help


def test_find_hook_binary_locates_in_dir(tmp_path):
    (tmp_path / HOOK_DLL_NAME).write_bytes(b"MZ")
    found = find_hook_binary([str(tmp_path)])
    assert found is not None and found.endswith(HOOK_DLL_NAME)


def test_find_hook_binary_missing_returns_none(tmp_path):
    assert find_hook_binary([str(tmp_path)]) is None


def test_missing_binary_help_mentions_name():
    assert HOOK_DLL_NAME in missing_binary_help()
```

- [ ] **Step 2: 写失败测试（capture）**

`tests/native/test_capture.py`：

```python
import asyncio

from src.native.capture import RecvCapture, recv_pipe_name
from src.native.hqp1 import PipeOp, encode_frame
from src.native.pipe_transport import FakeTransport


def test_recv_pipe_name():
    assert recv_pipe_name(4321) == r"\\.\pipe\mojo.4321.recv"


def test_recv_capture_emits_decoded_message(monkeypatch):
    # 构造一个可被 decode_message_push 识别的帧：这里用非消息 cmd 验证「不回调」，
    # 真消息路径由 sso_decode 的 fixture 测试覆盖。
    frame = encode_frame(PipeOp.recv_packet, value0=1, cmd="not.a.message", msg="10001", body=b"")
    transport = FakeTransport([frame])
    got = []
    cap = RecvCapture(pid=1, on_message=got.append, transport_factory=lambda name: transport)
    asyncio.run(asyncio.wait_for(cap.run(), timeout=2.0))
    assert got == []  # 非消息 cmd 不产出
```

- [ ] **Step 3: 跑测试确认失败**

Run: `uv run pytest tests/native/test_binary_locator.py tests/native/test_capture.py -v`
Expected: FAIL

- [ ] **Step 4: 实现 `src/native/binary_locator.py`**

```python
import os

HOOK_DLL_NAME = "snowluma-win32-x64.dll"

_DEFAULT_DIRS = ["native", os.path.join("asset", "native")]


def find_hook_binary(search_dirs: list[str] | None = None) -> str | None:
    dirs = search_dirs if search_dirs is not None else _DEFAULT_DIRS
    for d in dirs:
        candidate = os.path.join(d, HOOK_DLL_NAME)
        if os.path.exists(candidate):
            return os.path.abspath(candidate)
    return None


def missing_binary_help() -> str:
    return (
        f"未找到 {HOOK_DLL_NAME}。\n"
        f"请从 SnowLuma release 获取该文件，放到 ./native/ 目录下。\n"
        "该二进制为 SnowLuma 专有组件，QQListener 不随附分发（LICENSE §5）。"
    )
```

- [ ] **Step 5: 实现 `src/native/capture.py`**

```python
import asyncio
from collections.abc import Callable

from src.native.hook_client import RecvHookClient
from src.native.model import CapturedMessage
from src.native.pipe_transport import Win32NamedPipeTransport
from src.native.sso_decode import decode_message_push


def enumerate_qq_pids() -> list[int]:
    import psutil

    pids: list[int] = []
    for proc in psutil.process_iter(["name"]):
        name = proc.info.get("name") or ""
        if name.lower() == "qq.exe":
            pids.append(proc.pid)
    return pids


def recv_pipe_name(pid: int) -> str:
    return rf"\\.\pipe\mojo.{pid}.recv"


class RecvCapture:
    def __init__(
        self,
        pid: int,
        on_message: Callable[[CapturedMessage], None],
        transport_factory: Callable[[str], object] = Win32NamedPipeTransport,
    ) -> None:
        self._pid = pid
        self._on_message = on_message
        self._transport_factory = transport_factory
        self._client: RecvHookClient | None = None

    def stop(self) -> None:
        if self._client is not None:
            self._client.stop()

    async def run(self) -> None:
        transport = self._transport_factory(recv_pipe_name(self._pid))
        self._client = RecvHookClient(transport)

        def handle(packet):
            msg = decode_message_push(packet)
            if msg is not None:
                self._on_message(msg)

        await self._client.run(on_packet=handle)
```

- [ ] **Step 6: 跑测试确认通过**

Run: `uv run pytest tests/native/test_binary_locator.py tests/native/test_capture.py -v`
Expected: PASS

- [ ] **Step 7: 全量测试 + lint + 提交**

```bash
uv run pytest tests/native -v
uv run ruff check --fix . && uv run ruff format .
git add src/native/binary_locator.py src/native/capture.py tests/native/test_binary_locator.py tests/native/test_capture.py
git commit -m "feat(native): binary locator and recv capture orchestrator"
```

---

## Task 9: 端到端真机验证（人工）

**Files:**
- Create: `scripts/capture_demo.py`（把 `RecvCapture` 跑起来打印 `CapturedMessage`）

**Interfaces:**
- Consumes: `enumerate_qq_pids`, `RecvCapture`（Task 8）；`message_text`（Task 5）

> 人工验收任务，无单测。前置同 Task 2（QQ 已注入 SnowLuma `.dll`）。

- [ ] **Step 1: 写 demo 脚本**

`scripts/capture_demo.py`：

```python
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from src.native.capture import RecvCapture, enumerate_qq_pids  # noqa: E402
from src.native.model import message_text  # noqa: E402


async def main() -> None:
    pids = enumerate_qq_pids()
    if not pids:
        print("未找到 QQ.exe")
        return
    pid = pids[0]
    print(f"捕获 PID={pid}，在群里发消息试试（Ctrl+C 退出）")

    def on_message(msg):
        print(f"[{msg.scene}] 群={msg.peer_id} 发送者={msg.sender_id}: {message_text(msg)}")
        for seg in msg.segments:
            if seg.url:
                print(f"    {seg.type} url={seg.url}")

    cap = RecvCapture(pid, on_message)
    try:
        await cap.run()
    except KeyboardInterrupt:
        cap.stop()


if __name__ == "__main__":
    asyncio.run(main())
```

- [ ] **Step 2: 真机运行验收**

`uv run python scripts/capture_demo.py` → 群里发「文本 + 图片 + 文件」。**验收：**
- 文本消息打印出正确文字。
- 图片消息打印出 `image url=http://...`，且该 URL 浏览器可打开。
- 文件消息打印出文件名（若 Task 4 的 FileElem 字段号已校准）。

**若任一项不符**：回到对应 Task（cmd → Task 6；元素字段号 → Task 4/5），用 Task 2 fixture 修正字段号，补/改 golden test，再重跑。

- [ ] **Step 3: 提交**

```bash
git add scripts/capture_demo.py
git commit -m "feat(native): end-to-end capture demo script"
```

---

## 元素类型覆盖（对 spec 首批的诚实说明）

spec 首批列了 7 种元素：`text / at / image / file / record / reply / video`。本计划**只具体实现 text + image**，作为「从字节到 CapturedMessage」的完整竖切片并端到端验证。其余 5 种（at/file/record/video/reply）**故意不在本计划实现**，原因：它们的 protobuf 字段号必须由 Phase 0 真帧核实，凭 SnowLuma 源码猜值再写 TDD 等于制造伪测试。

这 5 种是 Task 5 `_elem_to_segment` 分派表的**同构扩展**——每种照 Task 4 的模式走一遍：真机发一条该类型消息 → 从 fixture 用 `decode_fields` 剥出字段号 → 加 parser + golden test。作为本计划验证通过后的**紧接续作**（Plan A.1），不与 Plan B（集成）混。`message_text`（Task 5）已预留全部 7 种的渲染分支，故扩展时只需补 element 解析、不动渲染。

## 收尾：本计划完成的判定

- `uv run pytest tests/native -v` 全绿。
- `scripts/capture_demo.py` 在真机上打印出含图片 URL 的结构化消息。
- 无任何 SnowLuma 二进制被提交（`git log --stat` 核对）。
- 元素覆盖：text + image 端到端可用；其余 5 种按上节作为 Plan A.1 续作（已在此显式声明，非静默缩范围）。

## 后续计划（本计划不含，验证后另写）

- **Plan B（集成）**：`MessageProcessor` 增 `process_captured(CapturedMessage)` 适配现有过滤/去重/TTS/通知窗（复用 `_download_url`/`_file_icon_for_path`，迁至 `src/utils/media.py`）；`worker.py` 用 `RecvCapture` 替换 `_run_selected_engine`；删除 `src/core/notification_engines.py` 全部旧引擎。
- **Plan C（Python 注入器）**：用 ctypes 忠实复刻 manual-map 注入（alloc/节拷贝/重定位/IAT/异常表/entry），替换借用的 SnowLuma `.node`，彻底脱离 Node。**必须保持 manual map（防封），不得用 LoadLibrary。**
- **Plan D+（未来）**：macOS hook，另立项目。
