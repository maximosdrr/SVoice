"""Persistent directories used by the service.

All user data lives outside the MSIX package so that reinstalling or updating
the widget never touches voices, models or settings:

    %LOCALAPPDATA%\SVoice\XTTS        models, voices, temp, config, discovery
    %LOCALAPPDATA%\SVoice\Logs        rotating logs
    %LOCALAPPDATA%\SVoice\Downloads   download cache (runtime packs, model)
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

DATA_SCHEMA_VERSION = 2


def local_app_data() -> Path:
    value = os.environ.get("LOCALAPPDATA")
    if value:
        return Path(value)
    return Path.home() / "AppData" / "Local"


def default_root() -> Path:
    return local_app_data() / "SVoice"


@dataclass(frozen=True)
class DataPaths:
    data_dir: Path

    @property
    def voices_dir(self) -> Path:
        return self.data_dir / "voices"

    @property
    def temp_dir(self) -> Path:
        return self.data_dir / "temp"

    @property
    def models_dir(self) -> Path:
        return self.data_dir / "models"

    @property
    def backups_dir(self) -> Path:
        return self.data_dir / "backups"

    @property
    def registry_path(self) -> Path:
        return self.data_dir / "profiles.json"

    @property
    def config_path(self) -> Path:
        return self.data_dir / "config.json"

    @property
    def discovery_path(self) -> Path:
        return self.data_dir / "service.json"

    @property
    def logs_dir(self) -> Path:
        return self.data_dir.parent / "Logs"

    @property
    def downloads_dir(self) -> Path:
        return self.data_dir.parent / "Downloads"

    def ensure(self) -> None:
        for directory in (
            self.data_dir,
            self.voices_dir,
            self.temp_dir,
            self.models_dir,
            self.backups_dir,
            self.logs_dir,
        ):
            directory.mkdir(parents=True, exist_ok=True)


def restrict_to_current_user(path: Path) -> None:
    """Best-effort ACL hardening of a file to the current user only."""
    if sys.platform != "win32":
        return
    user = os.environ.get("USERNAME")
    domain = os.environ.get("USERDOMAIN")
    if not user:
        return
    account = f"{domain}\\{user}" if domain else user
    try:
        subprocess.run(
            [
                "icacls",
                str(path),
                "/inheritance:r",
                "/grant:r",
                f"{account}:(R,W,D)",
            ],
            capture_output=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        pass


def free_space_bytes(path: Path) -> int:
    probe = path
    while not probe.exists() and probe.parent != probe:
        probe = probe.parent
    try:
        return shutil.disk_usage(probe).free
    except OSError:
        return 0
