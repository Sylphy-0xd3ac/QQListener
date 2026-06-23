#!/usr/bin/env python3
import argparse
import shlex
import subprocess
import sys
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
APP_NAME = "QQListener"


def _data_spec(source: Path, target: str) -> str:
    separator = ";" if sys.platform == "win32" else ":"
    return f"{source}{separator}{target}"


def _add_data(command: list[str], source: Path, target: str) -> None:
    if source.exists():
        command.extend(["--add-data", _data_spec(source, target)])


def _add_hidden_imports(command: list[str], imports: list[str]) -> None:
    for module_name in imports:
        command.extend(["--hidden-import", module_name])


def build_command(name: str) -> list[str]:
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
        "PySide2.QtSvg",
        "--hidden-import",
        "aiohttp.web",
        "--hidden-import",
        "edge_tts",
        "--hidden-import",
        "pygame",
    ]

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
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    command = build_command(args.name)
    if args.dry_run:
        print(shlex.join(command))
        return 0

    subprocess.run(command, cwd=PROJECT_ROOT, check=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
