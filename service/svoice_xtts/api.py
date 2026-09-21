"""Loopback HTTP API (protocol version 2).

Every request must carry ``Authorization: Bearer <token>``. Responses are JSON
and always include ``protocol_version``. Errors are ``{"error": message,
"code": ..., "action": ...}``.
"""

from __future__ import annotations

import hmac
import json
import threading
import traceback
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import unquote, urlparse

from . import PROTOCOL_VERSION, SERVICE_VERSION, backends, model as model_files
from .engine import ConfigStore, XttsEngine, friendly_error
from .errors import CancelledError, ServiceError
from .hardware import SystemInfo
from .jobs import Job, JobTracker
from .logging_setup import get_logger
from .paths import DataPaths
from .profiles import ProfileRegistry, cleanup_temp_files

log = get_logger("api")

MAX_JSON_BODY_BYTES = 1_000_000


class ServiceApp:
    """Application state shared by request handlers."""

    def __init__(self, paths: DataPaths, system: SystemInfo, runtime_info: dict[str, Any]):
        self.paths = paths
        self.system = system
        self.runtime_info = runtime_info
        self.registry = ProfileRegistry(paths)
        self.config = ConfigStore(paths.config_path)
        self.engine = XttsEngine(paths, self.registry, self.config, runtime_info)
        self.jobs = JobTracker()
        self.shutdown_requested = threading.Event()
        self._temp_cleanup()

    def _temp_cleanup(self) -> None:
        removed = cleanup_temp_files(self.paths)
        if removed:
            log.info("Arquivos temporários removidos: %d", removed)

    # ------------------------------------------------------------------ health
    def health(self) -> dict[str, Any]:
        job = self.jobs.snapshot()
        engine = self.engine.status()
        model_status = model_files.check_model(self.paths.models_dir)
        state = "ready"
        message = "Mecanismo XTTS pronto"
        active = self.jobs.active
        if active is not None:
            state = active.kind
            message = active.message or state
        elif not model_status.ready:
            state = "model_missing"
            message = "O modelo XTTS v2 precisa ser baixado"
        elif not engine["model_loaded"]:
            message = "Modelo será carregado na primeira fala"
        return {
            "version": SERVICE_VERSION,
            "protocol_version": PROTOCOL_VERSION,
            "state": state,
            "message": message,
            "busy": active is not None,
            "job": job,
            "model_ready": model_status.ready,
            "model_missing": model_status.missing,
            "model_loaded": engine["model_loaded"],
            "active_backend": engine["active_backend"],
            "active_backend_label": engine["active_backend_label"],
            "compute_mode": engine["compute_mode"],
            "fallback_reason": engine["fallback_reason"],
            "gpu_name": next((gpu.name for gpu in self.system.gpus if gpu.vendor in ("nvidia", "amd")), None),
            "runtime": self.runtime_info,
            "data_dir": str(self.paths.data_dir),
            "migration": self.registry.migration_report,
        }

    # -------------------------------------------------------------------- jobs
    def run_job(self, kind: str, message: str, function) -> Any:
        job = self.jobs.start(kind, message)
        try:
            result = function(job)
        except BaseException as error:
            self.jobs.finish(job, error=error if isinstance(error, Exception) else RuntimeError(str(error)))
            raise
        else:
            self.jobs.finish(job)
            return result

    def synthesize(self, payload: dict[str, Any]) -> dict[str, Any]:
        return self.run_job("synthesizing", "Preparando a voz clonada…", lambda job: self.engine.synthesize(payload, job))

    def create_profile(self, payload: dict[str, Any]) -> dict[str, Any]:
        def work(job: Job) -> dict[str, Any]:
            profile = self.registry.create(
                payload,
                progress=lambda message, value: job.update(message, value),
                check_cancelled=job.check_cancelled,
            )
            precompute = payload.get("precompute", True)
            if precompute and model_files.check_model(self.paths.models_dir).ready:
                try:
                    job.update("Analisando a voz…", 0.75)
                    self.engine.ensure_ready(job)
                    self.engine.conditioning_for_profile(profile, job)
                except CancelledError:
                    self.registry.delete(profile["id"])
                    raise
                except ServiceError as error:
                    log.warning("Condicionamento adiado para o perfil %s: %s", profile["id"], error)
                except Exception as error:  # pragma: no cover - depends on hardware
                    log.warning("Condicionamento adiado para o perfil %s: %s", profile["id"], friendly_error(error))
            return self.registry.public(profile)

        return self.run_job("cloning", "Preparando os áudios de referência…", work)

    def update_config(self, payload: dict[str, Any]) -> dict[str, Any]:
        if "compute_mode" in payload:
            raw = payload.get("compute_mode")
            mode = backends.normalize_compute_mode(raw)
            if str(raw).strip().lower() not in backends.COMPUTE_MODES and str(raw).strip().lower() not in backends.LEGACY_MODE_ALIASES:
                raise ServiceError("Modo de processamento inválido.", code="invalid_compute_mode")
            if mode != self.config.compute_mode:
                if self.jobs.busy:
                    raise ServiceError("Aguarde a operação atual antes de trocar o backend.", 409, code="busy")
                self.config.data["compute_mode"] = mode
                self.config.save()
                self.engine.unload()
        if payload.get("reset_validation"):
            self.config.data["backend_validation"] = {}
            self.config.save()
        return {"compute_mode": self.config.compute_mode}

    def diagnostics(self) -> dict[str, Any]:
        return {
            "service_version": SERVICE_VERSION,
            "protocol_version": PROTOCOL_VERSION,
            "system": self.system.to_json(),
            "model": model_files.check_model(self.paths.models_dir).to_json(),
            "engine": self.engine.diagnostics(),
            "data_dir": str(self.paths.data_dir),
            "logs_dir": str(self.paths.logs_dir),
            "profiles": len(self.registry.list()),
            "migration": self.registry.migration_report,
        }

    def test_backend(self, payload: dict[str, Any]) -> dict[str, Any]:
        backend_id = str(payload.get("backend") or "").strip().lower()
        if backend_id not in backends.BACKENDS:
            raise ServiceError("Backend inválido.", code="invalid_backend")
        compare_cpu = payload.get("compare_cpu")
        return self.run_job(
            "testing",
            f"Testando {backends.BACKEND_LABELS[backend_id]}…",
            lambda job: self.engine.validate_backend(
                backend_id, job, persist=True, compare_cpu=bool(compare_cpu) if compare_cpu is not None else None
            ),
        )

    def ensure_model(self, payload: dict[str, Any]) -> dict[str, Any]:
        allow_download = payload.get("download", True) is not False

        def work(job: Job) -> dict[str, Any]:
            def progress(stage: str, done: int, total: int) -> None:
                job.update(stage, (done / total) if total else None)

            status = model_files.ensure_model(
                self.paths.models_dir,
                progress=progress,
                cancel=job.cancel_requested,
                allow_download=allow_download,
            )
            return status.to_json()

        return self.run_job("downloading", "Verificando o modelo XTTS v2…", work)

    def audio_path_is_ours(self, raw: str) -> Path:
        path = Path(raw)
        try:
            resolved = path.resolve(strict=True)
        except (OSError, RuntimeError) as error:
            raise ServiceError("Áudio não encontrado.", 404, code="audio_not_found") from error
        if resolved.parent != self.paths.temp_dir.resolve() or resolved.suffix.lower() != ".wav":
            raise ServiceError("Caminho de áudio inválido.", 403, code="invalid_path")
        return resolved


