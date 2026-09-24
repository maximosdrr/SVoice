"""Audio helpers: reference validation, conversion and WAV output."""

from __future__ import annotations

import os
import re
import subprocess
import sys
import threading
import time
import wave
from pathlib import Path
from typing import Any, Callable

from .errors import ServiceError

ALLOWED_EXTENSIONS = {".wav", ".mp3", ".m4a", ".flac", ".ogg"}
MAX_REFERENCE_FILES = 12
MAX_REFERENCE_FILE_BYTES = 512 * 1024 * 1024
REFERENCE_CHUNK_SECONDS = 30
REFERENCE_TARGET_SECONDS = 25
REFERENCE_MIN_CHUNK_SECONDS = 3
REFERENCE_SAMPLE_RATE = 24000
OUTPUT_SAMPLE_RATE = 24000
VAD_SAMPLE_RATE = 16000
VAD_WINDOW_SECONDS = 10 * 60
VAD_WINDOW_OVERLAP_SECONDS = 2
VAD_MIN_SILENCE_MS = 350
VAD_SPEECH_PAD_MS = 200
INTER_CLIP_SILENCE_SECONDS = 0.15
FFMPEG_MIN_TIMEOUT_SECONDS = 30 * 60

_CREATION_FLAGS = getattr(subprocess, "CREATE_NO_WINDOW", 0) if sys.platform == "win32" else 0
_VAD_MODEL: Any = None
_VAD_MODEL_LOCK = threading.Lock()


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
                action="Use um arquivo menor ou compacte o áudio sem remover a fala.",
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


def processing_space_required(duration_seconds: float) -> int:
    """Conservative temporary + final PCM storage estimate for an import."""
    pcm_bytes = max(0.0, duration_seconds) * REFERENCE_SAMPLE_RATE * 2
    return int(pcm_bytes * 2.1 + 256 * 1024 * 1024)


def _ffmpeg_timeout(duration_seconds: float) -> float:
    # A timeout is still useful for a stuck decoder, but it must not act as a
    # duration limit. Four times real time leaves ample room on slower CPUs.
    return max(float(FFMPEG_MIN_TIMEOUT_SECONDS), max(0.0, duration_seconds) * 4 + 300)


def _run_ffmpeg_cancellable(
    command: list[str],
    *,
    timeout: float,
    check_cancelled: Callable[[], None],
) -> subprocess.CompletedProcess[str]:
    started = time.monotonic()
    try:
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            creationflags=_CREATION_FLAGS,
        )
    except OSError:
        raise
    try:
        while True:
            check_cancelled()
            remaining = timeout - (time.monotonic() - started)
            if remaining <= 0:
                raise subprocess.TimeoutExpired(command, timeout)
            try:
                stdout, stderr = process.communicate(timeout=min(0.25, remaining))
                return subprocess.CompletedProcess(command, process.returncode, stdout, stderr)
            except subprocess.TimeoutExpired:
                continue
    except BaseException:
        process.kill()
        process.communicate()
        raise


def _merge_intervals(intervals: list[tuple[float, float]], duration: float) -> list[tuple[float, float]]:
    normalized = sorted(
        (max(0.0, float(start)), min(duration, float(end)))
        for start, end in intervals
        if float(end) - float(start) >= 0.05
    )
    merged: list[list[float]] = []
    for start, end in normalized:
        if not merged or start > merged[-1][1] + 0.05:
            merged.append([start, end])
        else:
            merged[-1][1] = max(merged[-1][1], end)
    return [(start, end) for start, end in merged if end - start >= 0.05]


def _get_vad_model() -> Any:
    global _VAD_MODEL
    with _VAD_MODEL_LOCK:
        if _VAD_MODEL is None:
            from silero_vad import load_silero_vad

            # The sequence ONNX model runs on CPU and is shipped in the base
            # runtime. It is independent of the XTTS CUDA/DirectML backend.
            _VAD_MODEL = load_silero_vad(sequence=True, sampling_rate=VAD_SAMPLE_RATE)
        return _VAD_MODEL


