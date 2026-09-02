"""暂停期间的消息积压队列。

`CoreState.PAUSED` 时钩子还在 QQ 里、管道也还通，所以捕获照常进行——只是解析好的
通知不弹出来，先压在这里。恢复监听时一次性倒出来，合成一个摘要窗口。

解析（含图片预取、文件地址、rkey 签名）在**入队前**就做完：这些凭证都是短效的，
拖到恢复时再解就已经过期了。

队列有上限，满了丢最旧的——积压不该把内存和磁盘吃穿。
"""

from __future__ import annotations

import threading

_DEFAULT_MAX = 50

_lock = threading.Lock()
_items: list[dict] = []
_dropped = 0


def push_pending(payload: dict, max_items: int = _DEFAULT_MAX) -> None:
    global _dropped
    if not payload:
        return
    limit = max(1, int(max_items))
    with _lock:
        _items.append(payload)
        while len(_items) > limit:
            _items.pop(0)
            _dropped += 1


def drain_pending() -> tuple[list[dict], int]:
    """取出全部积压，返回 (消息, 因超出上限被丢弃的条数)。"""
    global _dropped
    with _lock:
        items, _items[:] = list(_items), []
        dropped, _dropped = _dropped, 0
    return items, dropped


def pending_count() -> int:
    with _lock:
        return len(_items)


def clear_pending() -> None:
    global _dropped
    with _lock:
        _items.clear()
        _dropped = 0
