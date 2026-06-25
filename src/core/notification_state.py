_notifications_muted = False


def is_notifications_muted() -> bool:
    return _notifications_muted


def set_notifications_muted(muted: bool) -> None:
    global _notifications_muted
    _notifications_muted = bool(muted)


def toggle_notifications_muted() -> bool:
    set_notifications_muted(not _notifications_muted)
    return _notifications_muted
