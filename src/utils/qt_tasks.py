"""后台 QThread 的存活登记。

QThread 在跑的时候必须一直有人持有它。一旦最后一个引用没了，Python GC 会析构
它，而 C++ 侧发现线程还在运行就直接 `qFatal("QThread: Destroyed while thread is
still running")` —— 进程 abort，**不经过 Python，日志里没有任何 traceback**。

同理也不能把这类线程 parent 到窗口/控件上：控件先销毁就会带着还在跑的线程一起析构。

所以：不设 parent，改为登记到这里，线程自己跑完再注销。
"""

from __future__ import annotations

_alive: set = set()


def keep_alive(thread) -> None:
    """让 ``thread`` 活到它自己结束为止。"""
    if thread in _alive:
        return
    _alive.add(thread)
    thread.finished.connect(lambda: _release(thread))


def _release(thread) -> None:
    _alive.discard(thread)


def alive_count() -> int:
    return len(_alive)
