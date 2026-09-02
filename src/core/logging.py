"""日志配置。

日志写到应用根目录下的 `logs/QQListener.log`（以前在系统临时目录，重启就可能被清掉，
出了问题也不好让人找）。等级可以在设置里改，改完立即生效，不用重启。
"""

from __future__ import annotations

import contextlib
import os
import sys
from pathlib import Path

from loguru import logger

LOG_LEVELS = ("TRACE", "DEBUG", "INFO", "SUCCESS", "WARNING", "ERROR")
DEFAULT_LEVEL = "INFO"

_FORMAT = (
    "<green>{time:YYYY-MM-DD HH:mm:ss.SSS}</green> | "
    "<level>{level: <8}</level> | "
    "<cyan>{name}</cyan>:<cyan>{function}</cyan>:<cyan>{line}</cyan> - <level>{message}</level>"
)
_FILE_FORMAT = "{time:YYYY-MM-DD HH:mm:ss.SSS} | {level: <8} | {name}:{function}:{line} - {message}"

_sink_ids: list[int] = []


def log_dir() -> Path:
    from src.core.resources import app_root

    return app_root() / "logs"


def log_file_path() -> Path:
    return log_dir() / "QQListener.log"


def normalize_level(value: object) -> str:
    level = str(value or "").strip().upper()
    return level if level in LOG_LEVELS else DEFAULT_LEVEL


def configured_level() -> str:
    """环境变量优先，其次是设置里的值。"""
    env_level = os.getenv("LOG_LEVEL")
    if env_level:
        return normalize_level(env_level)
    try:
        from src.core.settings import get_settings

        return normalize_level(get_settings().log_level)
    except Exception:
        return DEFAULT_LEVEL


def setup_logging(level: str | None = None) -> str:
    """装好日志 sink，返回实际生效的等级。可重复调用（换等级就再调一次）。"""
    resolved = normalize_level(level) if level else configured_level()

    for sink_id in _sink_ids:
        with contextlib.suppress(ValueError):
            logger.remove(sink_id)
    _sink_ids.clear()
    logger.remove()

    # GUI 模式（--windowed）下 stderr/stdout 都是 None，只能写文件。
    if sys.stderr is not None:
        _sink_ids.append(
            logger.add(sys.stderr, level=resolved, format=_FORMAT, backtrace=False, diagnose=False)
        )

    try:
        target = log_file_path()
        target.parent.mkdir(parents=True, exist_ok=True)
        _sink_ids.append(
            logger.add(
                str(target),
                level=resolved,
                format=_FILE_FORMAT,
                rotation="5 MB",
                retention=5,
                encoding="utf-8",
                enqueue=True,  # 多线程写同一个文件，交给 loguru 串行化
                backtrace=False,
                diagnose=False,
            )
        )
    except OSError:
        logger.warning("日志文件不可写，仅输出到控制台")

    return resolved


def read_log_tail(max_lines: int = 500) -> str:
    """读日志末尾若干行，给设置里的日志页用。"""
    path = log_file_path()
    if not path.exists():
        return "（还没有日志文件）"
    try:
        with path.open("r", encoding="utf-8", errors="replace") as handle:
            lines = handle.readlines()
    except OSError as exc:
        return f"（读取日志失败：{exc}）"
    return "".join(lines[-max_lines:]) or "（日志为空）"


def clear_log() -> bool:
    path = log_file_path()
    try:
        if path.exists():
            path.write_text("", encoding="utf-8")
        return True
    except OSError:
        logger.exception("清空日志失败")
        return False
