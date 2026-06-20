import os
import sys

from loguru import logger


def setup_logging() -> None:
    """Configure loguru sinks and level."""
    logger.remove()
    level = os.getenv("LOG_LEVEL", "INFO")
    if sys.stderr is not None:
        logger.add(sys.stderr, level=level, backtrace=False, diagnose=False)
    # GUI mode (--windowed): both stderr and stdout are None, log to file
    import tempfile
    log_path = os.path.join(tempfile.gettempdir(), "QQListener.log")
    logger.add(log_path, level=level, rotation="10 MB", backtrace=False, diagnose=False)
