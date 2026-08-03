"""Phase 0 探针：连 QQ 的 SnowLuma recv 命名管道，转储帧。

前置：QQ 已运行，且已用 SnowLuma 的 .node 注入器把 .dll 塞进该 QQ 进程
（本脚本不做注入）。用法：python scripts/probe_recv.py <pid> [--dump-dir DIR]
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
                op_name = (
                    PipeOp(frame.op).name
                    if frame.op in PipeOp._value2member_map_
                    else str(frame.op)
                )
                print(
                    f"[{op_name}] status={frame.status} flags={frame.flags} "
                    f"value0={frame.value0} cmd={frame.cmd!r} msg={frame.msg!r} "
                    f"bodyLen={len(frame.body)}"
                )
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