def _silero_speech_intervals(
    canonical_wav: Path,
    check_cancelled: Callable[[], None],
) -> list[tuple[float, float]]:
    import numpy as np
    import soundfile
    from scipy.signal import resample_poly
    from silero_vad import get_speech_timestamps_sequence

    model = _get_vad_model()
    intervals: list[tuple[float, float]] = []
    with soundfile.SoundFile(canonical_wav) as source:
        sample_rate = int(source.samplerate)
        if source.channels != 1 or sample_rate != REFERENCE_SAMPLE_RATE:
            raise ValueError("o áudio temporário não está em mono/24 kHz")
        total_frames = len(source)
        duration = total_frames / float(sample_rate)
        window_frames = int(VAD_WINDOW_SECONDS * sample_rate)
        overlap_frames = int(VAD_WINDOW_OVERLAP_SECONDS * sample_rate)
        step_frames = max(1, window_frames - overlap_frames)

        for first_frame in range(0, total_frames, step_frames):
            check_cancelled()
            source.seek(first_frame)
            samples = source.read(min(window_frames, total_frames - first_frame), dtype="float32", always_2d=False)
            if samples.size == 0:
                break
            samples_16k = resample_poly(samples, 2, 3).astype(np.float32, copy=False)
            timestamps = get_speech_timestamps_sequence(
                samples_16k,
                model,
                sampling_rate=VAD_SAMPLE_RATE,
                threshold=0.5,
                min_speech_duration_ms=250,
                max_speech_duration_s=float(REFERENCE_CHUNK_SECONDS),
                min_silence_duration_ms=VAD_MIN_SILENCE_MS,
                speech_pad_ms=VAD_SPEECH_PAD_MS,
                return_seconds=True,
            )
            offset = first_frame / float(sample_rate)
            for item in timestamps:
                intervals.append((offset + float(item["start"]), offset + float(item["end"])))
            if first_frame + window_frames >= total_frames:
                break
    return _merge_intervals(intervals, duration)