class ApiHandler(BaseHTTPRequestHandler):
    server_version = f"SVoiceXTTS/{SERVICE_VERSION}"
    protocol_version = "HTTP/1.1"

    @property
    def app(self) -> ServiceApp:
        return self.server.app  # type: ignore[attr-defined]

    @property
    def expected_token(self) -> str:
        return self.server.token  # type: ignore[attr-defined]

    def log_message(self, format_string: str, *args: Any) -> None:  # noqa: D401
        return

    def _authorized(self) -> bool:
        provided = self.headers.get("Authorization", "")
        expected = f"Bearer {self.expected_token}"
        return hmac.compare_digest(provided, expected)

    # ----------------------------------------------------------------- payload
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
                raise ServiceError("Requisição muito grande.", HTTPStatus.REQUEST_ENTITY_TOO_LARGE, code="too_large")
            content = self.rfile.read(length)
            if len(content) != length:
                raise ServiceError("A requisição foi recebida de forma incompleta.")
        try:
            value = json.loads(content.decode("utf-8"))
            if not isinstance(value, dict):
                raise ValueError
            return value
        except (UnicodeDecodeError, ValueError):
            raise ServiceError("JSON inválido.", code="invalid_json")

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
                raise ServiceError("Requisição muito grande.", HTTPStatus.REQUEST_ENTITY_TOO_LARGE, code="too_large")
            chunk = self.rfile.read(chunk_size)
            if len(chunk) != chunk_size or self.rfile.read(2) != b"\r\n":
                raise ServiceError("A requisição foi recebida de forma incompleta.")
            content.extend(chunk)

    # ---------------------------------------------------------------- responses
    def _send_json(self, status: int, value: dict[str, Any]) -> None:
        payload = dict(value)
        payload.setdefault("protocol_version", PROTOCOL_VERSION)
        content = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(content)

    def _send_file(self, path: Path, content_type: str) -> None:
        data = path.read_bytes()
        self.send_response(HTTPStatus.OK)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(data)

    # ----------------------------------------------------------------- routing
    def _dispatch(self, method: str) -> None:
        if not self._authorized():
            self._send_json(HTTPStatus.UNAUTHORIZED, {"error": "Não autorizado.", "code": "unauthorized"})
            return
        app = self.app
        app.jobs.touch()
        parsed = urlparse(self.path)
        path = parsed.path
        payload = self._read_payload() if method in {"POST", "PUT", "PATCH"} else {}

        if method == "GET" and path == "/health":
            self._send_json(HTTPStatus.OK, app.health())
        elif method == "GET" and path == "/diagnostics":
            self._send_json(HTTPStatus.OK, app.diagnostics())
        elif method == "POST" and path == "/diagnostics/test":
            self._send_json(HTTPStatus.OK, {"report": app.test_backend(payload)})
        elif method == "GET" and path == "/profiles":
            self._send_json(HTTPStatus.OK, {"profiles": app.registry.list()})
        elif method == "POST" and path == "/profiles":
            self._send_json(HTTPStatus.CREATED, {"profile": app.create_profile(payload)})
        elif method == "PATCH" and path.startswith("/profiles/"):
            profile_id = unquote(path.removeprefix("/profiles/"))
            self._send_json(HTTPStatus.OK, {"profile": app.registry.rename(profile_id, payload.get("name"))})
        elif method == "DELETE" and path.startswith("/profiles/"):
            profile_id = unquote(path.removeprefix("/profiles/"))
            if app.jobs.busy:
                raise ServiceError("Aguarde a operação atual antes de excluir a voz.", 409, code="busy")
            app.registry.delete(profile_id)
            self._send_json(HTTPStatus.OK, {"deleted": True})
        elif method == "GET" and path == "/config":
            self._send_json(HTTPStatus.OK, {"compute_mode": app.config.compute_mode})
        elif method == "POST" and path == "/config":
            self._send_json(HTTPStatus.OK, app.update_config(payload))
        elif method == "POST" and path == "/synthesize":
            self._send_json(HTTPStatus.OK, app.synthesize(payload))
        elif method == "GET" and path == "/audio":
            raw = dict(pair.split("=", 1) for pair in parsed.query.split("&") if "=" in pair).get("path", "")
            self._send_file(app.audio_path_is_ours(unquote(raw)), "audio/wav")
        elif method == "POST" and path == "/jobs/cancel":
            self._send_json(HTTPStatus.OK, {"cancelled": app.jobs.cancel()})
        elif method == "POST" and path == "/model/ensure":
            self._send_json(HTTPStatus.OK, {"model": app.ensure_model(payload)})
        elif method == "GET" and path == "/model":
            self._send_json(HTTPStatus.OK, {"model": model_files.check_model(app.paths.models_dir).to_json()})
        elif method == "POST" and path == "/shutdown":
            self._send_json(HTTPStatus.OK, {"shutting_down": True})
            app.shutdown_requested.set()
        else:
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "Rota não encontrada.", "code": "not_found"})

    def do_GET(self) -> None:  # noqa: N802
        self._handle("GET")

    def do_POST(self) -> None:  # noqa: N802
        self._handle("POST")

    def do_PATCH(self) -> None:  # noqa: N802
        self._handle("PATCH")

    def do_DELETE(self) -> None:  # noqa: N802
        self._handle("DELETE")

    def _handle(self, method: str) -> None:
        try:
            self._dispatch(method)
        except CancelledError as error:
            self._send_json(error.status, error.to_json())
        except ServiceError as error:
            self._send_json(error.status, error.to_json())
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as error:
            log.error("Erro interno: %s", "".join(traceback.format_exception(error)).strip()[-2000:])
            try:
                self._send_json(
                    HTTPStatus.INTERNAL_SERVER_ERROR,
                    {"error": f"Erro interno do XTTS: {friendly_error(error)}", "code": "internal"},
                )
            except Exception:
                pass


def create_server(app: ServiceApp, port: int, token: str) -> ThreadingHTTPServer:
    server = ThreadingHTTPServer(("127.0.0.1", port), ApiHandler)
    server.daemon_threads = True
    server.app = app  # type: ignore[attr-defined]
    server.token = token  # type: ignore[attr-defined]
    return server
