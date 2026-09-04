"""崩溃防线：QThread 存活登记 + 崩溃日志钩子 + IPC 通道名。

这些都是"崩了但日志里没有 traceback"那类问题的根因或诊断手段，回归了很难查。
"""

import os

import pytest

from src.core import ipc
from src.utils import qt_tasks


class _FakeSignal:
    def __init__(self):
        self._slots = []

    def connect(self, slot):
        self._slots.append(slot)

    def emit(self):
        for slot in list(self._slots):
            slot()


class _FakeThread:
    def __init__(self):
        self.finished = _FakeSignal()


@pytest.fixture(autouse=True)
def _clean_registry():
    qt_tasks._alive.clear()
    yield
    qt_tasks._alive.clear()


# ---------- QThread 存活登记 ----------


def test_registered_thread_is_held_until_it_finishes():
    """没人持有正在运行的 QThread，GC 一析构 Qt 就 abort（且没有 Python traceback）。"""
    thread = _FakeThread()

    qt_tasks.keep_alive(thread)

    assert qt_tasks.alive_count() == 1
    thread.finished.emit()
    assert qt_tasks.alive_count() == 0


def test_registering_twice_does_not_double_hold():
    thread = _FakeThread()

    qt_tasks.keep_alive(thread)
    qt_tasks.keep_alive(thread)

    assert qt_tasks.alive_count() == 1
    thread.finished.emit()
    assert qt_tasks.alive_count() == 0


def test_threads_are_tracked_independently():
    first, second = _FakeThread(), _FakeThread()
    qt_tasks.keep_alive(first)
    qt_tasks.keep_alive(second)

    first.finished.emit()

    assert qt_tasks.alive_count() == 1


# ---------- IPC 通道名 ----------


@pytest.mark.parametrize(
    ("username", "expected"),
    [
        ("duzexuan", "QQListener.control.duzexuan"),
        ("Class-PC 01", "QQListener.control.Class_PC_01"),
        ("中文", "QQListener.control"),  # 全部被折掉就不带后缀
        ("杜泽轩.admin", "QQListener.control.admin"),
    ],
)
def test_server_name_only_keeps_safe_characters(monkeypatch, username, expected):
    """用户名会拼进命名管道路径；中文名、空格、点号在真实机器上都会出现。"""
    monkeypatch.setenv("USERNAME", username)
    monkeypatch.setenv("USER", username)

    assert ipc.server_name() == expected


def test_server_name_is_bounded(monkeypatch):
    monkeypatch.setenv("USERNAME", "a" * 200)
    monkeypatch.setenv("USER", "a" * 200)

    name = ipc.server_name()

    assert len(name.split(".")[-1]) <= 32


def test_server_name_falls_back_when_environment_is_empty(monkeypatch):
    monkeypatch.delenv("USERNAME", raising=False)
    monkeypatch.delenv("USER", raising=False)

    name = ipc.server_name()

    assert name.startswith("QQListener.control")
    if hasattr(os, "getuid"):
        assert name.split(".")[-1].isdigit()


# ---------- 崩溃钩子 ----------


def test_crash_hooks_are_installed_once():
    import sys
    import threading

    from src.core import crash_handler

    crash_handler._installed = False
    try:
        crash_handler.install_crash_logging()
        assert sys.excepthook is crash_handler._log_exception
        assert threading.excepthook is crash_handler._log_thread_exception
        assert crash_handler._installed is True
    finally:
        sys.excepthook = sys.__excepthook__
        threading.excepthook = threading.__excepthook__
        crash_handler._installed = False


def test_uncaught_exception_is_logged_with_traceback():
    from loguru import logger

    from src.core import crash_handler

    messages = []
    sink = logger.add(lambda m: messages.append(str(m)), level="CRITICAL")
    try:
        try:
            raise ValueError("模拟槽函数里的异常")
        except ValueError:
            import sys as _sys

            crash_handler._log_exception(*_sys.exc_info())
    finally:
        logger.remove(sink)

    joined = "".join(messages)
    assert "未捕获异常" in joined
    assert "模拟槽函数里的异常" in joined
    assert "ValueError" in joined


def test_qt_fatal_message_is_logged_at_critical():
    from loguru import logger

    from src.core import crash_handler
    from src.ui.qt_compat import QtCore

    messages = []
    sink = logger.add(lambda m: messages.append(str(m)), level="CRITICAL")
    try:
        crash_handler._qt_message_handler(
            QtCore.QtMsgType.QtFatalMsg, None, "QThread: Destroyed while thread is still running"
        )
    finally:
        logger.remove(sink)

    joined = "".join(messages)
    assert "QThread: Destroyed" in joined
    assert "Python 调用栈" in joined  # abort 前留下的唯一线索
