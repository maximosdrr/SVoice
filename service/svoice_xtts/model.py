"""XTTS v2 model files: verification, download and integrity.

The files are the official Coqui XTTS-v2.0.3 release published on Hugging
Face (``coqui/XTTS-v2``). They are pinned by SHA-256 so that a corrupted or
partially downloaded model is detected and repaired instead of failing later
inside PyTorch. The on-disk layout is the one used by ``coqui-tts`` so that
models downloaded by earlier SVoice versions are reused as-is.
"""

from __future__ import annotations

import hashlib
import json
import os
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from .errors import CancelledError, ServiceError
from .logging_setup import get_logger
from .paths import free_space_bytes

log = get_logger("model")

MODEL_DIR_NAME = "tts_models--multilingual--multi-dataset--xtts_v2"
MODEL_LICENSE = "Coqui Public Model License 1.0.0 (CPML) — uso não comercial"
MODEL_LICENSE_URL = "https://huggingface.co/coqui/XTTS-v2/blob/main/LICENSE.txt"
MODEL_SOURCE = "https://huggingface.co/coqui/XTTS-v2"
MODEL_REVISION = "main"


@dataclass(frozen=True)
class ModelFile:
    name: str
    size: int
    sha256: str

    @property
    def url(self) -> str:
        return f"{MODEL_SOURCE}/resolve/{MODEL_REVISION}/{self.name}"


MODEL_FILES: tuple[ModelFile, ...] = (
    ModelFile("model.pth", 1867929118, "c7ea20001c6a0a841c77e252d8409f6a74fb423e79b3206a0771ba5989776187"),
    ModelFile("config.json", 4368, "ef262b1454dd2a77e1461b0b2cd53e19b8a7624cc131b837d36df67356bc75e8"),
    ModelFile("vocab.json", 361219, "928260878a59da8a72a2a5b7687fea29d5106137669d90945430fe17e415304a"),
    ModelFile("speakers_xtts.pth", 7754818, "f0f6137c19a4eab0cbbe4c99b5babacf68b1746e50da90807708c10e645b943b"),
    ModelFile("hash.md5", 32, "ef2e25fc4639bb81c6c5048740c1ee8268606a160548abaa2e2fe03b12278c54"),
)

TOTAL_MODEL_BYTES = sum(file.size for file in MODEL_FILES)
VERIFICATION_CACHE = ".svoice-verified.json"
CHUNK_SIZE = 1024 * 1024


def model_directory(models_dir: Path) -> Path:
    return models_dir / "tts" / MODEL_DIR_NAME


def sha256_of(path: Path, progress: Callable[[int], None] | None = None) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            chunk = stream.read(CHUNK_SIZE)
            if not chunk:
                break
            digest.update(chunk)
            if progress:
                progress(len(chunk))
    return digest.hexdigest()


@dataclass
class ModelStatus:
    ready: bool
    directory: str
    missing: list[str]
    corrupted: list[str]
    verified_at: str | None
    total_bytes: int = TOTAL_MODEL_BYTES

    def to_json(self) -> dict:
        return {
            "ready": self.ready,
            "directory": self.directory,
            "missing": self.missing,
            "corrupted": self.corrupted,
            "verified_at": self.verified_at,
            "total_bytes": self.total_bytes,
            "license": MODEL_LICENSE,
            "license_url": MODEL_LICENSE_URL,
            "source": MODEL_SOURCE,
        }


def _fingerprint(directory: Path) -> dict[str, list[int]]:
    result: dict[str, list[int]] = {}
    for file in MODEL_FILES:
        path = directory / file.name
        try:
            stat = path.stat()
            result[file.name] = [int(stat.st_size), int(stat.st_mtime_ns)]
        except OSError:
            result[file.name] = [-1, -1]
    return result


def _read_verification(directory: Path) -> dict | None:
    try:
        payload = json.loads((directory / VERIFICATION_CACHE).read_text(encoding="utf-8"))
        return payload if isinstance(payload, dict) else None
    except (OSError, ValueError):
        return None


def _write_verification(directory: Path) -> str:
    verified_at = time.strftime("%Y-%m-%dT%H:%M:%S%z")
    payload = {"fingerprint": _fingerprint(directory), "verified_at": verified_at}
    (directory / VERIFICATION_CACHE).write_text(json.dumps(payload), encoding="utf-8")
    return verified_at


def check_model(models_dir: Path, *, full_hash: bool = False) -> ModelStatus:
    """Quick (size) or full (SHA-256) verification of the model files."""
    directory = model_directory(models_dir)
    missing: list[str] = []
    corrupted: list[str] = []
    for file in MODEL_FILES:
        path = directory / file.name
        if not path.is_file():
            missing.append(file.name)
            continue
        if path.stat().st_size != file.size:
            corrupted.append(file.name)
    verified_at = None
    cache = _read_verification(directory)
    if cache and cache.get("fingerprint") == _fingerprint(directory):
        verified_at = cache.get("verified_at")
    if full_hash and not missing and not corrupted:
        for file in MODEL_FILES:
            if sha256_of(directory / file.name) != file.sha256:
                corrupted.append(file.name)
        if not corrupted:
            verified_at = _write_verification(directory)
        else:
            try:
                (directory / VERIFICATION_CACHE).unlink()
            except OSError:
                pass
            verified_at = None
    ready = not missing and not corrupted and (verified_at is not None or not full_hash)
    return ModelStatus(ready, str(directory), missing, corrupted, verified_at)


