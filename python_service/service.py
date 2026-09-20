"""SVoice local XTTSv2 service.

The Flutter application starts this process in the background on a random
loopback port. It is not intended to be launched directly by the end user.
"""

from __future__ import annotations

import argparse
import gc
import hmac
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import traceback
import uuid
import wave
from dataclasses import dataclass
from datetime import UTC, datetime
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlparse


SERVICE_VERSION = "1.1.2"
MODEL_NAME = "tts_models/multilingual/multi-dataset/xtts_v2"
ALLOWED_EXTENSIONS = {".wav", ".mp3", ".m4a", ".flac", ".ogg"}
MAX_TEXT_LENGTH = 1000
MAX_REFERENCE_DURATION_SECONDS = 30 * 60
REFERENCE_CHUNK_SECONDS = 20
REFERENCE_SAMPLE_RATE = 24000
CONDITIONING_CACHE_VERSION = 2
MAX_JSON_BODY_BYTES = 1_000_000


class ServiceError(Exception):
    def __init__(self, message: str, status: int = HTTPStatus.BAD_REQUEST):
        super().__init__(message)
        self.status = status


@dataclass
class RuntimeState:
    state: str = "ready"
    message: str = "Mecanismo de clonagem pronto"
    active_device: str | None = None
    warning: str | None = None


