"""端到端 demo：把 RecvCapture 跑起来，打印 CapturedMessage。

前置同 probe_recv.py（核心已由 Python 装载器装入 QQ）。用法：python scripts/capture_demo.py
"""

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