def _ffmpeg_speech_intervals(
    canonical_wav: Path,
    check_cancelled: Callable[[], None],
) -> list[tuple[float, float]]:
    """Energy-based fallback when the packaged VAD cannot run."""
    duration = audio_duration(canonical_wav)
    command = [
        ffmpeg_executable(),
        "-hide_banner",
        "-nostdin",
        "-i",
        str(canonical_wav),
        "-af",
        f"silencedetect=noise=-40dB:d={VAD_MIN_SILENCE_MS / 1000:.3f}",
        "-f",
        "null",
        "-",
    ]
    try:
        result = _run_ffmpeg_cancellable(
            command,
            timeout=_ffmpeg_timeout(duration),
            check_cancelled=check_cancelled,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ServiceError(
            "Não foi possível detectar as regiões de fala do áudio.",
            code="reference_processing_failed",
        ) from error
    if result.returncode != 0:
        raise ServiceError(
            "Não foi possível detectar as regiões de fala do áudio.",
            code="reference_processing_failed",
        )

    events = re.findall(r"silence_(start|end):\s*(-?\d+(?:\.\d+)?)", result.stderr)
    silent: list[tuple[float, float]] = []
    silence_start: float | None = None
    for kind, raw_value in events:
        value = max(0.0, min(duration, float(raw_value)))
        if kind == "start":
            silence_start = value
        elif silence_start is not None:
            silent.append((silence_start, value))
            silence_start = None
    if silence_start is not None:
        silent.append((silence_start, duration))

    speech: list[tuple[float, float]] = []
    cursor = 0.0
    padding = VAD_SPEECH_PAD_MS / 1000.0
    for start, end in silent:
        if start > cursor + 0.05:
            speech.append((max(0.0, cursor - padding), min(duration, start + padding)))
        cursor = max(cursor, end)
    if cursor < duration - 0.05:
        speech.append((max(0.0, cursor - padding), duration))
    return _merge_intervals(speech, duration)


def detect_speech_intervals(
    canonical_wav: Path,
    *,
    check_cancelled: Callable[[], None] | None = None,
) -> list[tuple[float, float]]:
    cancelled = check_cancelled or (lambda: None)
    try:
        return _silero_speech_intervals(canonical_wav, cancelled)
    except Exception as error:
        # The runtime self-test normally catches a missing VAD dependency. Keep
        # an FFmpeg fallback so a damaged optional detector does not strand the
        # user's import.
        if isinstance(error, ServiceError):
            raise
        cancelled()
        return _ffmpeg_speech_intervals(canonical_wav, cancelled)


def _split_long_intervals(intervals: list[tuple[float, float]]) -> list[tuple[float, float]]:
    pieces: list[tuple[float, float]] = []
    maximum = float(REFERENCE_CHUNK_SECONDS)
    for start, end in intervals:
        cursor = start
        while end - cursor > maximum:
            pieces.append((cursor, cursor + maximum))
            cursor += maximum
        if end - cursor >= 0.05:
            pieces.append((cursor, end))
    return pieces


def _clip_duration(clips: list[tuple[float, float]]) -> float:
    if not clips:
        return 0.0
    return sum(end - start for start, end in clips) + INTER_CLIP_SILENCE_SECONDS * (len(clips) - 1)


def group_speech_intervals(intervals: list[tuple[float, float]]) -> list[list[tuple[float, float]]]:
    """Pack speech spans into similarly sized references without long gaps."""
    groups: list[list[tuple[float, float]]] = []
    current: list[tuple[float, float]] = []
    for clip in _split_long_intervals(intervals):
        candidate = [*current, clip]
        candidate_duration = _clip_duration(candidate)
        current_duration = _clip_duration(current)
        if current and (
            candidate_duration > REFERENCE_CHUNK_SECONDS
            or (current_duration >= 8 and candidate_duration > REFERENCE_TARGET_SECONDS)
        ):
            groups.append(current)
            current = [clip]
        else:
            current = candidate
    if current:
        groups.append(current)

    # The 25-second target deliberately leaves room to absorb a short tail.
    if len(groups) > 1 and _clip_duration(groups[-1]) < REFERENCE_MIN_CHUNK_SECONDS:
        combined = [*groups[-2], *groups[-1]]
        if _clip_duration(combined) <= REFERENCE_CHUNK_SECONDS:
            groups[-2] = combined
            groups.pop()
    return groups


def _write_reference_chunk(
    canonical_wav: Path,
    output: Path,
    clips: list[tuple[float, float]],
) -> None:
    temporary = output.with_suffix(".wav.part")
    with wave.open(str(canonical_wav), "rb") as source:
        if source.getnchannels() != 1 or source.getsampwidth() != 2 or source.getframerate() != REFERENCE_SAMPLE_RATE:
            raise ServiceError("O áudio temporário está em um formato inesperado.", code="reference_processing_failed")
        maximum_frames = REFERENCE_CHUNK_SECONDS * REFERENCE_SAMPLE_RATE
        silence = b"\x00\x00" * int(INTER_CLIP_SILENCE_SECONDS * REFERENCE_SAMPLE_RATE)
        written = 0
        with wave.open(str(temporary), "wb") as destination:
            destination.setnchannels(1)
            destination.setsampwidth(2)
            destination.setframerate(REFERENCE_SAMPLE_RATE)
            for index, (start, end) in enumerate(clips):
                if index and written < maximum_frames:
                    separator = silence[: max(0, maximum_frames - written) * 2]
                    destination.writeframes(separator)
                    written += len(separator) // 2
                first_frame = max(0, int(round(start * REFERENCE_SAMPLE_RATE)))
                frame_count = max(0, int(round((end - start) * REFERENCE_SAMPLE_RATE)))
                frame_count = min(frame_count, maximum_frames - written)
                if frame_count <= 0:
                    break
                source.setpos(min(first_frame, source.getnframes()))
                frames = source.readframes(frame_count)
                destination.writeframes(frames)
                written += len(frames) // 2
    temporary.replace(output)


def split_reference_audio(
    source: Path,
    processing_dir: Path,
    source_index: int,
    *,
    check_cancelled: Callable[[], None] | None = None,
) -> list[Path]:
    """Convert to mono PCM16 and split on detected pauses (up to 30 seconds)."""
    cancelled = check_cancelled or (lambda: None)
    processing_dir.mkdir(parents=True, exist_ok=True)
    canonical = processing_dir / f"source_{source_index:04d}_canonical.wav"
    duration_hint = audio_duration(source)
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
        "-ac",
        "1",
        "-ar",
        str(REFERENCE_SAMPLE_RATE),
        "-c:a",
        "pcm_s16le",
        str(canonical),
    ]
    try:
        result = _run_ffmpeg_cancellable(
            command,
            timeout=_ffmpeg_timeout(duration_hint),
            check_cancelled=cancelled,
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
    try:
        cancelled()
        duration = audio_duration(canonical)
        intervals = detect_speech_intervals(canonical, check_cancelled=cancelled)
        groups = group_speech_intervals(_merge_intervals(intervals, duration))
        if not groups:
            raise ServiceError(
                f"Nenhuma fala foi detectada em {source.name}.",
                code="reference_no_speech",
                action="Use uma gravação com voz clara, sem música ou silêncio prolongado.",
            )
        outputs: list[Path] = []
        for chunk_index, clips in enumerate(groups):
            cancelled()
            output = processing_dir / f"source_{source_index:04d}_{chunk_index:04d}.wav"
            _write_reference_chunk(canonical, output, clips)
            if audio_duration(output) >= 0.33:
                outputs.append(output)
            else:
                output.unlink(missing_ok=True)
        return outputs
    finally:
        canonical.unlink(missing_ok=True)


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
