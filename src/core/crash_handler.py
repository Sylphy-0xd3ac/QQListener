"""把所有崩溃都写进日志。

之前"崩溃了但日志里一个 traceback 都没有"有三个来源，各自需要单独接管：

1. **Qt 槽函数里未捕获的 Python 异常** —— PySide6 会走 `sys.excepthook` 然后终止进程。
   GUI 模式（--windowed）下 stderr 是 None，默认 excepthook 打到哪儿都不知道。
2. **子线程里的异常** —— 走的是 `threading.excepthook`，跟主线程那个是两回事。
3. **Qt 自己的致命错误** —— 比如 `QThread: Destroyed while thread is still running`，
   直接在 C++ 侧 abort，**根本不经过 Python**。只有装 Qt 消息处理器才拦得到。
"""

from __future__ import annotations

import sys
import threading

from loguru import logger

from src.ui.qt_compat import QtCore

_installed = False

_QT_LEVELS = {
    QtCore.QtMsgType.QtDebugMsg: "DEBUG",
    QtCore.QtMsgType.QtInfoMsg: "INFO",
    QtCore.QtMsgType.QtWarningMsg: "WARNING",
    QtCore.QtMsgType.QtCriticalMsg: "ERROR",
    QtCore.QtMsgType.QtFatalMsg: "CRITICAL",
}


def _log_exception(exc_type, exc_value, exc_tb) -> None:
    if issubclass(exc_type, KeyboardInterrupt):
        sys.__excepthook__(exc_type, exc_value, exc_tb)
        return
    logger.opt(exception=(exc_type, exc_value, exc_tb)).critical("未捕获异常")


def _log_thread_exception(args) -> None:
    if issubclass(args.exc_type, SystemExit):
        return
    name = getattr(args.thread, "name", "?")
    logger.opt(exception=(args.exc_type, args.exc_value, args.exc_traceback)).critical(
        "线程 {} 未捕获异常", name
    )


def _log_unraisable(args) -> None:
    logger.opt(exception=(args.exc_type, args.exc_value, args.exc_traceback)).error(
        "析构/回调中的异常: {}", args.object
    )


def _qt_message_handler(mode, context, message: str) -> None:
    level = _QT_LEVELS.get(mode, "INFO")
    where = ""
    if context is not None and context.file:
        where = f" [{context.file}:{context.line}]"
    logger.log(level, "Qt: {}{}", message, where)
    if level == "CRITICAL":
        # Qt 接下来就要 abort 了，把 Python 侧的调用栈也留下——这往往是唯一线索。
        logger.critical("Qt 致命错误，即将终止。Python 调用栈：\n{}", "".join(_stack()))


def _stack() -> list[str]:
    import traceback

    return traceback.format_stack()


def install_crash_logging() -> None:
    """尽早调用，越早装越能盖住启动期的崩溃。"""
    global _installed
    if _installed:
        return
    _installed = True

    sys.excepthook = _log_exception
    threading.excepthook = _log_thread_exception
    if hasattr(sys, "unraisablehook"):
        sys.unraisablehook = _log_unraisable
    QtCore.qInstallMessageHandler(_qt_message_handler)
    logger.debug("崩溃日志钩子已安装")