class SVoiceXttsService:
    def __init__(self, data_dir: Path):
        self.data_dir = data_dir.resolve()
        self.voices_dir = self.data_dir / "voices"
        self.temp_dir = self.data_dir / "temp"
        self.models_dir = self.data_dir / "models"
        self.registry_path = self.data_dir / "profiles.json"
        self.config_path = self.data_dir / "config.json"
        for directory in (
            self.data_dir,
            self.voices_dir,
            self.temp_dir,
            self.models_dir,
        ):
            directory.mkdir(parents=True, exist_ok=True)

        os.environ.setdefault("TTS_HOME", str(self.models_dir))
        os.environ.setdefault("COQUI_TTS_CACHE", str(self.models_dir))

        self._registry_lock = threading.RLock()
        self._model_lock = threading.RLock()
        self._synthesis_lock = threading.Lock()
        self._state_lock = threading.Lock()
        self._runtime = RuntimeState()
        self._tts_api = None
        self._tts_model = None
        self._torch = None
        self._torchaudio = None
        self._loaded_device: str | None = None
        self._cuda_available: bool | None = None
        self._gpu_name: str | None = None
        self._profiles = self._read_json(self.registry_path, {"profiles": []})
        self._config = self._read_json(
            self.config_path,
            {"compute_mode": "auto"},
        )
        self._cleanup_temp_files()

    @staticmethod
    def _read_json(path: Path, default: dict[str, Any]) -> dict[str, Any]:
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            return value if isinstance(value, dict) else default
        except (OSError, ValueError):
            return default

    @staticmethod
    def _write_json(path: Path, value: dict[str, Any]) -> None:
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(path.suffix + ".tmp")
        temporary.write_text(
            json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        temporary.replace(path)

    def _cleanup_temp_files(self) -> None:
        for path in self.temp_dir.iterdir():
            try:
                if path.is_dir() and path.name.startswith("import_"):
                    shutil.rmtree(path)
                elif path.is_file() and path.suffix.lower() == ".wav":
                    path.unlink()
            except OSError:
                pass

    def _set_status(
        self,
        state: str,
        message: str,
        *,
        active_device: str | None = None,
        warning: str | None = None,
    ) -> None:
        with self._state_lock:
            self._runtime.state = state
            self._runtime.message = message
            if active_device is not None:
                self._runtime.active_device = active_device
            self._runtime.warning = warning

    def _detect_hardware(self) -> None:
        if self._cuda_available is not None:
            return
        try:
            import torch

            self._torch = torch
            self._cuda_available = bool(torch.cuda.is_available())
            if self._cuda_available:
                self._gpu_name = torch.cuda.get_device_name(0)
        except Exception:
            self._cuda_available = False
            self._gpu_name = None

    def health(self) -> dict[str, Any]:
        with self._state_lock:
            runtime = RuntimeState(**self._runtime.__dict__)
        return {
            "version": SERVICE_VERSION,
            "hardware_checked": self._cuda_available is not None,
            "cuda_available": self._cuda_available is True,
            "gpu_name": self._gpu_name,
            "model_loaded": self._tts_model is not None,
            "state": runtime.state,
            "message": runtime.message,
            "active_device": runtime.active_device,
            "warning": runtime.warning,
            "compute_mode": self._config.get("compute_mode", "auto"),
        }

    def list_profiles(self) -> list[dict[str, Any]]:
        with self._registry_lock:
            profiles = self._profiles.get("profiles", [])
            return [self._public_profile(profile) for profile in profiles]

    @staticmethod
    def _public_profile(profile: dict[str, Any]) -> dict[str, Any]:
        return {
            "id": profile["id"],
            "name": profile["name"],
            "reference_count": len(profile.get("reference_paths", [])),
            "duration_seconds": float(profile.get("duration_seconds", 0.0)),
            "source_count": int(profile.get("source_count", 1)),
            "truncated": bool(profile.get("truncated", False)),
            "created_at": profile.get("created_at"),
        }

    def add_profile(self, payload: dict[str, Any]) -> dict[str, Any]:
        sources = self._validated_reference_sources(payload.get("reference_paths"))
        name = self._available_profile_name(payload.get("name"), sources[0].stem)
        original_duration = sum(
            max(0.0, self._audio_duration(source)) for source in sources
        )

        profile_id = uuid.uuid4().hex
        profile_dir = self.voices_dir / profile_id
        processing_dir = self.temp_dir / f"import_{profile_id}"
        profile_dir.mkdir(parents=True, exist_ok=False)
        processing_dir.mkdir(parents=True, exist_ok=False)
        processed_paths: list[str] = []
        total_duration = 0.0
        try:
            self._set_status("processing", "Cortando os áudios de referência…")
            remaining_seconds = float(MAX_REFERENCE_DURATION_SECONDS)
            chunk_number = 0
            for source_index, source in enumerate(sources):
                if remaining_seconds <= 0.05:
                    break
                generated_chunks = self._split_reference_audio(
                    source,
                    processing_dir,
                    source_index,
                    remaining_seconds,
                )
                if not generated_chunks:
                    raise ServiceError(f"Não foi possível ler o áudio {source.name}.")
                for temporary_chunk in generated_chunks:
                    duration = self._audio_duration(temporary_chunk)
                    if duration <= 0:
                        continue
                    chunk_number += 1
                    destination = profile_dir / f"reference_{chunk_number:04d}.wav"
                    shutil.move(str(temporary_chunk), destination)
                    processed_paths.append(str(destination))
                    total_duration += duration
                remaining_seconds = max(
                    0.0, MAX_REFERENCE_DURATION_SECONDS - total_duration
                )

            if total_duration < 3:
                raise ServiceError(
                    "O áudio de referência precisa ter pelo menos 3 segundos. "
                    "Para melhor qualidade, utilize de 10 a 30 segundos."
                )

            profile = {
                "id": profile_id,
                "name": name,
                "reference_paths": processed_paths,
                "duration_seconds": round(
                    min(total_duration, MAX_REFERENCE_DURATION_SECONDS), 3
                ),
                "source_count": len(sources),
                "truncated": original_duration
                > MAX_REFERENCE_DURATION_SECONDS + 0.05,
                "created_at": datetime.now(UTC).isoformat(),
            }
            with self._registry_lock:
                self._profiles.setdefault("profiles", []).append(profile)
                self._write_json(self.registry_path, self._profiles)
            self._set_status("ready", "Áudios de referência preparados")
            return self._public_profile(profile)
        except Exception:
            shutil.rmtree(profile_dir, ignore_errors=True)
            self._set_status("ready", "Mecanismo de clonagem pronto")
            raise
        finally:
            shutil.rmtree(processing_dir, ignore_errors=True)

    @staticmethod
    def _validated_reference_sources(raw_paths: Any) -> list[Path]:
        if not isinstance(raw_paths, list) or not raw_paths:
            raise ServiceError("Selecione pelo menos um áudio de referência.")

        sources: list[Path] = []
        seen_paths: set[str] = set()
        for raw_path in raw_paths:
            if not isinstance(raw_path, (str, os.PathLike)) or not str(raw_path).strip():
                raise ServiceError("Um dos caminhos de áudio é inválido.")
            try:
                source = Path(raw_path).expanduser().resolve(strict=True)
            except (OSError, RuntimeError) as error:
                raise ServiceError(
                    "Um dos áudios selecionados não foi encontrado."
                ) from error
            if not source.is_file() or source.suffix.lower() not in ALLOWED_EXTENSIONS:
                raise ServiceError(
                    "Use arquivos WAV, MP3, M4A, FLAC ou OGG como referência."
                )
            try:
                if source.stat().st_size <= 0:
                    raise ServiceError(f"O arquivo {source.name} está vazio.")
            except OSError as error:
                raise ServiceError(
                    f"Não foi possível acessar o arquivo {source.name}."
                ) from error
            normalized_path = os.path.normcase(str(source))
            if normalized_path in seen_paths:
                continue
            seen_paths.add(normalized_path)
            sources.append(source)

        if not sources:
            raise ServiceError("Selecione pelo menos um áudio de referência.")
        return sources

    def _available_profile_name(self, requested_name: Any, fallback: str) -> str:
        raw_name = "" if requested_name is None else str(requested_name)
        name = re.sub(r"[\x00-\x1f\x7f]", "", raw_name)
        name = re.sub(r"\s+", " ", name).strip(" .")
        if not name:
            name = re.sub(r"\s+", " ", fallback).strip(" .")
        if not name:
            name = "Voz clonada"
        name = name[:80].rstrip(" .") or "Voz clonada"

        with self._registry_lock:
            existing_names = {
                str(profile.get("name", "")).casefold()
                for profile in self._profiles.get("profiles", [])
            }
        if name.casefold() not in existing_names:
            return name

        index = 2
        while True:
            suffix = f" ({index})"
            candidate = f"{name[: 80 - len(suffix)].rstrip()}{suffix}"
            if candidate.casefold() not in existing_names:
                return candidate
            index += 1

    @staticmethod
    def _split_reference_audio(
        source: Path,
        processing_dir: Path,
        source_index: int,
        maximum_seconds: float,
    ) -> list[Path]:
        try:
            import imageio_ffmpeg

            ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
        except Exception as error:
            raise ServiceError(
                "O conversor de áudio integrado não está disponível."
            ) from error

        output_pattern = processing_dir / f"source_{source_index:04d}_%04d.wav"
        creation_flags = (
            subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        )
        command = [
            ffmpeg,
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
                timeout=30 * 60,
                creationflags=creation_flags,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ServiceError(
                f"Não foi possível processar o áudio {source.name}."
            ) from error
        if result.returncode != 0:
            detail = result.stderr.strip().splitlines()
            reason = detail[-1] if detail else "formato não reconhecido"
            raise ServiceError(
                f"Não foi possível processar {source.name}: {reason}"
            )
        return sorted(processing_dir.glob(f"source_{source_index:04d}_*.wav"))

    def delete_profile(self, profile_id: str) -> None:
        with self._synthesis_lock, self._registry_lock:
            profiles = self._profiles.get("profiles", [])
            profile = next(
                (item for item in profiles if item.get("id") == profile_id), None
            )
            if profile is None:
                raise ServiceError("Perfil de voz não encontrado.", HTTPStatus.NOT_FOUND)
            self._profiles["profiles"] = [
                item for item in profiles if item.get("id") != profile_id
            ]
            self._write_json(self.registry_path, self._profiles)
            shutil.rmtree(self.voices_dir / profile_id, ignore_errors=True)

    def update_config(self, payload: dict[str, Any]) -> dict[str, Any]:
        mode = payload.get("compute_mode")
        if mode is not None:
            if mode not in {"auto", "gpu", "cpu"}:
                raise ServiceError("Modo de processamento inválido.")
            if mode != self._config.get("compute_mode"):
                self._config["compute_mode"] = mode
                self._unload_model()
        self._write_json(self.config_path, self._config)
        return {"compute_mode": self._config.get("compute_mode", "auto")}

    def synthesize(self, payload: dict[str, Any]) -> dict[str, Any]:
        text = str(payload.get("text", "")).strip()
        profile_id = str(payload.get("profile_id", "")).strip()
        mode = str(
            payload.get("compute_mode") or self._config.get("compute_mode", "auto")
        )
        try:
            speed = float(payload.get("speed", 1.0))
        except (TypeError, ValueError):
            speed = 1.0

        if not text:
            raise ServiceError("Digite um texto para gerar a voz.")
        if len(text) > MAX_TEXT_LENGTH:
            raise ServiceError(
                f"O texto da voz clonada deve ter no máximo {MAX_TEXT_LENGTH} caracteres."
            )
        if mode not in {"auto", "gpu", "cpu"}:
            raise ServiceError("Modo de processamento inválido.")
        speed = max(0.5, min(2.0, speed))

        with self._registry_lock:
            profile = next(
                (
                    dict(item)
                    for item in self._profiles.get("profiles", [])
                    if item.get("id") == profile_id
                ),
                None,
            )
        if profile is None:
            raise ServiceError("Perfil de voz não encontrado.", HTTPStatus.NOT_FOUND)

        if not self._synthesis_lock.acquire(blocking=False):
            raise ServiceError(
                "Já existe uma voz sendo gerada. Aguarde ou interrompa a geração.",
                HTTPStatus.CONFLICT,
            )
        try:
            output_path = self.temp_dir / f"utterance_{uuid.uuid4().hex}.wav"
            try:
                device = self._resolve_device(mode)
                self._generate_once(profile, text, speed, output_path, device)
            except Exception as first_error:
                if mode == "auto" and self._loaded_device == "cuda":
                    self._set_status(
                        "loading",
                        "A GPU não concluiu a geração; alternando para CPU…",
                        warning=str(first_error),
                    )
                    self._unload_model()
                    self._generate_once(profile, text, speed, output_path, "cpu")
                else:
                    raise

            self._set_status(
                "ready",
                "Áudio clonado gerado",
                active_device=self._loaded_device,
            )
            return {
                "output_path": str(output_path),
                "device": self._loaded_device,
            }
        except ServiceError:
            raise
        except Exception as error:
            self._set_status(
                "error",
                "Não foi possível gerar a voz clonada",
                warning=str(error),
            )
            traceback.print_exc(file=sys.stderr)
            raise ServiceError(
                f"Falha ao gerar a voz clonada: {self._friendly_error(error)}",
                HTTPStatus.INTERNAL_SERVER_ERROR,
            ) from error
        finally:
            self._synthesis_lock.release()

    def _resolve_device(self, mode: str) -> str:
        self._detect_hardware()
        if mode == "cpu":
            return "cpu"
        if mode == "gpu":
            if not self._cuda_available:
                raise ServiceError(
                    "CUDA não está disponível. Selecione Automático ou CPU."
                )
            return "cuda"
        return "cuda" if self._cuda_available else "cpu"

    def _generate_once(
        self,
        profile: dict[str, Any],
        text: str,
        speed: float,
        output_path: Path,
        device: str,
    ) -> None:
        model = self._get_model(device)
        self._set_status(
            "conditioning",
            "Analisando as características da voz…",
            active_device=device,
        )
        conditioning, embedding = self._conditioning_for_profile(profile, model, device)
        self._set_status(
            "synthesizing",
            "Gerando a voz clonada…",
            active_device=device,
        )

        result = model.inference(
            self._sanitize_text(text),
            "pt",
            conditioning,
            embedding,
            speed=speed,
            enable_text_splitting=True,
        )
        waveform = result["wav"]
        if not self._torch.is_tensor(waveform):
            waveform = self._torch.tensor(waveform)
        waveform = waveform.detach().float().cpu().reshape(1, -1)
        peak = float(waveform.abs().max()) if waveform.numel() else 0.0
        target_peak = 10 ** (-3 / 20)
        if peak > 0:
            waveform = waveform * (target_peak / peak)
        self._torchaudio.save(str(output_path), waveform, 24000)

    def _get_model(self, device: str):
        with self._model_lock:
            if self._tts_model is not None and self._loaded_device == device:
                return self._tts_model
            self._unload_model()
            self._set_status(
                "loading",
                "Baixando ou carregando o modelo XTTSv2…",
                active_device=device,
            )
            os.environ["COQUI_TOS_AGREED"] = "1"
            self._apply_transformers_compatibility()
            import torch
            import torchaudio
            from TTS.api import TTS

            self._torch = torch
            self._torchaudio = torchaudio
            self._tts_api = TTS(model_name=MODEL_NAME, progress_bar=False).to(device)
            self._tts_model = self._tts_api.synthesizer.tts_model
            self._loaded_device = device
            return self._tts_model

    def _conditioning_for_profile(self, profile, model, device: str):
        cache_path = self.voices_dir / profile["id"] / "conditioning.pt"
        if cache_path.exists():
            try:
                payload = self._torch.load(
                    cache_path, map_location=device, weights_only=False
                )
                if (
                    payload.get("model") == MODEL_NAME
                    and payload.get("cache_version") == CONDITIONING_CACHE_VERSION
                ):
                    return (
                        payload["gpt_cond_latent"].to(device),
                        payload["speaker_embedding"].to(device),
                    )
            except Exception:
                pass
            try:
                cache_path.unlink()
            except OSError:
                pass

        reference_paths = [
            path
            for path in profile.get("reference_paths", [])
            if Path(path).is_file()
        ]
        if not reference_paths:
            raise ServiceError("O áudio de referência deste perfil não foi encontrado.")
        conditioning, embedding = model.get_conditioning_latents(
            audio_path=reference_paths,
            max_ref_length=REFERENCE_CHUNK_SECONDS,
            gpt_cond_len=-1,
            gpt_cond_chunk_len=6,
        )
        self._torch.save(
            {
                "gpt_cond_latent": conditioning.detach().cpu(),
                "speaker_embedding": embedding.detach().cpu(),
                "model": MODEL_NAME,
                "cache_version": CONDITIONING_CACHE_VERSION,
                "created_at": datetime.now(UTC).isoformat(),
            },
            cache_path,
        )
        return conditioning, embedding

    def _unload_model(self) -> None:
        with self._model_lock:
            self._tts_model = None
            self._tts_api = None
            loaded_device = self._loaded_device
            self._loaded_device = None
            gc.collect()
            try:
                if loaded_device == "cuda" and self._torch is not None:
                    self._torch.cuda.empty_cache()
            except Exception:
                pass

    @staticmethod
    def _apply_transformers_compatibility() -> None:
        try:
            import torch
            import transformers.pytorch_utils as pytorch_utils

            if not hasattr(pytorch_utils, "isin_mps_friendly"):
                pytorch_utils.isin_mps_friendly = torch.isin
        except Exception:
            pass

    @staticmethod
    def _sanitize_text(text: str) -> str:
        text = text.replace("…", "...")
        text = re.sub(r"\s+", " ", text)
        return text.strip()

    @staticmethod
    def _audio_duration(path: Path) -> float:
        if path.suffix.lower() == ".wav":
            try:
                with wave.open(str(path), "rb") as audio:
                    return audio.getnframes() / float(audio.getframerate())
            except (wave.Error, OSError, ZeroDivisionError):
                pass
        try:
            from mutagen import File as MutagenFile

            metadata = MutagenFile(path)
            if metadata is not None and metadata.info is not None:
                return float(metadata.info.length)
        except Exception:
            pass
        return 0.0

    @staticmethod
    def _friendly_error(error: Exception) -> str:
        message = str(error).strip()
        lowered = message.casefold()
        if "out of memory" in lowered or "cuda" in lowered and "memory" in lowered:
            return "memória de vídeo insuficiente; tente o modo CPU"
        if "ffmpeg" in lowered or "codec" in lowered:
            return "formato de áudio não suportado; tente um arquivo WAV"
        return message or error.__class__.__name__


class ApiHandler(BaseHTTPRequestHandler):
    server_version = "SVoiceXTTS/1.0"

    @property
    def app(self) -> SVoiceXttsService:
        return self.server.app  # type: ignore[attr-defined]

    @property
    def expected_token(self) -> str:
        return self.server.token  # type: ignore[attr-defined]

    def log_message(self, format_string: str, *args: Any) -> None:
        return

    def _authorized(self) -> bool:
        provided = self.headers.get("Authorization", "")
        expected = f"Bearer {self.expected_token}"
        return hmac.compare_digest(provided, expected)

    def _read_payload(self) -> dict[str, Any]:
        transfer_encoding = self.headers.get("Transfer-Encoding", "").casefold()
        if transfer_encoding:
            if transfer_encoding != "chunked":
                raise ServiceError("Codificação da requisição não suportada.")
            content = self._read_chunked_body()
        else:
            raw_length = self.headers.get("Content-Length")
            if raw_length is None:
                return {}
            try:
                length = int(raw_length)
            except ValueError as error:
                raise ServiceError("Tamanho da requisição inválido.") from error
            if length < 0:
                raise ServiceError("Tamanho da requisição inválido.")
            if length == 0:
                return {}
            if length > MAX_JSON_BODY_BYTES:
                raise ServiceError(
                    "Requisição muito grande.",
                    HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                )
            content = self.rfile.read(length)
            if len(content) != length:
                raise ServiceError("A requisição foi recebida de forma incompleta.")

        try:
            value = json.loads(content.decode("utf-8"))
            if not isinstance(value, dict):
                raise ValueError
            return value
        except (UnicodeDecodeError, ValueError, json.JSONDecodeError):
            raise ServiceError("JSON inválido.")

    def _read_chunked_body(self) -> bytes:
        content = bytearray()
        while True:
            size_line = self.rfile.readline(128)
            if not size_line or len(size_line) >= 128 or not size_line.endswith(b"\r\n"):
                raise ServiceError("A requisição foi recebida de forma incompleta.")
            try:
                chunk_size = int(size_line.split(b";", 1)[0].strip(), 16)
            except ValueError as error:
                raise ServiceError("Codificação da requisição inválida.") from error
            if chunk_size < 0:
                raise ServiceError("Codificação da requisição inválida.")
            if chunk_size == 0:
                while True:
                    trailer = self.rfile.readline(8192)
                    if trailer in {b"\r\n", b""}:
                        return bytes(content)
                    if len(trailer) >= 8192:
                        raise ServiceError("Codificação da requisição inválida.")
            if len(content) + chunk_size > MAX_JSON_BODY_BYTES:
                raise ServiceError(
                    "Requisição muito grande.",
                    HTTPStatus.REQUEST_ENTITY_TOO_LARGE,
                )
            chunk = self.rfile.read(chunk_size)
            if len(chunk) != chunk_size or self.rfile.read(2) != b"\r\n":
                raise ServiceError("A requisição foi recebida de forma incompleta.")
            content.extend(chunk)

    def _send_json(self, status: int, value: dict[str, Any]) -> None:
        content = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(content)

    def _dispatch(self, method: str) -> None:
        if not self._authorized():
            self._send_json(HTTPStatus.UNAUTHORIZED, {"error": "Não autorizado."})
            return
        path = urlparse(self.path).path
        payload = self._read_payload() if method in {"POST", "PUT"} else {}

        if method == "GET" and path == "/health":
            self._send_json(HTTPStatus.OK, self.app.health())
        elif method == "GET" and path == "/profiles":
            self._send_json(
                HTTPStatus.OK, {"profiles": self.app.list_profiles()}
            )
        elif method == "POST" and path == "/profiles":
            profile = self.app.add_profile(payload)
            self._send_json(HTTPStatus.CREATED, {"profile": profile})
        elif method == "DELETE" and path.startswith("/profiles/"):
            profile_id = unquote(path.removeprefix("/profiles/"))
            self.app.delete_profile(profile_id)
            self._send_json(HTTPStatus.OK, {"deleted": True})
        elif method == "POST" and path == "/config":
            self._send_json(HTTPStatus.OK, self.app.update_config(payload))
        elif method == "POST" and path == "/synthesize":
            self._send_json(HTTPStatus.OK, self.app.synthesize(payload))
        elif method == "POST" and path == "/shutdown":
            self._send_json(HTTPStatus.OK, {"shutting_down": True})
            threading.Thread(target=self.server.shutdown, daemon=True).start()
        else:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "Rota não encontrada."})

    def do_GET(self) -> None:  # noqa: N802
        self._handle("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._handle("POST")

    def do_DELETE(self) -> None:  # noqa: N802
        self._handle("DELETE")

    def _handle(self, method: str) -> None:
        try:
            self._dispatch(method)
        except ServiceError as error:
            self._send_json(error.status, {"error": str(error)})
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            self._send_json(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                {"error": f"Erro interno do XTTS: {error}"},
            )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--token", required=True)
    parser.add_argument("--data-dir", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.port < 1024 or args.port > 65535:
        raise SystemExit("Porta inválida")
    if len(args.token) < 32:
        raise SystemExit("Token inválido")

    app = SVoiceXttsService(args.data_dir)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), ApiHandler)
    server.daemon_threads = True
    server.app = app  # type: ignore[attr-defined]
    server.token = args.token  # type: ignore[attr-defined]
    try:
        server.serve_forever(poll_interval=0.25)
    finally:
        server.server_close()
        app._unload_model()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
