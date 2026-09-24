"""XTTS v2 engine: model loading, backend validation and synthesis."""

from __future__ import annotations

import gc
import os
import re
import sys
import time
import traceback
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Callable

from . import MODEL_NAME, audio, backends
from .backends import BackendAvailability, get_backend
from .errors import CancelledError, ServiceError
from .hardware import process_memory_bytes
from .jobs import Job
from .logging_setup import get_logger
from .model import check_model, model_directory
from .paths import DataPaths
from .profiles import CONDITIONING_FILE, ProfileRegistry

log = get_logger("engine")

CONDITIONING_CACHE_FORMAT = 3
CONDITIONING_BATCH_SECONDS = 90
GPT_CONDITIONING_CHUNK_SECONDS = 6
MIN_GPT_AUDIO_SECONDS = 0.33
MAX_TEXT_LENGTH = 1000
LANGUAGE = "pt"
TARGET_PEAK = 10 ** (-3 / 20)  # -3 dBFS
VALIDATION_SHORT_TEXT = "Olá! Este é um teste de síntese do SVoice."
VALIDATION_LONG_TEXT = (
    "A instalação foi concluída com sucesso. Agora você pode escrever uma frase, "
    "pressionar Enter e ouvir a voz clonada no Discord ou em outro aplicativo. "
    "Ação, coração, informação e conexão são palavras com acentuação em português."
)


def _configure_audio_io(torch_module: Any, torchaudio_module: Any) -> None:
    """Route torchaudio load/save through soundfile.

    torchaudio >= 2.9 delegates to torchcodec, which is not shipped in every
    runtime pack; soundfile handles the WAV references used by XTTS.
    """
    import soundfile

    def load_audio(uri: Any, frame_offset: int = 0, num_frames: int = -1, normalize: bool = True,
                   channels_first: bool = True, **kwargs: Any):
        del normalize, kwargs
        with soundfile.SoundFile(os.fspath(uri)) as audio_file:
            offset = max(0, int(frame_offset))
            if offset:
                audio_file.seek(min(offset, audio_file.frames))
            frames = -1 if num_frames is None or num_frames < 0 else int(num_frames)
            samples = audio_file.read(frames=frames, dtype="float32", always_2d=True)
            sample_rate = audio_file.samplerate
        waveform = torch_module.from_numpy(samples.copy())
        if channels_first:
            waveform = waveform.transpose(0, 1)
        return waveform, sample_rate

    def save_audio(uri: Any, source: Any, sample_rate: int, channels_first: bool = True,
                   format: str | None = None, **kwargs: Any) -> None:
        del kwargs
        samples = source.detach().float().cpu().numpy()
        if channels_first and samples.ndim == 2:
            samples = samples.transpose(1, 0)
        soundfile.write(os.fspath(uri), samples, int(sample_rate),
                        format=format.upper() if format else None, subtype="PCM_16")

    torchaudio_module.load = load_audio
    torchaudio_module.save = save_audio


def _apply_transformers_compatibility() -> None:
    try:
        import torch
        import transformers.pytorch_utils as pytorch_utils

        if not hasattr(pytorch_utils, "isin_mps_friendly"):
            pytorch_utils.isin_mps_friendly = torch.isin
    except Exception:
        pass


_SENTENCE_BOUNDARY = re.compile(r"(?<=[.!?;:…])\s+")
CHUNK_CHAR_LIMIT = 150


def split_into_chunks(text: str, limit: int) -> list[str]:
    """Split text into sentence groups of at most ``limit`` characters.

    coqui-tts only splits with spaCy (not shipped). Sentences are grouped so
    that each XTTS call stays short enough to keep cancellation responsive and
    below the model's 400-token limit; over-long sentences are wrapped on
    whitespace.
    """
    import textwrap

    limit = max(20, int(limit))
    sentences = [part.strip() for part in _SENTENCE_BOUNDARY.split(text) if part and part.strip()]
    chunks: list[str] = []
    current = ""
    for sentence in sentences:
        if len(sentence) > limit:
            if current:
                chunks.append(current)
                current = ""
            chunks.extend(
                textwrap.wrap(sentence, width=limit, break_on_hyphens=False, break_long_words=True)
            )
        elif not current:
            current = sentence
        elif len(current) + 1 + len(sentence) <= limit:
            current = f"{current} {sentence}"
        else:
            chunks.append(current)
            current = sentence
    if current:
        chunks.append(current)
    return [chunk for chunk in chunks if chunk.strip()]


