import builtins
from contextlib import contextmanager

_QFLUENTWIDGETS_ALERT_MARKER = "QFluentWidgets Pro is now released"
_PYGAME_HELLO_MARKER = "pygame"


@contextmanager
def filtered_print():
    original_print = builtins.print

    def filtered(*args, **kwargs):
        message = " ".join(str(arg) for arg in args)
        if _QFLUENTWIDGETS_ALERT_MARKER in message or _PYGAME_HELLO_MARKER in message:
            return
        original_print(*args, **kwargs)

    builtins.print = filtered
    try:
        yield
    finally:
        builtins.print = original_print
