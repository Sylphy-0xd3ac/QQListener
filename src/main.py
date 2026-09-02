#!/usr/bin/env python3
"""QQListener 入口。

不带参数 = daemon 模式：后台常驻（托盘 + 悬浮球），不弹设置窗口。
带子命令 = 控制已经在跑的那个实例（走本机 IPC 通道，不启动第二份）。
"""

import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

_USAGE = """用法:
  qqlistener                后台启动（daemon，默认）
  qqlistener --window       启动并直接打开设置窗口
  qqlistener status         查看守护进程与核心状态
  qqlistener start          开始监听
  qqlistener pause          暂停监听（核心留在 QQ 里）
  qqlistener toggle         监听 ⇄ 暂停
  qqlistener unload         从 QQ 卸载核心
  qqlistener show           打开设置窗口
  qqlistener reload         重新加载 setting.json
  qqlistener quit           退出守护进程
"""

_CONTROL_COMMANDS = {
    "status",
    "start",
    "pause",
    "toggle",
    "unload",
    "show",
    "reload",
    "quit",
}


def _attach_console() -> None:
    """--windowed 打出来的 exe 没有控制台，子命令的输出会掉进黑洞。

    Windows 上先蹭调用方的控制台，让 `qqlistener.exe status` 有地方打印。
    """
    if sys.platform != "win32":
        return
    try:
        import ctypes

        if not ctypes.windll.kernel32.AttachConsole(-1):
            return
        for stream, name in ((sys.stdout, "stdout"), (sys.stderr, "stderr")):
            if stream is None or stream.fileno() < 0:
                # 这个句柄要活到进程结束，不能用 with 关掉。
                stream = open("CONOUT$", "w", encoding="utf-8", buffering=1)  # noqa: SIM115
                setattr(sys, name, stream)
    except Exception:
        pass


def _control(command: str) -> int:
    _attach_console()
    from src.core.ipc import send_command

    response = send_command(command)
    if response is None:
        print("QQListener 守护进程没有在运行（先不带参数启动它）", file=sys.stderr)
        return 3
    print(json.dumps(response, ensure_ascii=False, indent=2))
    return 0 if response.get("ok") else 1


def main(argv: list[str]) -> int:
    args = [arg for arg in argv[1:] if arg]
    if args and args[0] in {"-h", "--help", "help"}:
        _attach_console()
        print(_USAGE)
        return 0

    if args and args[0].lstrip("-") in _CONTROL_COMMANDS:
        return _control(args[0].lstrip("-"))

    daemon = "--window" not in args and "-w" not in args

    from src.core.app import run_app

    # 单实例判定放在 run_app 里做：那里已经有 QApplication，
    # 抢不到控制通道就说明已有一份在跑，会把它唤到前台再退出。
    run_app(daemon=daemon)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