ProgressCallback = Callable[[str, int, int], None]


def ensure_model(
    models_dir: Path,
    *,
    progress: ProgressCallback | None = None,
    cancel: Callable[[], bool] | None = None,
    allow_download: bool = True,
) -> ModelStatus:
    """Verify the model, downloading missing or corrupted files.

    ``progress(stage, done_bytes, total_bytes)`` is called during hashing and
    downloading. ``cancel()`` returning ``True`` aborts the operation.
    """
    directory = model_directory(models_dir)
    directory.mkdir(parents=True, exist_ok=True)
    status = check_model(models_dir, full_hash=True)
    if status.ready:
        return status
    if not allow_download:
        raise ServiceError(
            "O modelo XTTS v2 está ausente ou incompleto e o download está desativado.",
            503,
            code="model_missing",
            action="Execute o reparo do SVoice para baixar o modelo.",
        )

    pending = [file for file in MODEL_FILES if file.name in status.missing or file.name in status.corrupted]
    needed = sum(file.size for file in pending) + 64 * 1024 * 1024
    if free_space_bytes(directory) < needed:
        raise ServiceError(
            f"Espaço em disco insuficiente para baixar o modelo ({needed // (1024 * 1024)} MB necessários).",
            507,
            code="disk_full",
            action="Libere espaço na unidade do perfil do usuário e tente novamente.",
        )

    for file in pending:
        _download_file(file, directory / file.name, progress=progress, cancel=cancel)

    status = check_model(models_dir, full_hash=True)
    if not status.ready:
        raise ServiceError(
            "O modelo XTTS v2 foi baixado, mas a verificação de integridade falhou.",
            502,
            code="model_corrupted",
            action="Verifique a conexão e execute o reparo novamente.",
        )
    return status


def _download_file(
    file: ModelFile,
    destination: Path,
    *,
    progress: ProgressCallback | None,
    cancel: Callable[[], bool] | None,
) -> None:
    partial = destination.with_suffix(destination.suffix + ".part")
    resume_from = partial.stat().st_size if partial.exists() else 0
    if resume_from >= file.size:
        resume_from = 0
        partial.unlink(missing_ok=True)
    log.info("Baixando %s (%d bytes, retomando de %d)", file.name, file.size, resume_from)

    request = urllib.request.Request(file.url, headers={"User-Agent": "SVoice-XTTS-Service"})
    if resume_from:
        request.add_header("Range", f"bytes={resume_from}-")
    attempts = 0
    while True:
        attempts += 1
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                status_code = getattr(response, "status", 200)
                mode = "ab" if resume_from and status_code == 206 else "wb"
                if mode == "wb":
                    resume_from = 0
                done = resume_from
                with partial.open(mode) as output:
                    while True:
                        if cancel and cancel():
                            raise CancelledError("Download do modelo cancelado.")
                        chunk = response.read(CHUNK_SIZE)
                        if not chunk:
                            break
                        output.write(chunk)
                        done += len(chunk)
                        if progress:
                            progress(f"Baixando {file.name}", done, file.size)
            break
        except CancelledError:
            raise
        except (urllib.error.URLError, OSError, TimeoutError) as error:
            if attempts >= 3:
                raise ServiceError(
                    f"Não foi possível baixar {file.name}: {error}",
                    502,
                    code="download_failed",
                    action="Verifique a conexão com a internet e tente novamente.",
                ) from error
            time.sleep(2 * attempts)
            resume_from = partial.stat().st_size if partial.exists() else 0
            request = urllib.request.Request(file.url, headers={"User-Agent": "SVoice-XTTS-Service"})
            if resume_from:
                request.add_header("Range", f"bytes={resume_from}-")

    if partial.stat().st_size != file.size:
        partial.unlink(missing_ok=True)
        raise ServiceError(
            f"O arquivo {file.name} foi recebido com tamanho inesperado.",
            502,
            code="download_failed",
            action="Tente novamente; o download será reiniciado.",
        )
    if progress:
        progress(f"Verificando {file.name}", 0, file.size)
    hashed = 0

    def on_hash(count: int) -> None:
        nonlocal hashed
        hashed += count
        if progress:
            progress(f"Verificando {file.name}", hashed, file.size)

    if sha256_of(partial, on_hash) != file.sha256:
        partial.unlink(missing_ok=True)
        raise ServiceError(
            f"O arquivo {file.name} não corresponde ao hash esperado.",
            502,
            code="model_corrupted",
            action="O download será refeito no próximo reparo.",
        )
    os.replace(partial, destination)
