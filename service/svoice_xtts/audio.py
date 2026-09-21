"""Audio helpers: reference validation, conversion and WAV output."""

from __future__ import annotations

import os
import re
import subprocess
import sys
import wave
from pathlib import Path
from typing import Any

from .errors import ServiceError

ALLOWED_EXTENSIONS = {".wav", ".mp3", ".m4a", ".flac", ".ogg"}
MAX_REFERENCE_FILES = 12
MAX_REFERENCE_FILE_BYTES = 512 * 1024 * 1024
MAX_REFERENCE_DURATION_SECONDS = 30 * 60
REFERENCE_CHUNK_SECONDS = 20
REFERENCE_SAMPLE_RATE = 24000
OUTPUT_SAMPLE_RATE = 24000
FFMPEG_TIMEOUT_SECONDS = 30 * 60

_CREATION_FLAGS = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0


def ffmpeg_executable() -> str:
    try:
        import imageio_ffmpeg

        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception as error:
        raise ServiceError(
            "O conversor de áudio integrado (FFmpeg) não está disponível.",
            500,
            code="ffmpeg_missing",
            action="Execute o reparo do SVoice para restaurar o runtime.",
        ) from error


def validate_reference_sources(raw_paths: Any) -> list[Path]:
    """Validate user-supplied reference paths (existence, type, size)."""
    if not isinstance(raw_paths, list) or not raw_paths:
        raise ServiceError("Selecione pelo menos um áudio de referência.", code="no_reference")
    if len(raw_paths) > MAX_REFERENCE_FILES:
        raise ServiceError(
            f"Selecione no máximo {MAX_REFERENCE_FILES} áudios de referência.",
            code="too_many_references",
        )

    sources: list[Path] = []
    seen: set[str] = set()
    for raw_path in raw_paths:
        if not isinstance(raw_path, (str, os.PathLike)) or not str(raw_path).strip():
            raise ServiceError("Um dos caminhos de áudio é inválido.", code="invalid_path")
        text = str(raw_path).strip()
        if "\x00" in text or len(text) > 4096:
            raise ServiceError("Um dos caminhos de áudio é inválido.", code="invalid_path")
        candidate = Path(text)
        if not candidate.is_absolute():
            raise ServiceError(
                "Os áudios de referência precisam ser informados com caminho completo.",
                code="invalid_path",
            )
        try:
            source = candidate.resolve(strict=True)
        except (OSError, RuntimeError) as error:
            raise ServiceError(
                "Um dos áudios selecionados não foi encontrado.",
                code="reference_not_found",
                action="Selecione o arquivo novamente.",
            ) from error
        if not source.is_file() or source.suffix.lower() not in ALLOWED_EXTENSIONS:
            raise ServiceError(
                "Use arquivos WAV, MP3, M4A, FLAC ou OGG como referência.",
                code="unsupported_format",
            )
        try:
            size = source.stat().st_size
        except OSError as error:
            raise ServiceError(
                f"Não foi possível acessar o arquivo {source.name}.",
                code="reference_unreadable",
            ) from error
        if size <= 0:
            raise ServiceError(f"O arquivo {source.name} está vazio.", code="reference_empty")
        if size > MAX_REFERENCE_FILE_BYTES:
            raise ServiceError(
                f"O arquivo {source.name} excede {MAX_REFERENCE_FILE_BYTES // (1024 * 1024)} MB.",
                code="reference_too_large",
                action="Use um trecho menor do áudio (até 30 minutos bastam).",
            )
        key = os.path.normcase(str(source))
        if key in seen:
            continue
        seen.add(key)
        sources.append(source)
    if not sources:
        raise ServiceError("Selecione pelo menos um áudio de referência.", code="no_reference")
    return sources


