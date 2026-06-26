from __future__ import annotations

from collections.abc import Callable


_notifications_muted = False
_listeners: list[Callable[[bool], None]] = []


def is_notifications_muted() -> bool:
    return _notifications_muted


def set_notifications_muted(muted: bool) -> None:
    global _notifications_muted
    muted = bool(muted)
    if _notifications_muted == muted:
        return

    _notifications_muted = muted
    _emit_notifications_muted_changed(muted)


def toggle_notifications_muted() -> bool:
    set_notifications_muted(not _notifications_muted)
    return _notifications_muted


def add_notification_state_listener(listener: Callable[[bool], None]) -> None:
    if listener not in _listeners:
        _listeners.append(listener)


def remove_notification_state_listener(listener: Callable[[bool], None]) -> None:
    try:
        _listeners.remove(listener)
    except ValueError:
        pass


def _emit_notifications_muted_changed(muted: bool) -> None:
    failed_listeners: list[Callable[[bool], None]] = []
    for listener in list(_listeners):
        try:
            listener(muted)
        except RuntimeError:
            failed_listeners.append(listener)

    for listener in failed_listeners:
        remove_notification_state_listener(listener)
