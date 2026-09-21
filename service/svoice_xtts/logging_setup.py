from __future__ import annotations

import logging
import logging.handlers
import sys
from pathlib import Path

LOG_FILE_NAME = "xtts-service.log"
MAX_BYTES = 2 * 1024 * 1024
BACKUP_COUNT = 5


def configure_logging(logs_dir: Path, level: str = "INFO") -> logging.Logger:
    logger = logging.getLogger("svoice")
    logger.setLevel(getattr(logging, level.upper(), logging.INFO))
    logger.propagate = False
    if logger.handlers:
        return logger

    formatter = logging.Formatter(
        "%(asctime)s %(levelname)s %(name)s: %(message)s"
    )
    try:
        logs_dir.mkdir(parents=True, exist_ok=True)
        file_handler = logging.handlers.RotatingFileHandler(
            logs_dir / LOG_FILE_NAME,
            maxBytes=MAX_BYTES,
            backupCount=BACKUP_COUNT,
            encoding="utf-8",
        )
        file_handler.setFormatter(formatter)
        logger.addHandler(file_handler)
    except OSError:
        pass

    stream_handler = logging.StreamHandler(sys.stderr)
    stream_handler.setFormatter(formatter)
    logger.addHandler(stream_handler)
    return logger


def get_logger(name: str) -> logging.Logger:
    return logging.getLogger(f"svoice.{name}")
