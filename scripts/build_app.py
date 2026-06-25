#!/usr/bin/env python3
import argparse
import shlex
import subprocess
import sys
from contextlib import contextmanager
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
APP_NAME = "QQListener"
UI_INIT_PATH = PROJECT_ROOT / "src" / "ui" / "__init__.py"


def normalize_qt_api(value: str) -> str:
    api = str(value or "").strip().lower().replace("-", "").replace("_", "")
    if api in {"pyside2", "qt5"}:
        return "pyside2"
    return "pyside6"


def _data_spec(source: Path, target: str) -> str:
    separator = ";" if sys.platform == "win32" else ":"
    return f"{source}{separator}{target}"


def _add_data(command: list[str], source: Path, target: str) -> None:
    if source.exists():
        command.extend(["--add-data", _data_spec(source, target)])


def _add_hidden_imports(command: list[str], imports: list[str]) -> None:
    for module_name in imports:
        command.extend(["--hidden-import", module_name])


def _add_excluded_imports(command: list[str], imports: list[str]) -> None:
    for module_name in imports:
        command.extend(["--exclude-module", module_name])


@contextmanager
def write_ui_qt_api(qt_api: str):
    original = UI_INIT_PATH.read_text(encoding="utf-8") if UI_INIT_PATH.exists() else None
    UI_INIT_PATH.write_text(
        f'QT_API = "{qt_api}"\nqtapi = QT_API\n',
        encoding="utf-8",
    )
    try:
        yield
    finally:
        if original is None:
            UI_INIT_PATH.unlink(missing_ok=True)
        else:
            UI_INIT_PATH.write_text(original, encoding="utf-8")


def build_command(name: str, qt_api: str) -> list[str]:
    qt_binding = "PySide2" if qt_api == "pyside2" else "PySide6"
    excluded_qt_binding = "PySide6" if qt_api == "pyside2" else "PySide2"
    command = [
        sys.executable,
        "-m",
        "PyInstaller",
        "--noconfirm",
        "--clean",
        "--onefile",
        "--windowed",
        "--name",
        name,
        "--distpath",
        str(PROJECT_ROOT / "dist"),
        "--workpath",
        str(PROJECT_ROOT / "build"),
        "--paths",
        str(PROJECT_ROOT),
        "--runtime-hook",
        str(PROJECT_ROOT / "scripts" / "pyinstaller_runtime_hook.py"),
        "--collect-data",
        "qfluentwidgets",
        "--collect-data",
        "qframelesswindow",
        "--collect-submodules",
        "qfluentwidgets",
        "--collect-submodules",
        "qframelesswindow",
        "--hidden-import",
        "src.ui.qt_compat",
        "--hidden-import",
        f"{qt_binding}.QtSvg",
        "--hidden-import",
        "aiohttp.web",
        "--hidden-import",
        "edge_tts",
        "--hidden-import",
        "pygame",
    ]
    _add_excluded_imports(
        command,
        [
            excluded_qt_binding,
            f"{excluded_qt_binding}.QtCore",
            f"{excluded_qt_binding}.QtGui",
            f"{excluded_qt_binding}.QtWidgets",
            f"{excluded_qt_binding}.QtSvg",
        ],
    )

    icon_path = PROJECT_ROOT / "icon.ico"
    if icon_path.exists() and sys.platform == "win32":
        command.extend(["--icon", str(icon_path)])

    _add_data(command, PROJECT_ROOT / "asset", "asset")
    _add_data(command, PROJECT_ROOT / "translations", "translations")
    _add_data(command, icon_path, "icon.ico")

    if sys.platform == "win32":
        _add_hidden_imports(
            command,
            [
                "comtypes",
                "pycaw.pycaw",
                "uiautomation",
                "winsdk.windows.ui.notifications",
                "winsdk.windows.ui.notifications.management",
            ],
        )
        command.extend(["--collect-submodules", "comtypes", "--collect-submodules", "pycaw"])
    elif sys.platform == "darwin":
        _add_hidden_imports(command, ["AppKit", "Foundation", "objc"])

    command.append(str(PROJECT_ROOT / "main.py"))
    return command


def main() -> int:
    parser = argparse.ArgumentParser(description="Build QQListener with PyInstaller.")
    parser.add_argument("--name", default=APP_NAME)
    parser.add_argument("--qt-api", default="pyside6", choices=["pyside2", "pyside6"])
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    qt_api = normalize_qt_api(args.qt_api)
    command = build_command(args.name, qt_api)
    if args.dry_run:
        print(shlex.join(command))
        return 0

    with write_ui_qt_api(qt_api):
        subprocess.run(command, cwd=PROJECT_ROOT, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
