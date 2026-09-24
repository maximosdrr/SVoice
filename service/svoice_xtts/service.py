"""Service entry point: single instance, discovery file, idle shutdown."""

from __future__ import annotations

import argparse
import ctypes
import json
import os
import secrets
import socket
import sys
import tempfile
import threading
import time
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from . import PROTOCOL_VERSION, SERVICE_VERSION
from .api import ServiceApp, create_server
from .hardware import detect_system
from .logging_setup import configure_logging, get_logger
from .paths import DataPaths, default_root, restrict_to_current_user

MUTEX_NAME = "Local\\SVoice.XttsService"
ERROR_ALREADY_EXISTS = 183
EXIT_ALREADY_RUNNING = 3
DEFAULT_IDLE_TIMEOUT = 15 * 60


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="svoice-xtts-service", add_help=True)
    parser.add_argument("--port", type=int, default=0, help="Porta loopback (0 = automática)")
    parser.add_argument("--token", help="Token Bearer (gerado quando omitido)")
    parser.add_argument("--data-dir", type=Path, help="Diretório de dados (padrão: %%LOCALAPPDATA%%\\SVoice\\XTTS)")
    parser.add_argument("--idle-timeout", type=int, default=DEFAULT_IDLE_TIMEOUT,
                        help="Segundos sem requisições até encerrar (0 = nunca)")
    parser.add_argument("--log-level", default="INFO")
    parser.add_argument("--self-test", action="store_true", help="Valida as dependências e sai")
    parser.add_argument("--print-discovery", action="store_true",
                        help="Imprime o arquivo de descoberta em JSON na saída padrão ao iniciar")
    parser.add_argument("--no-single-instance", action="store_true", help=argparse.SUPPRESS)
    return parser.parse_args(argv)


class SingleInstance:
    def __init__(self, name: str = MUTEX_NAME):
        self.name = name
        self.handle: int | None = None

    def acquire(self) -> bool:
        if sys.platform != "win32":
            return True
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.CreateMutexW(None, True, self.name)
        if not handle:
            return True
        if kernel32.GetLastError() == ERROR_ALREADY_EXISTS:
            kernel32.CloseHandle(handle)
            return False
        self.handle = handle
        return True

    def release(self) -> None:
        if self.handle and sys.platform == "win32":
            ctypes.windll.kernel32.ReleaseMutex(self.handle)
            ctypes.windll.kernel32.CloseHandle(self.handle)
            self.handle = None


def reserve_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def write_discovery(paths: DataPaths, port: int, token: str, runtime_info: dict[str, Any]) -> dict[str, Any]:
    payload = {
        "pid": os.getpid(),
        "port": port,
        "token": token,
        "service_version": SERVICE_VERSION,
        "protocol_version": PROTOCOL_VERSION,
        "started_at": datetime.now(UTC).isoformat(),
        "runtime": runtime_info,
    }
    path = paths.discovery_path
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(payload), encoding="utf-8")
    temporary.replace(path)
    restrict_to_current_user(path)
    return payload


def read_discovery(paths: DataPaths) -> dict[str, Any] | None:
    try:
        payload = json.loads(paths.discovery_path.read_text(encoding="utf-8"))
        return payload if isinstance(payload, dict) else None
    except (OSError, ValueError):
        return None


def wait_for_discovery(paths: DataPaths, timeout: float) -> dict[str, Any] | None:
    """Poll the discovery file until it names a live process or ``timeout`` passes."""
    deadline = time.monotonic() + timeout
    while True:
        existing = read_discovery(paths)
        if existing and _pid_alive(existing.get("pid")):
            return existing
        if time.monotonic() >= deadline:
            return existing
        time.sleep(0.25)


def _pid_alive(pid: Any) -> bool:
    if not isinstance(pid, int) or pid <= 0:
        return False
    if sys.platform != "win32":
        try:
            os.kill(pid, 0)
            return True
        except OSError:
            return False
    kernel32 = ctypes.windll.kernel32
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return False
    try:
        exit_code = ctypes.c_ulong()
        if kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
            return exit_code.value == 259  # STILL_ACTIVE
        return False
    finally:
        kernel32.CloseHandle(handle)


def remove_discovery(paths: DataPaths) -> None:
    try:
        current = read_discovery(paths)
        if current is None or current.get("pid") == os.getpid():
            paths.discovery_path.unlink(missing_ok=True)
    except OSError:
        pass


