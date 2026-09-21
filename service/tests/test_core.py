from __future__ import annotations

import hashlib
import io
import json
import sys
import tempfile
import threading
import unittest
from email.message import Message
from pathlib import Path
from unittest.mock import patch

SERVICE_DIRECTORY = Path(__file__).resolve().parents[1]
if str(SERVICE_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIRECTORY))

from svoice_xtts import backends, model, runtime  # noqa: E402
from svoice_xtts.api import ApiHandler  # noqa: E402
from svoice_xtts.engine import ConfigStore, sanitize_text, split_into_chunks  # noqa: E402
from svoice_xtts.errors import CancelledError, ServiceError  # noqa: E402
from svoice_xtts.hardware import GpuInfo, SystemInfo  # noqa: E402
from svoice_xtts.jobs import JobTracker  # noqa: E402


class ApiPayloadTests(unittest.TestCase):
    @staticmethod
    def _handler(headers: dict[str, str], content: bytes) -> ApiHandler:
        handler = object.__new__(ApiHandler)
        handler.headers = Message()
        for name, value in headers.items():
            handler.headers[name] = value
        handler.rfile = io.BytesIO(content)
        return handler

    def test_reads_json_with_content_length(self) -> None:
        content = '{"name":"Voz ç","reference_paths":["C:/voz.mp3"]}'.encode()
        payload = self._handler({"Content-Length": str(len(content))}, content)._read_payload()
        self.assertEqual(payload["name"], "Voz ç")

    def test_reads_chunked_json(self) -> None:
        content = '{"name":"Voz ç"}'.encode()
        parts = (content[:5], content[5:])
        encoded = b"".join(f"{len(p):X}\r\n".encode() + p + b"\r\n" for p in parts) + b"0\r\n\r\n"
        payload = self._handler({"Transfer-Encoding": "chunked"}, encoded)._read_payload()
        self.assertEqual(payload["name"], "Voz ç")

    def test_rejects_malformed_requests(self) -> None:
        cases = (
            ({"Content-Length": "10"}, b"{}"),
            ({"Content-Length": "invalid"}, b"{}"),
            ({"Transfer-Encoding": "gzip"}, b"{}"),
            ({"Content-Length": "2"}, b"[]"),
        )
        for headers, content in cases:
            with self.subTest(headers=headers), self.assertRaises(ServiceError):
                self._handler(headers, content)._read_payload()

    def test_rejects_oversized_request(self) -> None:
        with self.assertRaises(ServiceError) as captured:
            self._handler({"Content-Length": "1000001"}, b"")._read_payload()
        self.assertEqual(captured.exception.status, 413)


class JobTrackerTests(unittest.TestCase):
    def test_single_slot_and_cancel(self) -> None:
        tracker = JobTracker()
        job = tracker.start("synthesizing", "a")
        with self.assertRaises(ServiceError) as busy:
            tracker.start("cloning", "b")
        self.assertEqual(busy.exception.code, "busy")
        self.assertTrue(tracker.cancel())
        with self.assertRaises(CancelledError):
            job.check_cancelled()
        tracker.finish(job, error=CancelledError())
        self.assertEqual(tracker.snapshot()["state"], "cancelled")
        self.assertFalse(tracker.cancel())
        self.assertFalse(tracker.busy)

    def test_progress_is_clamped(self) -> None:
        tracker = JobTracker()
        job = tracker.start("cloning", "a")
        job.update("x", 7)
        self.assertEqual(job.progress, 1.0)
        tracker.finish(job)
        self.assertEqual(tracker.snapshot()["state"], "done")


class TextTests(unittest.TestCase):
    def test_sanitize(self) -> None:
        self.assertEqual(sanitize_text("Olá\u2026  mundo\x00\n"), "Olá... mundo")

    def test_chunking_groups_sentences(self) -> None:
        text = "Primeira frase. Segunda frase! Terceira? " + "x" * 200 + ". Fim."
        chunks = split_into_chunks(text, 60)
        self.assertEqual(chunks[0], "Primeira frase. Segunda frase! Terceira?")
        self.assertTrue(all(len(chunk) <= 60 for chunk in chunks))
        self.assertEqual(chunks[-1], "Fim.")

    def test_chunking_keeps_short_text_whole(self) -> None:
        self.assertEqual(split_into_chunks("Oi! Tudo bem?", 150), ["Oi! Tudo bem?"])
        self.assertEqual(split_into_chunks("   ", 150), [])