def sanitize_text(text: str) -> str:
    text = text.replace("…", "...").replace(" ", " ")
    text = re.sub(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", "", text)
    text = re.sub(r"\s+", " ", text)
    return text.strip()


def friendly_error(error: Exception) -> str:
    message = str(error).strip()
    lowered = message.casefold()
    if "out of memory" in lowered or ("cuda" in lowered and "memory" in lowered):
        return "memória de vídeo insuficiente; tente o modo CPU"
    if "no kernel image" in lowered or "not compatible with the current pytorch" in lowered:
        return "esta GPU não é suportada pelo runtime CUDA instalado; use o modo CPU"
    if "ffmpeg" in lowered or "codec" in lowered:
        return "formato de áudio não suportado; tente um arquivo WAV"
    if "not implemented" in lowered or "unsupported" in lowered or "no operator" in lowered:
        return f"operação não suportada pelo backend selecionado ({message[:160]})"
    return message or error.__class__.__name__


class ConfigStore:
    """Persisted service configuration (``config.json``)."""

    DEFAULTS: dict[str, Any] = {
        "compute_mode": "auto",
        "backend_validation": {},
    }

    def __init__(self, path: Path):
        from .profiles import read_json, write_json

        self._path = path
        self._read = read_json
        self._write = write_json
        self.data: dict[str, Any] = dict(self.DEFAULTS)
        self.data.update(self._read(path, {}))
        self.data["compute_mode"] = backends.normalize_compute_mode(self.data.get("compute_mode"))
        if not isinstance(self.data.get("backend_validation"), dict):
            self.data["backend_validation"] = {}

    @property
    def compute_mode(self) -> str:
        return backends.normalize_compute_mode(self.data.get("compute_mode"))

    def save(self) -> None:
        self._write(self._path, self.data)


class XttsEngine:
    def __init__(self, paths: DataPaths, registry: ProfileRegistry, config: ConfigStore,
                 runtime_info: dict[str, Any] | None = None):
        self.paths = paths
        self.registry = registry
        self.config = config
        self.runtime_info = runtime_info or {}
        self.torch: Any = None
        self.torchaudio: Any = None
        self.model: Any = None
        self.loaded_backend: str | None = None
        self.device: Any = None
        self.availability: dict[str, BackendAvailability] = {}
        self.torch_version: str | None = None
        self.last_fallback_reason: str | None = None
        self.model_load_seconds: float | None = None

    # -------------------------------------------------------------- torch setup
    def import_torch(self) -> None:
        if self.torch is not None:
            return
        os.environ.setdefault("COQUI_TOS_AGREED", "1")
        os.environ.setdefault("TTS_HOME", str(self.paths.models_dir))
        os.environ.setdefault("COQUI_TTS_CACHE", str(self.paths.models_dir))
        _apply_transformers_compatibility()
        import torch
        import torchaudio

        _configure_audio_io(torch, torchaudio)
        self.torch = torch
        self.torchaudio = torchaudio
        self.torch_version = str(torch.__version__)
        threads = max(1, min(os.cpu_count() or 1, 8))
        try:
            torch.set_num_threads(threads)
        except Exception:
            pass
        self.availability = backends.probe_all(torch)
        log.info("PyTorch %s carregado; backends: %s", self.torch_version,
                 {k: v.available for k, v in self.availability.items()})

    # ----------------------------------------------------------- backend choice
    def validation_for(self, backend_id: str) -> dict[str, Any] | None:
        record = self.config.data.get("backend_validation", {}).get(backend_id)
        if not isinstance(record, dict):
            return None
        availability = self.availability.get(backend_id)
        fingerprint = self._fingerprint(backend_id, availability)
        if record.get("fingerprint") != fingerprint:
            return None
        return record

    def _fingerprint(self, backend_id: str, availability: BackendAvailability | None) -> str:
        parts = [
            backend_id,
            self.torch_version or "",
            (availability.device_name if availability else "") or "",
            str((availability.details if availability else {}).get("cuda_runtime", "")),
            str(self.runtime_info.get("torch_pack", "")),
            str(self.runtime_info.get("torch_pack_version", "")),
        ]
        return "|".join(parts)

    def resolve_backend(self, mode: str | None = None) -> tuple[str, str]:
        """Pick the backend for ``mode`` using availability and validations."""
        self.import_torch()
        mode = backends.normalize_compute_mode(mode or self.config.compute_mode)
        candidates = backends.candidates_for_mode(mode, self.availability)
        for backend_id in candidates:
            if backend_id == "cpu":
                return "cpu", "CPU selecionada" if mode == "cpu" else "Nenhum backend de GPU validado; usando CPU"
            record = self.validation_for(backend_id)
            if record is None:
                return backend_id, "Backend ainda não validado; será testado antes do uso"
            if record.get("ok"):
                return backend_id, "Backend validado anteriormente"
            self.last_fallback_reason = record.get("reason")
            if mode != "auto":
                raise ServiceError(
                    f"O backend {backends.BACKEND_LABELS[backend_id]} falhou na validação: {record.get('reason')}",
                    503,
                    code="backend_unavailable",
                    action="Selecione Automático ou CPU, ou refaça o teste no diagnóstico.",
                )
        return "cpu", "CPU"

    # ------------------------------------------------------------ model loading
    def load_model(self, backend_id: str, job: Job | None = None) -> None:
        self.import_torch()
        if self.model is not None and self.loaded_backend == backend_id:
            return
        self.unload()
        models_dir = self.paths.resolved_models_dir()
        status = check_model(models_dir)
        if not status.ready:
            raise ServiceError(
                "O modelo XTTS v2 não está instalado ou está incompleto.",
                503,
                code="model_missing",
                action="Abra o diagnóstico e execute o reparo para baixar o modelo.",
            )
        backend = get_backend(backend_id)
        if job:
            job.update(f"Carregando o modelo XTTS v2 ({backend.label})…")
        started = time.perf_counter()
        from TTS.tts.configs.xtts_config import XttsConfig
        from TTS.tts.models.xtts import Xtts

        directory = model_directory(models_dir)
        config = XttsConfig()
        config.load_json(str(directory / "config.json"))
        model = Xtts.init_from_config(config)
        model.load_checkpoint(config, checkpoint_dir=str(directory), eval=True)
        device = backend.device(self.torch)
        model.to(device)
        self.model = model
        self.device = device
        self.loaded_backend = backend_id
        self.model_load_seconds = round(time.perf_counter() - started, 2)
        log.info("Modelo carregado em %s (%.1f s)", backend.label, self.model_load_seconds)

    def unload(self) -> None:
        backend_id = self.loaded_backend
        self.model = None
        self.device = None
        self.loaded_backend = None
        gc.collect()
        if backend_id and self.torch is not None:
            get_backend(backend_id).empty_cache(self.torch)

    def ensure_ready(self, job: Job, mode: str | None = None) -> str:
        """Load the model on the resolved backend, validating GPUs once."""
        backend_id, _ = self.resolve_backend(mode)
        mode = backends.normalize_compute_mode(mode or self.config.compute_mode)
        if backend_id != "cpu" and self.validation_for(backend_id) is None:
            report = self.validate_backend(backend_id, job, persist=True)
            if not report["ok"]:
                if mode != "auto":
                    raise ServiceError(
                        f"{backends.BACKEND_LABELS[backend_id]} não passou no teste: {report['reason']}",
                        503,
                        code="backend_unavailable",
                        action="Selecione Automático ou CPU.",
                    )
                self.last_fallback_reason = report["reason"]
                return self.ensure_ready(job, mode)
        self.load_model(backend_id, job)
        return backend_id

    # ------------------------------------------------------------- conditioning
    def _conditioning_cache_path(self, profile_id: str) -> Path:
        return self.paths.voices_dir / profile_id / CONDITIONING_FILE

    @staticmethod
    def _conditioning_batches(references: list[str]) -> list[list[str]]:
        batches: list[list[str]] = []
        current: list[str] = []
        current_duration = 0.0
        for reference in references:
            duration = max(MIN_GPT_AUDIO_SECONDS, audio.audio_duration(Path(reference)))
            if current and current_duration + duration > CONDITIONING_BATCH_SECONDS:
                batches.append(current)
                current = []
                current_duration = 0.0
            current.append(reference)
            current_duration += duration
        if current:
            batches.append(current)
        return batches

    @staticmethod
    def _gpt_window_count(references: list[str]) -> int:
        duration = sum(max(0.0, audio.audio_duration(Path(reference))) for reference in references)
        full_windows = int(duration // GPT_CONDITIONING_CHUNK_SECONDS)
        remainder = duration - full_windows * GPT_CONDITIONING_CHUNK_SECONDS
        return max(1, full_windows + (1 if remainder >= MIN_GPT_AUDIO_SECONDS else 0))

    def _load_conditioning_cache(self, cache_path: Path) -> tuple[Any, Any]:
        try:
            payload = self.torch.load(cache_path, map_location="cpu", weights_only=False)
            latent = payload["gpt_cond_latent"]
            embedding = payload["speaker_embedding"]
            if not hasattr(latent, "numel") or not hasattr(embedding, "numel"):
                raise ValueError("tensores ausentes")
            if latent.numel() == 0 or embedding.numel() == 0:
                raise ValueError("tensores vazios")
            if not bool(self.torch.isfinite(latent).all()) or not bool(self.torch.isfinite(embedding).all()):
                raise ValueError("tensores inválidos")
            return latent.to(self.device), embedding.to(self.device)
        except Exception as error:
            raise ServiceError(
                "O cache permanente desta voz está danificado ou incompatível.",
                500,
                code="conditioning_cache_corrupted",
                action="Exclua esta voz e crie-a novamente com os áudios originais.",
            ) from error

    def _save_conditioning_cache(self, cache_path: Path, latent: Any, embedding: Any) -> None:
        temporary = cache_path.with_suffix(cache_path.suffix + ".tmp")
        try:
            self.torch.save(
                {
                    "gpt_cond_latent": latent.detach().cpu(),
                    "speaker_embedding": embedding.detach().cpu(),
                    "model": MODEL_NAME,
                    "cache_format": CONDITIONING_CACHE_FORMAT,
                    "created_at": datetime.now(UTC).isoformat(),
                    "persistent": True,
                },
                temporary,
            )
            temporary.replace(cache_path)
        finally:
            try:
                temporary.unlink()
            except OSError:
                pass

    def conditioning_for_profile(self, profile: dict[str, Any], job: Job | None = None) -> tuple[Any, Any]:
        torch = self.torch
        cache_path = self._conditioning_cache_path(profile["id"])
        if cache_path.exists():
            return self._load_conditioning_cache(cache_path)

        references = self.registry.usable_references(profile)
        if not references:
            raise ServiceError(
                "O áudio de referência deste perfil não foi encontrado.",
                404,
                code="profile_corrupted",
                action="Exclua o perfil e crie-o novamente com o áudio original.",
            )
        if job:
            job.update("Analisando as características da voz…")
        batches = self._conditioning_batches(references)
        backend = get_backend(self.loaded_backend or "cpu")
        move_to_cpu = backend.conditioning_on_cpu()
        if move_to_cpu:
            self.model.to("cpu")
        latent_sum = None
        embedding_sum = None
        latent_weight_total = 0
        embedding_weight_total = 0
        processed_references = 0
        try:
            for batch_index, batch in enumerate(batches):
                if job:
                    job.check_cancelled()
                    job.update(
                        f"Analisando a voz ({processed_references + 1}-{processed_references + len(batch)} de {len(references)})…",
                        0.75 + 0.2 * (batch_index / max(1, len(batches))),
                    )
                with torch.no_grad():
                    batch_latent, batch_embedding = self.model.get_conditioning_latents(
                        audio_path=batch,
                        max_ref_length=audio.REFERENCE_CHUNK_SECONDS,
                        gpt_cond_len=-1,
                        gpt_cond_chunk_len=GPT_CONDITIONING_CHUNK_SECONDS,
                    )
                latent_weight = self._gpt_window_count(batch)
                embedding_weight = len(batch)
                batch_latent_cpu = batch_latent.detach().cpu()
                batch_embedding_cpu = batch_embedding.detach().cpu()
                latent_sum = (
                    batch_latent_cpu * latent_weight
                    if latent_sum is None
                    else latent_sum + batch_latent_cpu * latent_weight
                )
                embedding_sum = (
                    batch_embedding_cpu * embedding_weight
                    if embedding_sum is None
                    else embedding_sum + batch_embedding_cpu * embedding_weight
                )
                latent_weight_total += latent_weight
                embedding_weight_total += embedding_weight
                processed_references += len(batch)
                del batch_latent, batch_embedding, batch_latent_cpu, batch_embedding_cpu
        finally:
            if move_to_cpu:
                self.model.to(self.device)
        if latent_sum is None or embedding_sum is None:
            raise ServiceError(
                "O XTTS não conseguiu extrair as características desta voz.",
                code="conditioning_failed",
                action="Use uma gravação com voz clara e tente novamente.",
            )
        latent = latent_sum / max(1, latent_weight_total)
        embedding = embedding_sum / max(1, embedding_weight_total)
        if job:
            job.check_cancelled()
            job.update("Salvando o cache permanente da voz…", 0.96)
        self._save_conditioning_cache(cache_path, latent, embedding)
        return latent.to(self.device), embedding.to(self.device)

    def builtin_speaker(self) -> tuple[str, Any, Any]:
        manager = getattr(self.model, "speaker_manager", None)
        speakers = getattr(manager, "speakers", None) or {}
        if not speakers:
            raise ServiceError("O modelo não contém vozes internas para o teste.", 500, code="model_corrupted")
        name = sorted(speakers)[0]
        latent, embedding = speakers[name].values()
        return name, latent.to(self.device), embedding.to(self.device)

    # ---------------------------------------------------------------- synthesis
    def split_text(self, text: str) -> list[str]:
        limit = int(self.model.tokenizer.char_limits.get(LANGUAGE, 250))
        return split_into_chunks(text, min(limit, CHUNK_CHAR_LIMIT))

    def synthesize_waveform(
        self,
        text: str,
        latent: Any,
        embedding: Any,
        *,
        speed: float = 1.0,
        job: Job | None = None,
        on_sentence: Callable[[int, int], None] | None = None,
    ) -> Any:
        torch = self.torch
        sentences = self.split_text(text)
        if not sentences:
            raise ServiceError("Digite um texto para gerar a voz.", code="empty_text")
        pieces = []
        for index, sentence in enumerate(sentences):
            if job:
                job.check_cancelled()
                job.update(f"Gerando a voz clonada ({index + 1}/{len(sentences)})…",
                           index / len(sentences))
            result = self._run_inference(sentence, latent, embedding, speed)
            waveform = result["wav"]
            if not torch.is_tensor(waveform):
                waveform = torch.tensor(waveform)
            pieces.append(waveform.detach().float().cpu().reshape(-1))
            if on_sentence:
                on_sentence(index + 1, len(sentences))
        if job:
            job.check_cancelled()
        combined = torch.cat(pieces) if len(pieces) > 1 else pieces[0]
        peak = float(combined.abs().max()) if combined.numel() else 0.0
        if peak > 0:
            combined = combined * (TARGET_PEAK / peak)
        return combined

    def _run_inference(self, sentence: str, latent: Any, embedding: Any, speed: float) -> dict[str, Any]:
        backend = get_backend(self.loaded_backend or "cpu")
        with self.torch.no_grad():
            if backend.bypass_inference_mode():
                unwrapped = getattr(type(self.model).inference, "__wrapped__", None)
                if unwrapped is not None:
                    return unwrapped(self.model, sentence, LANGUAGE, latent, embedding, speed=speed,
                                     enable_text_splitting=False)
            return self.model.inference(sentence, LANGUAGE, latent, embedding, speed=speed,
                                        enable_text_splitting=False)

    def synthesize(self, payload: dict[str, Any], job: Job) -> dict[str, Any]:
        text = sanitize_text(str(payload.get("text", "")))
        profile_id = str(payload.get("profile_id", "")).strip()
        mode = backends.normalize_compute_mode(payload.get("compute_mode") or self.config.compute_mode)
        try:
            speed = float(payload.get("speed", 1.0))
        except (TypeError, ValueError):
            speed = 1.0
        speed = max(0.5, min(2.0, speed))
        if not text:
            raise ServiceError("Digite um texto para gerar a voz.", code="empty_text")
        if len(text) > MAX_TEXT_LENGTH:
            raise ServiceError(
                f"O texto da voz clonada deve ter no máximo {MAX_TEXT_LENGTH} caracteres.",
                code="text_too_long",
            )
        profile = self.registry.get(profile_id)
        output_path = self.paths.temp_dir / f"utterance_{job.id}.wav"

        def run(backend_mode: str) -> str:
            backend_id = self.ensure_ready(job, backend_mode)
            latent, embedding = self.conditioning_for_profile(profile, job)
            waveform = self.synthesize_waveform(text, latent, embedding, speed=speed, job=job)
            audio.write_wav_pcm16(output_path, waveform.numpy())
            return backend_id

        try:
            backend_id = run(mode)
        except (ServiceError, CancelledError):
            raise
        except Exception as first_error:
            if mode == "auto" and self.loaded_backend not in (None, "cpu"):
                reason = friendly_error(first_error)
                log.warning("Falha em %s; alternando para CPU: %s", self.loaded_backend, reason)
                self._record_validation(self.loaded_backend, {
                    "ok": False,
                    "reason": f"Falha durante a síntese: {reason}",
                    "validated_at": datetime.now(UTC).isoformat(),
                })
                self.last_fallback_reason = reason
                self.unload()
                job.update("A GPU não concluiu a geração; alternando para CPU…")
                try:
                    backend_id = run("cpu")
                except (ServiceError, CancelledError):
                    raise
                except Exception as second_error:
                    raise self._synthesis_failure(second_error) from second_error
            else:
                raise self._synthesis_failure(first_error) from first_error
        _, _, duration = audio.wav_info(output_path)
        return {
            "output_path": str(output_path),
            "device": backend_id,
            "backend": backend_id,
            "backend_label": backends.BACKEND_LABELS[backend_id],
            "duration_seconds": round(duration, 3),
            "characters": len(text),
        }

    def _synthesis_failure(self, error: Exception) -> ServiceError:
        log.error("Falha na síntese: %s", "".join(traceback.format_exception(error)).strip()[-2000:])
        return ServiceError(
            f"Falha ao gerar a voz clonada: {friendly_error(error)}",
            500,
            code="synthesis_failed",
            action="Tente novamente; se persistir, selecione o modo CPU no diagnóstico.",
        )

    # --------------------------------------------------------------- validation
    def _record_validation(self, backend_id: str, report: dict[str, Any]) -> None:
        availability = self.availability.get(backend_id)
        report = dict(report)
        report["fingerprint"] = self._fingerprint(backend_id, availability)
        self.config.data.setdefault("backend_validation", {})[backend_id] = report
        self.config.save()

    def validate_backend(self, backend_id: str, job: Job | None = None, *, persist: bool = True,
                         compare_cpu: bool | None = None) -> dict[str, Any]:
        """Run the full XTTS flow on ``backend_id`` and record the outcome."""
        self.import_torch()
        backend = get_backend(backend_id)
        availability = self.availability.get(backend_id)
        started = time.perf_counter()
        report: dict[str, Any] = {
            "backend": backend_id,
            "label": backend.label,
            "ok": False,
            "reason": "",
            "device_name": availability.device_name if availability else None,
            "torch_version": self.torch_version,
            "checks": [],
            "validated_at": datetime.now(UTC).isoformat(),
        }
        if compare_cpu is None:
            compare_cpu = backend_id in backends.EXPERIMENTAL_BACKENDS
        if availability is None or not availability.available:
            report["reason"] = availability.reason if availability else "Backend desconhecido."
            if persist:
                self._record_validation(backend_id, report)
            return report

        def check(name: str, function: Callable[[], dict[str, Any] | None]) -> dict[str, Any] | None:
            if job:
                job.check_cancelled()
                job.update(f"Testando {backend.label}: {name}…")
            begin = time.perf_counter()
            try:
                extra = function() or {}
                entry = {"name": name, "ok": True, "seconds": round(time.perf_counter() - begin, 3), **extra}
                report["checks"].append(entry)
                return entry
            except CancelledError:
                raise
            except Exception as error:
                entry = {"name": name, "ok": False, "seconds": round(time.perf_counter() - begin, 3),
                         "error": friendly_error(error)}
                report["checks"].append(entry)
                report["reason"] = f"{name}: {entry['error']}"
                log.warning("Validação de %s falhou em %s: %s", backend_id, name, entry["error"])
                return None

        try:
            backend.reset_peak_memory(self.torch)
            if check("carregar modelo", lambda: self.load_model(backend_id, job)) is None:
                return self._finish_validation(report, started, persist, backend_id)
            speaker = check("voz interna", lambda: {"speaker": self.builtin_speaker()[0]})
            if speaker is None:
                return self._finish_validation(report, started, persist, backend_id)
            _, latent, embedding = self.builtin_speaker()

            def synth(text: str, name: str) -> Callable[[], dict[str, Any]]:
                def run() -> dict[str, Any]:
                    waveform = self.synthesize_waveform(text, latent, embedding, job=None)
                    self._assert_waveform(waveform)
                    return {"audio_seconds": round(waveform.numel() / audio.OUTPUT_SAMPLE_RATE, 2),
                            "rms": round(float(waveform.pow(2).mean().sqrt()), 4)}
                return run

            short = check("síntese curta", synth(VALIDATION_SHORT_TEXT, "short"))
            if short is None:
                return self._finish_validation(report, started, persist, backend_id)
            if check("síntese longa (várias sentenças)", synth(VALIDATION_LONG_TEXT, "long")) is None:
                return self._finish_validation(report, started, persist, backend_id)
            if check("síntese consecutiva", synth(VALIDATION_SHORT_TEXT, "repeat")) is None:
                return self._finish_validation(report, started, persist, backend_id)

            def cancel_test() -> dict[str, Any]:
                probe = Job(kind="validation")

                def cancel_after_first(done: int, total: int) -> None:
                    if done == 1:
                        probe.request_cancel()

                try:
                    self.synthesize_waveform(VALIDATION_LONG_TEXT, latent, embedding, job=probe,
                                             on_sentence=cancel_after_first)
                except CancelledError:
                    return {"cancelled": True}
                raise RuntimeError("o cancelamento entre sentenças não interrompeu a síntese")

            if check("cancelamento", cancel_test) is None:
                return self._finish_validation(report, started, persist, backend_id)

            if compare_cpu and backend_id != "cpu":
                def cpu_compare() -> dict[str, Any]:
                    gpu_seconds = short.get("audio_seconds", 0.0)
                    self.model.to("cpu")
                    try:
                        with self.torch.no_grad():
                            cpu_wave = self.synthesize_waveform(
                                VALIDATION_SHORT_TEXT, latent.to("cpu"), embedding.to("cpu"), job=None)
                    finally:
                        self.model.to(self.device)
                    cpu_seconds = cpu_wave.numel() / audio.OUTPUT_SAMPLE_RATE
                    ratio = gpu_seconds / cpu_seconds if cpu_seconds else 0.0
                    if not 0.5 <= ratio <= 2.0:
                        raise RuntimeError(
                            f"duração do áudio na GPU ({gpu_seconds:.2f}s) difere demais da CPU ({cpu_seconds:.2f}s)")
                    return {"cpu_audio_seconds": round(cpu_seconds, 2), "ratio": round(ratio, 3)}

                if check("comparação com CPU", cpu_compare) is None:
                    return self._finish_validation(report, started, persist, backend_id)

            report["ok"] = True
            report["reason"] = "Síntese completa executada com sucesso."
        except CancelledError:
            report["reason"] = "Teste cancelado."
            raise
        finally:
            report["peak_memory_bytes"] = backend.peak_memory_bytes(self.torch)
            report["process_memory_bytes"] = process_memory_bytes()
        return self._finish_validation(report, started, persist, backend_id)

    def _finish_validation(self, report: dict[str, Any], started: float, persist: bool,
                           backend_id: str) -> dict[str, Any]:
        report["elapsed_seconds"] = round(time.perf_counter() - started, 2)
        synth = next((c for c in report["checks"] if c["name"] == "síntese curta"), None)
        report["test_synthesis_seconds"] = synth["seconds"] if synth else None
        if persist:
            self._record_validation(backend_id, report)
        if not report["ok"]:
            self.unload()
        log.info("Validação de %s: ok=%s (%s)", backend_id, report["ok"], report["reason"])
        return report

    def _assert_waveform(self, waveform: Any) -> None:
        torch = self.torch
        if waveform.numel() < audio.OUTPUT_SAMPLE_RATE // 4:
            raise RuntimeError("o áudio gerado é curto demais")
        if not bool(torch.isfinite(waveform).all()):
            raise RuntimeError("o áudio gerado contém valores inválidos (NaN/Inf)")
        rms = float(waveform.pow(2).mean().sqrt())
        if rms < 0.005:
            raise RuntimeError("o áudio gerado está praticamente silencioso")

    # -------------------------------------------------------------- diagnostics
    def status(self) -> dict[str, Any]:
        return {
            "torch_version": self.torch_version,
            "torch_imported": self.torch is not None,
            "model_loaded": self.model is not None,
            "active_backend": self.loaded_backend,
            "active_backend_label": backends.BACKEND_LABELS.get(self.loaded_backend or "", None),
            "model_load_seconds": self.model_load_seconds,
            "fallback_reason": self.last_fallback_reason,
            "compute_mode": self.config.compute_mode,
        }

    def diagnostics(self) -> dict[str, Any]:
        self.import_torch()
        available = {backend_id: item.to_json() for backend_id, item in self.availability.items()}
        validations = {
            backend_id: {k: v for k, v in record.items() if k != "fingerprint"}
            for backend_id, record in self.config.data.get("backend_validation", {}).items()
            if isinstance(record, dict)
        }
        try:
            recommended, reason = self.resolve_backend("auto")
        except ServiceError as error:
            recommended, reason = "cpu", str(error)
        return {
            **self.status(),
            "backends": available,
            "validations": validations,
            "recommended_backend": recommended,
            "recommended_reason": reason,
            "runtime": self.runtime_info,
            "python": sys.version.split()[0],
        }
