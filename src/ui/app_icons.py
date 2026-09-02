"""项目自带的 Fluent 风格图标。

qfluentwidgets 内置的 174 个里没有"日志板"这种东西——`COMMAND_PROMPT` 是命令行，
`HISTORY` 是时钟，都不对。这里按 Fluent System Icons 的 24px 网格与线性风格
（1.6 描边、圆角端点）自绘，并按亮/暗主题各出一份。
"""

from __future__ import annotations

from enum import Enum

from qfluentwidgets import FluentIconBase, Theme, getIconColor

from src.core.resources import resource_path


class AppIcon(FluentIconBase, Enum):
    """本项目补充的图标。"""

    LOG = "log"

    def path(self, theme=Theme.AUTO) -> str:
        # getIconColor 按当前主题给出 "black" / "white"，和 qfluentwidgets 内置图标一致。
        return str(resource_path("asset", "icons", f"{self.value}_{getIconColor(theme)}.svg"))