def probe_audio(source: Path) -> dict[str, Any]:
    """Decode the first half second with FFmpeg to confirm the file is audio."""
    command = [
        ffmpeg_executable(),
        "-hide_banner",
        "-nostdin",
        "-i",
        str(source),
        "-map",
        "0:a:0",
        "-t",
        "0.5",
        "-f",
        "null",
        "-",
    ]
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=120,
            creationflags=_CREATION_FLAGS,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ServiceError(
            f"Não foi possível analisar o áudio {source.name}.",
            code="reference_invalid",
        ) from error
    stream = re.search(r"Stream #\d+:\d+.*?: Audio: (?P<codec>[^,\n]+)(?:.*?, (?P<rate>\d+) Hz)?", result.stderr)
    if result.returncode != 0 or stream is None:
        detail = [line for line in result.stderr.strip().splitlines() if line.strip()]
        reason = detail[-1] if detail else "formato não reconhecido"
        raise ServiceError(
            f"O arquivo {source.name} não contém áudio válido: {reason}",
            code="reference_invalid",
            action="Use uma gravação em WAV, MP3, M4A, FLAC ou OGG.",
        )
    duration = re.search(r"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)", result.stderr)
    seconds = None
    if duration:
        hours, minutes, secs = duration.groups()
        seconds = int(hours) * 3600 + int(minutes) * 60 + float(secs)
    return {
        "codec": stream.group("codec").strip(),
        "sample_rate": int(stream.group("rate")) if stream.group("rate") else None,
        "duration_seconds": seconds,
    }


def audio_duration(path: Path) -> float:
    if path.suffix.lower() == ".wav":
        try:
            with wave.open(str(path), "rb") as audio:
                return audio.getnframes() / float(audio.getframerate())
        except (wave.Error, OSError, ZeroDivisionError, EOFError):
            pass
    try:
        from mutagen import File as MutagenFile

        metadata = MutagenFile(path)
        if metadata is not None and metadata.info is not None:
            return float(metadata.info.length)
    except Exception:
        pass
    try:
        return float(probe_audio(path).get("duration_seconds") or 0.0)
    except ServiceError:
        return 0.0


def split_reference_audio(
    source: Path,
    processing_dir: Path,
    source_index: int,
    maximum_seconds: float,
) -> list[Path]:
    """Convert to mono 24 kHz PCM16 and split into 20-second chunks."""
    output_pattern = processing_dir / f"source_{source_index:04d}_%04d.wav"
    command = [
        ffmpeg_executable(),
        "-hide_banner",
        "-loglevel",
        "error",
        "-nostdin",
        "-y",
        "-i",
        str(source),
        "-map",
        "0:a:0",
        "-vn",
        "-t",
        f"{maximum_seconds:.3f}",
        "-ac",
        "1",
        "-ar",
        str(REFERENCE_SAMPLE_RATE),
        "-c:a",
        "pcm_s16le",
        "-f",
        "segment",
        "-segment_time",
        str(REFERENCE_CHUNK_SECONDS),
        "-reset_timestamps",
        "1",
        str(output_pattern),
    ]
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=FFMPEG_TIMEOUT_SECONDS,
            creationflags=_CREATION_FLAGS,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ServiceError(
            f"Não foi possível processar o áudio {source.name}.",
            code="reference_processing_failed",
        ) from error
    if result.returncode != 0:
        detail = result.stderr.strip().splitlines()
        reason = detail[-1] if detail else "formato não reconhecido"
        raise ServiceError(
            f"Não foi possível processar {source.name}: {reason}",
            code="reference_processing_failed",
            action="Use uma gravação em WAV, MP3, M4A, FLAC ou OGG.",
        )
    return sorted(processing_dir.glob(f"source_{source_index:04d}_*.wav"))


def write_wav_pcm16(path: Path, samples: Any, sample_rate: int = OUTPUT_SAMPLE_RATE) -> None:
    """Write a float32 mono numpy array as 16-bit PCM WAV."""
    import numpy as np

    array = np.asarray(samples, dtype=np.float32).reshape(-1)
    array = np.clip(array, -1.0, 1.0)
    pcm = (array * 32767.0).astype("<i2")
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(int(sample_rate))
        output.writeframes(pcm.tobytes())


def wav_info(path: Path) -> tuple[int, int, float]:
    """Return (channels, sample_rate, duration_seconds) of a WAV file."""
    with wave.open(str(path), "rb") as audio:
        frames = audio.getnframes()
        rate = audio.getframerate()
        return audio.getnchannels(), rate, frames / float(rate)