class ConfigStoreTests(unittest.TestCase):
    def test_reads_and_persists_compute_mode(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.json"
            path.write_text(json.dumps({"compute_mode": "cuda"}), encoding="utf-8")
            config = ConfigStore(path)
            self.assertEqual(config.compute_mode, "cuda")
            config.data["compute_mode"] = "cpu"
            config.save()
            self.assertEqual(json.loads(path.read_text(encoding="utf-8"))["compute_mode"], "cpu")

    def test_corrupt_config_uses_safe_defaults(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "config.json"
            path.write_text("{invalid", encoding="utf-8")
            config = ConfigStore(path)
            self.assertEqual(config.compute_mode, "auto")


class BackendSelectionTests(unittest.TestCase):
    def test_mode_normalization(self) -> None:
        self.assertEqual(backends.normalize_compute_mode("gpu"), "auto")
        self.assertEqual(backends.normalize_compute_mode("CUDA"), "cuda")
        self.assertEqual(backends.normalize_compute_mode("bogus"), "auto")
        self.assertEqual(backends.normalize_compute_mode(None), "auto")

    def test_candidates(self) -> None:
        availability = {
            "cuda": backends.BackendAvailability("cuda", True, ""),
            "directml": backends.BackendAvailability("directml", False, ""),
            "rocm": backends.BackendAvailability("rocm", False, ""),
            "cpu": backends.BackendAvailability("cpu", True, ""),
        }
        self.assertEqual(backends.candidates_for_mode("auto", availability), ["cuda", "cpu"])
        self.assertEqual(backends.candidates_for_mode("cpu", availability), ["cpu"])
        self.assertEqual(backends.candidates_for_mode("directml", availability), ["cpu"])
        self.assertEqual(backends.candidates_for_mode("cuda", availability), ["cuda", "cpu"])


class RuntimeSelectionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        for name in ("base", "torch-cpu", "torch-cuda", "torch-directml"):
            pack = self.root / "packs" / name
            pack.mkdir(parents=True)
            (pack / ".svoice-pack.json").write_text(json.dumps({"name": name, "version": "1"}))
        self.layout = runtime.discover_layout(self.root)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _system(self, vendor: str | None) -> SystemInfo:
        gpus = [GpuInfo("GPU", vendor)] if vendor else []
        return SystemInfo("win", "AMD64", "3.12", 8, None, gpus)

    def test_nvidia_prefers_cuda(self) -> None:
        pack, _ = runtime.choose_torch_pack(self.layout, self._system("nvidia"), {"compute_mode": "auto"})
        self.assertEqual(pack, "torch-cuda")

    def test_amd_prefers_directml_when_rocm_missing(self) -> None:
        pack, _ = runtime.choose_torch_pack(self.layout, self._system("amd"), {"compute_mode": "auto"})
        self.assertEqual(pack, "torch-directml")

    def test_failed_validation_skips_gpu_pack(self) -> None:
        config = {"compute_mode": "auto", "backend_validation": {"cuda": {"ok": False}}}
        pack, reason = runtime.choose_torch_pack(self.layout, self._system("nvidia"), config)
        self.assertEqual(pack, "torch-cpu")
        self.assertIn("falhou na validação anterior", reason)

    def test_no_gpu_uses_cpu(self) -> None:
        pack, _ = runtime.choose_torch_pack(self.layout, self._system(None), {})
        self.assertEqual(pack, "torch-cpu")

    def test_forced_mode_and_missing_pack(self) -> None:
        pack, _ = runtime.choose_torch_pack(self.layout, self._system("amd"), {"compute_mode": "cuda"})
        self.assertEqual(pack, "torch-cuda")
        import shutil

        shutil.rmtree(self.root / "packs" / "torch-cuda")
        layout = runtime.discover_layout(self.root)
        pack, reason = runtime.choose_torch_pack(layout, self._system("nvidia"), {"compute_mode": "cuda"})
        self.assertEqual(pack, "torch-cpu")
        self.assertIn("não instalado", reason)

    def test_dev_layout_uses_interpreter(self) -> None:
        layout = runtime.discover_layout(None)
        self.assertFalse(layout.is_packaged)
        self.assertEqual(runtime.choose_torch_pack(layout, self._system("nvidia"), {})[0], None)


class ModelCheckTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.models = Path(self.temp.name)
        self.directory = model.model_directory(self.models)
        self.directory.mkdir(parents=True)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _fake_files(self) -> list[model.ModelFile]:
        files = []
        for index, name in enumerate(("model.pth", "config.json")):
            content = f"content-{index}".encode() * 10
            (self.directory / name).write_bytes(content)
            files.append(model.ModelFile(name, len(content), hashlib.sha256(content).hexdigest()))
        return files

    def test_missing_and_corrupted_detection(self) -> None:
        files = self._fake_files()
        with patch.object(model, "MODEL_FILES", tuple(files)):
            status = model.check_model(self.models, full_hash=True)
            self.assertTrue(status.ready)
            self.assertIsNotNone(status.verified_at)
            cached = model.check_model(self.models)
            self.assertTrue(cached.ready)
            (self.directory / "config.json").write_bytes(b"x")
            broken = model.check_model(self.models)
            self.assertEqual(broken.corrupted, ["config.json"])
            (self.directory / "config.json").unlink()
            self.assertEqual(model.check_model(self.models).missing, ["config.json"])

    def test_ensure_without_download_raises(self) -> None:
        with patch.object(model, "MODEL_FILES", (model.ModelFile("model.pth", 3, "0" * 64),)):
            with self.assertRaises(ServiceError) as captured:
                model.ensure_model(self.models, allow_download=False)
            self.assertEqual(captured.exception.code, "model_missing")

    def test_download_verifies_hash(self) -> None:
        content = b"hello model"
        good = model.ModelFile("model.pth", len(content), hashlib.sha256(content).hexdigest())

        class FakeResponse(io.BytesIO):
            status = 200

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        with patch.object(model, "MODEL_FILES", (good,)), patch.object(
            model.urllib.request, "urlopen", lambda request, timeout=60: FakeResponse(content)
        ):
            status = model.ensure_model(self.models)
            self.assertTrue(status.ready)
            self.assertEqual((self.directory / "model.pth").read_bytes(), content)

        bad = model.ModelFile("model.pth", len(content), "f" * 64)
        (self.directory / "model.pth").unlink()
        with patch.object(model, "MODEL_FILES", (bad,)), patch.object(
            model.urllib.request, "urlopen", lambda request, timeout=60: FakeResponse(content)
        ):
            with self.assertRaises(ServiceError) as captured:
                model.ensure_model(self.models)
            self.assertEqual(captured.exception.code, "model_corrupted")


class ServiceErrorTests(unittest.TestCase):
    def test_json_shape(self) -> None:
        error = ServiceError("x", 404, code="profile_not_found", action="y")
        self.assertEqual(error.to_json(), {"error": "x", "code": "profile_not_found", "action": "y"})
        self.assertEqual(CancelledError().status, 499)


if __name__ == "__main__":
    unittest.main()