def runtime_self_test() -> None:
    """Import the heavy dependencies and round-trip a WAV file."""
    from .engine import _configure_audio_io

    from transformers import (  # noqa: F401
        GPT2Config,
        GPT2PreTrainedModel,
        GenerationMixin,
        LogitsProcessorList,
    )
    from TTS.tts.configs.xtts_config import XttsConfig  # noqa: F401
    from TTS.tts.models.xtts import Xtts  # noqa: F401
    import imageio_ffmpeg
    import numpy as np
    import onnxruntime  # noqa: F401
    import torch
    import torchaudio
    from silero_vad import get_speech_timestamps_sequence, load_silero_vad

    imageio_ffmpeg.get_ffmpeg_exe()
    _configure_audio_io(torch, torchaudio)
    with tempfile.TemporaryDirectory(prefix="svoice_self_test_") as directory:
        test_audio = Path(directory) / "roundtrip.wav"
        torchaudio.save(str(test_audio), torch.zeros(1, 240), 24000)
        waveform, sample_rate = torchaudio.load(str(test_audio))
        if sample_rate != 24000 or tuple(waveform.shape) != (1, 240):
            raise RuntimeError("Falha no autoteste de leitura e gravação de áudio.")
        vad = load_silero_vad(sequence=True, sampling_rate=16000)
        timestamps = get_speech_timestamps_sequence(
            np.zeros(16000, dtype=np.float32),
            vad,
            sampling_rate=16000,
            return_seconds=True,
        )
        if timestamps:
            raise RuntimeError("O autoteste do detector de voz retornou fala em silêncio digital.")
    print(json.dumps({"ok": True, "torch": torch.__version__, "cuda": torch.version.cuda,
                      "service_version": SERVICE_VERSION}))


def main(argv: list[str] | None = None, runtime_info: dict[str, Any] | None = None) -> int:
    args = parse_args(argv)
    runtime_info = dict(runtime_info or {})
    if args.self_test:
        runtime_self_test()
        return 0

    data_dir = (args.data_dir or (default_root() / "XTTS")).expanduser().resolve()
    paths = DataPaths(data_dir)
    paths.ensure()
    log = configure_logging(paths.logs_dir, args.log_level)
    log.info("SVoice XTTS service %s iniciando (pid %d, runtime %s)", SERVICE_VERSION, os.getpid(),
             runtime_info.get("torch_pack", "dev"))

    if args.token is not None and len(args.token) < 32:
        log.error("Token inválido (mínimo 32 caracteres).")
        return 2
    token = args.token or secrets.token_hex(32)

    instance = SingleInstance()
    if not args.no_single_instance and not instance.acquire():
        # The other instance may still be binding its port; give it time to
        # publish the discovery file so callers can adopt it instead of failing.
        existing = wait_for_discovery(paths, timeout=30.0)
        log.warning("Outra instância do serviço já está em execução: %s", existing and existing.get("pid"))
        if args.print_discovery and existing:
            print(json.dumps({"already_running": True, **existing}), flush=True)
        return EXIT_ALREADY_RUNNING

    system = detect_system()
    app = ServiceApp(paths, system, runtime_info)
    port = args.port or reserve_port()
    try:
        server = create_server(app, port, token)
    except OSError as error:
        log.error("Não foi possível abrir a porta %d: %s", port, error)
        instance.release()
        return 4
    port = int(server.server_address[1])
    discovery = write_discovery(paths, port, token, runtime_info)
    if args.print_discovery:
        print(json.dumps(discovery), flush=True)
    log.info("Escutando em 127.0.0.1:%d", port)

    stop = threading.Event()

    def serve() -> None:
        try:
            server.serve_forever(poll_interval=0.25)
        finally:
            stop.set()

    thread = threading.Thread(target=serve, name="http", daemon=True)
    thread.start()

    exit_code = 0
    try:
        while not stop.is_set():
            if app.shutdown_requested.wait(1.0):
                log.info("Encerramento solicitado pelo cliente.")
                break
            if args.idle_timeout > 0 and not app.jobs.busy:
                idle = time.monotonic() - app.jobs.last_activity
                if idle >= args.idle_timeout:
                    log.info("Encerrando após %.0f s de inatividade.", idle)
                    break
    except KeyboardInterrupt:
        exit_code = 0
    finally:
        try:
            app.jobs.cancel()
            server.shutdown()
            server.server_close()
        except Exception:
            pass
        app.engine.unload()
        remove_discovery(paths)
        instance.release()
        log.info("Serviço encerrado.")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
