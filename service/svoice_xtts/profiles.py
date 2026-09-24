"""Voice profile registry with migration and validation.

Profiles created by earlier SVoice versions (Flutter app or widget 0.3.x)
use the same directory layout and are picked up without any conversion. The
registry gains a ``schema_version`` and per-profile validation status; a
backup of ``profiles.json`` is written before the file is rewritten by the
migration.
"""

from __future__ import annotations

import json
import re
import shutil
import threading
import time
import uuid
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Callable

from . import audio
from .errors import ServiceError
from .logging_setup import get_logger
from .paths import DATA_SCHEMA_VERSION, DataPaths

log = get_logger("profiles")

MAX_NAME_LENGTH = 80
CONDITIONING_FILE = "conditioning.pt"


def _registry_payload(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict) or not isinstance(value.get("profiles"), list):
        raise ValueError("registro de perfis não é um objeto válido")
    return value


def read_json(path: Path, default: dict[str, Any]) -> dict[str, Any]:
    """Read a non-critical JSON object such as config.json.

    The profile registry deliberately uses the stricter ``_registry_payload``
    path so a transient read failure can never erase cloned voices.
    """
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else default
    except (OSError, ValueError):
        return default


def write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def sanitize_name(requested: Any, fallback: str) -> str:
    raw = "" if requested is None else str(requested)
    name = re.sub(r"[\x00-\x1f\x7f]", "", raw)
    name = re.sub(r"\s+", " ", name).strip(" .")
    if not name:
        name = re.sub(r"\s+", " ", fallback).strip(" .")
    if not name:
        name = "Voz clonada"
    return name[:MAX_NAME_LENGTH].rstrip(" .") or "Voz clonada"


