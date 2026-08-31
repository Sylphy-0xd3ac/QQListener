import os

HOOK_DLL_NAME = "snowluma-win32-x64.dll"


def _default_dirs() -> list[str]:
    from src.core.resources import app_root

    root = app_root()
    return [str(root / "native"), str(root / "asset" / "native")]


def find_hook_binary(search_dirs: list[str] | None = None) -> str | None:
    dirs = search_dirs if search_dirs is not None else _default_dirs()
    for d in dirs:
        candidate = os.path.join(d, HOOK_DLL_NAME)
        if os.path.exists(candidate):
            return os.path.abspath(candidate)
    return None


def missing_binary_help() -> str:
    return (
        f"未找到 {HOOK_DLL_NAME}。\n"
        f"请从 SnowLuma release 获取该文件，放到 ./native/ 目录下。\n"
        "该二进制为 SnowLuma 专有组件，QQListener 不随附分发（LICENSE §5）。"
    )
