import sys
from pathlib import Path

from loguru import logger

RUN_VALUE_NAME = "QQListener"
RUN_KEY_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"


def is_auto_start_supported() -> bool:
    return sys.platform == "win32"


def _quote(value: str) -> str:
    return f'"{value}"'


def auto_start_command() -> str:
    executable = Path(sys.executable).resolve()
    executable_name = executable.name.lower()

    if executable_name in {"python.exe", "pythonw.exe"}:
        script = Path(sys.argv[0]).resolve() if sys.argv and sys.argv[0] else Path("main.py").resolve()
        return f"{_quote(str(executable))} {_quote(str(script))}"

    return _quote(str(executable))


def is_auto_start_enabled() -> bool:
    if not is_auto_start_supported():
        return False

    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY_PATH, 0, winreg.KEY_READ) as key:
            winreg.QueryValueEx(key, RUN_VALUE_NAME)
        return True
    except FileNotFoundError:
        return False
    except OSError:
        logger.exception("读取开机自启动状态失败")
        return False


def set_auto_start_enabled(enabled: bool) -> bool:
    if not is_auto_start_supported():
        return not enabled

    try:
        import winreg

        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY_PATH) as key:
            if enabled:
                winreg.SetValueEx(key, RUN_VALUE_NAME, 0, winreg.REG_SZ, auto_start_command())
            else:
                try:
                    winreg.DeleteValue(key, RUN_VALUE_NAME)
                except FileNotFoundError:
                    pass
        return True
    except OSError:
        logger.exception("设置开机自启动失败")
        return False
