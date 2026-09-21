"""Single-slot job tracking with cooperative cancellation."""

from __future__ import annotations

import threading
import time
import uuid
from dataclasses import dataclass, field
from typing import Any

from .errors import CancelledError, ServiceError


@dataclass
class Job:
    kind: str
    id: str = field(default_factory=lambda: uuid.uuid4().hex)
    state: str = "running"  # running | cancelling | done | failed | cancelled
    message: str = ""
    progress: float | None = None
    started_at: float = field(default_factory=time.monotonic)
    finished_at: float | None = None
    error: str | None = None
    _cancel: threading.Event = field(default_factory=threading.Event, repr=False)

    def cancel_requested(self) -> bool:
        return self._cancel.is_set()

    def request_cancel(self) -> None:
        self._cancel.set()

    def check_cancelled(self) -> None:
        if self._cancel.is_set():
            raise CancelledError()

    def update(self, message: str | None = None, progress: float | None = None) -> None:
        if message is not None:
            self.message = message
        if progress is not None:
            self.progress = max(0.0, min(1.0, float(progress)))

    def to_json(self) -> dict[str, Any]:
        end = self.finished_at if self.finished_at is not None else time.monotonic()
        return {
            "id": self.id,
            "kind": self.kind,
            "state": self.state,
            "message": self.message,
            "progress": self.progress,
            "elapsed_seconds": round(end - self.started_at, 3),
            "error": self.error,
        }


class JobTracker:
    """Only one job runs at a time; the last finished job stays visible."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._active: Job | None = None
        self._last: Job | None = None
        self.last_activity = time.monotonic()

    def touch(self) -> None:
        self.last_activity = time.monotonic()

    @property
    def active(self) -> Job | None:
        return self._active

    @property
    def busy(self) -> bool:
        return self._active is not None

    def start(self, kind: str, message: str) -> Job:
        with self._lock:
            if self._active is not None:
                raise ServiceError(
                    "Já existe uma operação em andamento. Aguarde ou cancele-a.",
                    409,
                    code="busy",
                    action="Aguarde a conclusão ou pressione Esc para cancelar.",
                )
            job = Job(kind=kind, message=message)
            self._active = job
            self.touch()
            return job

    def finish(self, job: Job, *, error: Exception | None = None) -> None:
        with self._lock:
            job.finished_at = time.monotonic()
            if error is None:
                job.state = "done"
                job.progress = 1.0
            elif isinstance(error, CancelledError):
                job.state = "cancelled"
                job.error = str(error)
            else:
                job.state = "failed"
                job.error = str(error)
            if self._active is job:
                self._active = None
            self._last = job
            self.touch()

    def cancel(self) -> bool:
        with self._lock:
            job = self._active
            if job is None:
                return False
            job.state = "cancelling"
            job.message = "Cancelando…"
            job._cancel.set()
            self.touch()
            return True

    def snapshot(self) -> dict[str, Any] | None:
        with self._lock:
            job = self._active or self._last
            return job.to_json() if job else None