class ProfileRegistry:
    def __init__(self, paths: DataPaths):
        self.paths = paths
        self._lock = threading.RLock()
        self._data, self._recovered_from = self._load_registry()
        self.migration_report = self._migrate()

    # ----------------------------------------------------------------- migration
    def _migrate(self) -> dict[str, Any]:
        """Idempotent upgrade of the registry to the current schema."""
        report: dict[str, Any] = {
            "schema_before": int(self._data.get("schema_version", 1) or 1),
            "schema_after": DATA_SCHEMA_VERSION,
            "profiles": len(self._data.get("profiles", [])),
            "changed": False,
            "backup": None,
            "recovered_from": self._recovered_from,
            "invalid_profiles": [],
        }
        changed = report["schema_before"] != DATA_SCHEMA_VERSION or self._recovered_from is not None
        voices_dir = self.paths.voices_dir
        kept: list[dict[str, Any]] = []
        for raw in self._data.get("profiles", []):
            if not isinstance(raw, dict) or not isinstance(raw.get("id"), str) or not raw["id"]:
                report["invalid_profiles"].append({"reason": "registro sem id"})
                changed = True
                continue
            profile = dict(raw)
            profile_id = profile["id"]
            profile["name"] = sanitize_name(profile.get("name"), profile_id[:8])
            if profile["name"] != raw.get("name"):
                changed = True
            references = profile.get("reference_paths")
            if not isinstance(references, list):
                references = []
                changed = True
            normalized: list[str] = []
            for reference in references:
                if not isinstance(reference, str) or not reference:
                    changed = True
                    continue
                candidate = Path(reference)
                # Data directory may have moved (other user name/drive): keep the
                # file name and re-anchor it on the current voices directory.
                if not candidate.is_absolute() or not candidate.is_file():
                    relocated = voices_dir / profile_id / candidate.name
                    if relocated.is_file():
                        candidate = relocated
                        changed = True
                normalized.append(str(candidate))
            if not normalized:
                # Registry lost its references but the folder may still hold them.
                discovered = sorted((voices_dir / profile_id).glob("reference_*.wav"))
                normalized = [str(path) for path in discovered]
                if normalized:
                    changed = True
            profile["reference_paths"] = normalized
            profile.setdefault("source_count", 1)
            profile.setdefault("truncated", False)
            profile.setdefault("created_at", datetime.now(UTC).isoformat())
            try:
                profile["duration_seconds"] = round(float(profile.get("duration_seconds", 0.0)), 3)
            except (TypeError, ValueError):
                profile["duration_seconds"] = 0.0
                changed = True
            try:
                profile["input_duration_seconds"] = round(
                    float(profile.get("input_duration_seconds", profile["duration_seconds"])),
                    3,
                )
            except (TypeError, ValueError):
                profile["input_duration_seconds"] = profile["duration_seconds"]
                changed = True
            kept.append(profile)
        self._data["profiles"] = kept
        if changed:
            self._data["schema_version"] = DATA_SCHEMA_VERSION
            self._data["migrated_at"] = datetime.now(UTC).isoformat()
            report["backup"] = self._backup_registry()
            write_json(self.paths.registry_path, self._data)
            report["changed"] = True
            log.info("Registro de perfis migrado: %s", {k: v for k, v in report.items() if k != "invalid_profiles"})
        return report

    def _load_registry(self) -> tuple[dict[str, Any], str | None]:
        source = self.paths.registry_path
        if not source.exists():
            return {"profiles": []}, None

        last_error: Exception | None = None
        payload: dict[str, Any] | None = None
        # Atomic replacement and antivirus scanning can briefly make the file
        # unreadable. Never turn that transient condition into an empty registry.
        for attempt in range(6):
            try:
                payload = _registry_payload(source)
                break
            except (OSError, ValueError) as error:
                last_error = error
                if attempt < 5:
                    time.sleep(0.05 * (attempt + 1))

        if payload is not None and payload["profiles"]:
            return payload, None

        recovered = self._recover_from_backups()
        if recovered is not None:
            recovered_payload, backup = recovered
            return recovered_payload, str(backup)

        if payload is not None:
            # A genuinely empty registry is valid when no orphaned voice data
            # exists. Deleting a profile also deletes its directory, so this
            # does not resurrect profiles intentionally removed by the user.
            return payload, None

        raise ServiceError(
            "O registro de perfis de voz está ilegível e nenhum backup válido pôde ser recuperado.",
            code="profile_registry_corrupted",
            action="Preserve profiles.json e use o reparo do SVoice ou restaure um backup da pasta backups.",
        ) from last_error

    def _recover_from_backups(self) -> tuple[dict[str, Any], Path] | None:
        try:
            orphan_ids = {
                directory.name
                for directory in self.paths.voices_dir.iterdir()
                if directory.is_dir() and any(directory.glob("reference_*.wav"))
            }
        except OSError:
            orphan_ids = set()
        if not orphan_ids:
            return None

        try:
            backups = sorted(
                self.paths.backups_dir.glob("profiles-*.json"),
                key=lambda path: path.stat().st_mtime,
                reverse=True,
            )
        except OSError:
            return None

        for backup in backups:
            try:
                candidate = _registry_payload(backup)
            except (OSError, ValueError):
                continue
            matching = [
                dict(profile)
                for profile in candidate["profiles"]
                if isinstance(profile, dict) and profile.get("id") in orphan_ids
            ]
            if matching:
                recovered = dict(candidate)
                recovered["profiles"] = matching
                log.warning("Recuperando %d perfil(is) órfão(s) a partir de %s", len(matching), backup)
                return recovered, backup
        return None

    def _backup_registry(self) -> str | None:
        source = self.paths.registry_path
        if not source.is_file():
            return None
        self.paths.backups_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d-%H%M%S-%f")
        destination = self.paths.backups_dir / f"profiles-{stamp}.json"
        try:
            shutil.copy2(source, destination)
        except OSError:
            return None
        # Keep only the ten most recent backups.
        backups = sorted(self.paths.backups_dir.glob("profiles-*.json"))
        for stale in backups[:-10]:
            try:
                stale.unlink()
            except OSError:
                pass
        return str(destination)

    # ------------------------------------------------------------------ queries
    def _find(self, profile_id: str) -> dict[str, Any] | None:
        return next((item for item in self._data["profiles"] if item.get("id") == profile_id), None)

    def get(self, profile_id: str) -> dict[str, Any]:
        with self._lock:
            profile = self._find(profile_id)
            if profile is None:
                raise ServiceError(
                    "Perfil de voz não encontrado.",
                    404,
                    code="profile_not_found",
                    action="Atualize a lista de vozes.",
                )
            return dict(profile)

    def public(self, profile: dict[str, Any]) -> dict[str, Any]:
        references = [Path(path) for path in profile.get("reference_paths", [])]
        missing = sum(1 for path in references if not path.is_file())
        conditioning = (self.paths.voices_dir / profile["id"] / CONDITIONING_FILE).is_file()
        status = "ok"
        if references and missing == len(references):
            status = "missing_references"
        elif missing:
            status = "partial_references"
        elif not references:
            status = "missing_references"
        return {
            "id": profile["id"],
            "name": profile["name"],
            "reference_count": len(references),
            "missing_references": missing,
            "duration_seconds": float(profile.get("duration_seconds", 0.0)),
            "input_duration_seconds": float(profile.get("input_duration_seconds", profile.get("duration_seconds", 0.0))),
            "source_count": int(profile.get("source_count", 1)),
            "truncated": bool(profile.get("truncated", False)),
            "created_at": profile.get("created_at"),
            "conditioning_cached": conditioning,
            "status": status,
        }

    def list(self) -> list[dict[str, Any]]:
        with self._lock:
            return [self.public(profile) for profile in self._data["profiles"]]

    def usable_references(self, profile: dict[str, Any]) -> list[str]:
        return [path for path in profile.get("reference_paths", []) if Path(path).is_file()]

    # ---------------------------------------------------------------- mutations
    def available_name(self, requested: Any, fallback: str, *, exclude_id: str | None = None) -> str:
        name = sanitize_name(requested, fallback)
        with self._lock:
            existing = {
                str(item.get("name", "")).casefold()
                for item in self._data["profiles"]
                if item.get("id") != exclude_id
            }
        if name.casefold() not in existing:
            return name
        index = 2
        while True:
            suffix = f" ({index})"
            candidate = f"{name[: MAX_NAME_LENGTH - len(suffix)].rstrip()}{suffix}"
            if candidate.casefold() not in existing:
                return candidate
            index += 1

    def create(
        self,
        payload: dict[str, Any],
        *,
        progress: Callable[[str, float | None], None] | None = None,
        check_cancelled: Callable[[], None] | None = None,
    ) -> dict[str, Any]:
        sources = audio.validate_reference_sources(payload.get("reference_paths"))
        notify = progress or (lambda message, value: None)
        cancelled = check_cancelled or (lambda: None)

        notify("Verificando os áudios de referência…", 0.0)
        original_duration = 0.0
        for source in sources:
            cancelled()
            audio.probe_audio(source)
            original_duration += max(0.0, audio.audio_duration(source))

        if original_duration > 0:
            required = audio.processing_space_required(original_duration)
            try:
                available = shutil.disk_usage(self.paths.temp_dir).free
            except OSError:
                available = required
            if available < required:
                raise ServiceError(
                    "Não há espaço livre suficiente para preparar estes áudios.",
                    code="insufficient_disk_space",
                    action=f"Libere pelo menos {required / (1024 ** 3):.1f} GB e tente novamente.",
                )

        name = self.available_name(payload.get("name"), sources[0].stem)
        profile_id = uuid.uuid4().hex
        profile_dir = self.paths.voices_dir / profile_id
        processing_dir = self.paths.temp_dir / f"import_{profile_id}"
        profile_dir.mkdir(parents=True, exist_ok=False)
        processing_dir.mkdir(parents=True, exist_ok=False)
        processed: list[str] = []
        total_duration = 0.0
        try:
            chunk_number = 0
            for source_index, source in enumerate(sources):
                cancelled()
                notify(
                    f"Detectando fala e cortando o áudio {source_index + 1} de {len(sources)}…",
                    0.1 + 0.6 * (source_index / max(1, len(sources))),
                )
                chunks = audio.split_reference_audio(
                    source,
                    processing_dir,
                    source_index,
                    check_cancelled=cancelled,
                )
                if not chunks:
                    raise ServiceError(
                        f"Não foi possível ler o áudio {source.name}.",
                        code="reference_invalid",
                    )
                for chunk in chunks:
                    duration = audio.audio_duration(chunk)
                    if duration <= 0:
                        continue
                    chunk_number += 1
                    destination = profile_dir / f"reference_{chunk_number:04d}.wav"
                    shutil.move(str(chunk), destination)
                    processed.append(str(destination))
                    total_duration += duration

            if total_duration < 3:
                raise ServiceError(
                    "O áudio de referência precisa ter pelo menos 3 segundos. "
                    "Para melhor qualidade, utilize de 10 a 30 segundos.",
                    code="reference_too_short",
                )

            profile = {
                "id": profile_id,
                "name": name,
                "reference_paths": processed,
                "duration_seconds": round(total_duration, 3),
                "input_duration_seconds": round(original_duration, 3),
                "source_count": len(sources),
                "truncated": False,
                "created_at": datetime.now(UTC).isoformat(),
            }
            with self._lock:
                self._data["profiles"].append(profile)
                self._data["schema_version"] = DATA_SCHEMA_VERSION
                write_json(self.paths.registry_path, self._data)
            notify("Áudios de referência preparados", 0.7)
            log.info("Perfil criado: %s (%d trechos, %.1f s)", profile_id, len(processed), total_duration)
            return dict(profile)
        except Exception:
            shutil.rmtree(profile_dir, ignore_errors=True)
            raise
        finally:
            shutil.rmtree(processing_dir, ignore_errors=True)

    def rename(self, profile_id: str, requested_name: Any) -> dict[str, Any]:
        with self._lock:
            profile = self._find(profile_id)
            if profile is None:
                raise ServiceError("Perfil de voz não encontrado.", 404, code="profile_not_found")
            if requested_name is None or not str(requested_name).strip():
                raise ServiceError("Informe um nome para a voz.", code="invalid_name")
            profile["name"] = self.available_name(requested_name, profile["name"], exclude_id=profile_id)
            write_json(self.paths.registry_path, self._data)
            return self.public(profile)

    def delete(self, profile_id: str) -> None:
        with self._lock:
            profile = self._find(profile_id)
            if profile is None:
                raise ServiceError("Perfil de voz não encontrado.", 404, code="profile_not_found")
            self._data["profiles"] = [item for item in self._data["profiles"] if item.get("id") != profile_id]
            write_json(self.paths.registry_path, self._data)
        target = self.paths.voices_dir / profile_id
        if target.resolve().parent == self.paths.voices_dir.resolve():
            shutil.rmtree(target, ignore_errors=True)
        log.info("Perfil removido: %s", profile_id)

def cleanup_temp_files(paths: DataPaths, *, max_age_seconds: float = 0.0) -> int:
    """Remove leftover import folders and utterances from the temp directory."""
    removed = 0
    now = time.time()
    try:
        entries = list(paths.temp_dir.iterdir())
    except OSError:
        return 0
    for path in entries:
        try:
            age = now - path.stat().st_mtime
            if max_age_seconds > 0 and age < max_age_seconds:
                continue
            if path.is_dir() and path.name.startswith("import_"):
                shutil.rmtree(path)
                removed += 1
            elif path.is_file() and path.suffix.lower() in {".wav", ".part", ".tmp"}:
                path.unlink()
                removed += 1
        except OSError:
            pass
    return removed
