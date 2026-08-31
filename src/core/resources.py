import sys
from pathlib import Path


def app_root() -> Path:
    """可持久写入的应用根目录（配置和用户下载的核心放在这里）。"""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[2]


def bundle_root() -> Path:
    """打包资源根目录；PyInstaller onefile 下指向本次运行的解包目录。"""
    if getattr(sys, "frozen", False):
        extracted = getattr(sys, "_MEIPASS", None)
        if extracted:
            return Path(extracted)
    return app_root()


def resource_path(*parts: str) -> Path:
    return bundle_root().joinpath(*parts)


def app_icon_path() -> Path:
    return resource_path("icon.ico")


def app_icon_png_path() -> Path:
    return resource_path("asset", "app_icon.png")
