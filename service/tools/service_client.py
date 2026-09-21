"""Minimal client used by tests, the installer smoke test and diagnostics.

Usage examples::

    python tools/service_client.py --data-dir <dir> health
    python tools/service_client.py --data-dir <dir> e2e --reference voice.wav
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
SERVICE_DIR = HERE.parent
LAUNCHER = SERVICE_DIR / "svoice_xtts_service.py"


class ServiceClient:
    def __init__(self, port: int, token: str):
        self.base = f"http://127.0.0.1:{port}"
        self.token = token

    def request(self, method: str, path: str, body: dict[str, Any] | None = None,
                timeout: float = 30.0) -> dict[str, Any]:
        data = json.dumps(body).encode("utf-8") if body is not None else None
        request = urllib.request.Request(self.base + path, data=data, method=method)
        request.add_header("Authorization", f"Bearer {self.token}")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as error:
            payload = error.read().decode("utf-8", "replace")
            try:
                parsed = json.loads(payload)
            except ValueError:
                parsed = {"error": payload}
            raise ServiceCallError(error.code, parsed) from None

    def request_bytes(self, path: str, timeout: float = 30.0) -> bytes:
        request = urllib.request.Request(self.base + path)
        request.add_header("Authorization", f"Bearer {self.token}")
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.read()


class ServiceCallError(Exception):
    def __init__(self, status: int, payload: dict[str, Any]):
        super().__init__(f"HTTP {status}: {payload.get('error')} [{payload.get('code')}]")
        self.status = status
        self.payload = payload


def launch_service(data_dir: Path, python: str | None = None, extra: list[str] | None = None,
                   timeout: float = 120.0) -> tuple[subprocess.Popen, dict[str, Any]]:
    python = python or sys.executable
    command = [python, str(LAUNCHER), "--data-dir", str(data_dir), "--print-discovery",
               "--idle-timeout", "0", *(extra or [])]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                               encoding="utf-8", errors="replace")
    deadline = time.monotonic() + timeout
    discovery: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        line = process.stdout.readline() if process.stdout else ""
        if line:
            try:
                candidate = json.loads(line)
                if isinstance(candidate, dict) and "port" in candidate:
                    discovery = candidate
                    break
            except ValueError:
                continue
        if process.poll() is not None:
            break
    if discovery is None:
        stderr = process.stderr.read() if process.stderr else ""
        raise RuntimeError(f"O serviço não iniciou: {stderr[-2000:]}")
    # Drain pipes so the child never blocks on a full buffer.
    for stream in (process.stdout, process.stderr):
        threading.Thread(target=lambda s=stream: [None for _ in iter(s.readline, "")], daemon=True).start()
    client = ServiceClient(discovery["port"], discovery["token"])
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            client.request("GET", "/health", timeout=5)
            return process, discovery
        except (OSError, ServiceCallError):
            time.sleep(0.3)
    raise RuntimeError("O serviço iniciou, mas /health não respondeu.")


def wait_job(client: ServiceClient, kind: str, timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        health = client.request("GET", "/health", timeout=5)
        job = health.get("job") or {}
        if not health.get("busy"):
            return
        print(f"  [{job.get('kind')}] {job.get('message')} {job.get('progress')}")
        time.sleep(1)
    raise TimeoutError(kind)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-dir", type=Path, required=True)
    parser.add_argument("--python")
    parser.add_argument("--backend", default="cuda")
    parser.add_argument("--service-arg", action="append", default=[], help="Argumento extra para o serviço (repetível)")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("health")
    sub.add_parser("diagnostics")
    e2e = sub.add_parser("e2e")
    e2e.add_argument("--reference", type=Path)
    e2e.add_argument("--text", default="Olá, este é um teste do SVoice com síntese por voz clonada. Funcionou?")
    e2e.add_argument("--output", type=Path)
    e2e.add_argument("--keep-profile", action="store_true")
    args = parser.parse_args(argv)

    process, discovery = launch_service(args.data_dir, args.python, args.service_arg)
    client = ServiceClient(discovery["port"], discovery["token"])
    started = time.perf_counter()
    try:
        if args.command == "health":
            print(json.dumps(client.request("GET", "/health"), ensure_ascii=False, indent=1))
            return 0
        if args.command == "diagnostics":
            print(json.dumps(client.request("GET", "/diagnostics", timeout=120), ensure_ascii=False, indent=1))
            return 0

        health = client.request("GET", "/health")
        print("health:", health["state"], health["message"], "| model_ready:", health["model_ready"])
        print("profiles before:", [p["name"] for p in client.request("GET", "/profiles")["profiles"]])

        print(f"== test backend {args.backend}")
        report = client.request("POST", "/diagnostics/test", {"backend": args.backend}, timeout=1800)["report"]
        print(json.dumps({k: v for k, v in report.items() if k != "checks"}, ensure_ascii=False))
        for check in report["checks"]:
            print("  check:", check)

        profile_id = None
        if args.reference:
            print("== create profile")
            created = client.request("POST", "/profiles", {"name": "E2E teste", "reference_paths": [str(args.reference.resolve())]}, timeout=1800)
            profile_id = created["profile"]["id"]
            print("  created:", created["profile"])
        else:
            profiles = client.request("GET", "/profiles")["profiles"]
            usable = [p for p in profiles if p["status"] == "ok"]
            if not usable:
                raise SystemExit("Nenhum perfil utilizável; informe --reference.")
            profile_id = usable[0]["id"]
            print("  using profile:", usable[0]["name"])

        print("== synthesize")
        t0 = time.perf_counter()
        result = client.request("POST", "/synthesize", {"text": args.text, "profile_id": profile_id, "speed": 1.0}, timeout=1800)
        print(f"  {json.dumps(result, ensure_ascii=False)} ({time.perf_counter() - t0:.1f}s)")
        audio = client.request_bytes("/audio?path=" + urllib.request.quote(result["output_path"]))
        print("  audio bytes:", len(audio))
        if args.output:
            args.output.write_bytes(audio)
            print("  saved:", args.output)

        print("== cancel during long synthesis")
        long_text = " ".join(["Esta é uma frase bastante longa para testar o cancelamento entre sentenças."] * 6)
        outcome: dict[str, Any] = {}

        def run_long() -> None:
            try:
                outcome["result"] = client.request("POST", "/synthesize", {"text": long_text, "profile_id": profile_id}, timeout=600)
            except ServiceCallError as error:
                outcome["error"] = error

        worker = threading.Thread(target=run_long)
        worker.start()
        time.sleep(2.5)
        print("  cancel:", client.request("POST", "/jobs/cancel"))
        worker.join(600)
        print("  outcome:", outcome.get("error") or outcome.get("result"))

        print("== rename / delete")
        renamed = client.request("PATCH", f"/profiles/{profile_id}", {"name": "E2E renomeado"})
        print("  renamed:", renamed["profile"]["name"])
        if args.reference and not args.keep_profile:
            print("  delete:", client.request("DELETE", f"/profiles/{profile_id}"))
        else:
            client.request("PATCH", f"/profiles/{profile_id}", {"name": "Voice 1" if not args.reference else "E2E teste"})
        print("profiles after:", [p["name"] for p in client.request("GET", "/profiles")["profiles"]])
        print(f"== done in {time.perf_counter() - started:.1f}s")
        return 0
    finally:
        try:
            client.request("POST", "/shutdown", timeout=5)
        except Exception:
            pass
        try:
            process.wait(15)
        except subprocess.TimeoutExpired:
            process.kill()


if __name__ == "__main__":
    raise SystemExit(main())
