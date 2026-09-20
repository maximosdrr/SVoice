from __future__ import annotations

import io
import tempfile
import unittest
import wave
from email.message import Message
from pathlib import Path
import subprocess
import sys
from unittest.mock import Mock, patch

SERVICE_DIRECTORY = Path(__file__).resolve().parents[1]
if str(SERVICE_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIRECTORY))

from service import ApiHandler, ServiceError, SVoiceXttsService


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
        handler = self._handler({"Content-Length": str(len(content))}, content)

        payload = handler._read_payload()

        self.assertEqual(payload["name"], "Voz ç")
        self.assertEqual(payload["reference_paths"], ["C:/voz.mp3"])

    def test_reads_chunked_json_for_compatibility(self) -> None:
        content = '{"name":"Voz ç","reference_paths":["C:/voz.mp3"]}'.encode()
        parts = (content[:7], content[7:19], content[19:])
        encoded = b"".join(
            f"{len(part):X}\r\n".encode() + part + b"\r\n" for part in parts
        ) + b"0\r\n\r\n"
        handler = self._handler({"Transfer-Encoding": "chunked"}, encoded)

        payload = handler._read_payload()

        self.assertEqual(payload["name"], "Voz ç")
        self.assertEqual(payload["reference_paths"], ["C:/voz.mp3"])

    def test_rejects_truncated_and_malformed_requests(self) -> None:
        cases = (
            ({"Content-Length": "10"}, b"{}"),
            ({"Content-Length": "invalid"}, b"{}"),
            ({"Transfer-Encoding": "gzip"}, b"{}"),
            ({"Transfer-Encoding": "chunked"}, b"G\r\n{}\r\n0\r\n\r\n"),
        )
        for headers, content in cases:
            with self.subTest(headers=headers), self.assertRaises(ServiceError):
                self._handler(headers, content)._read_payload()

    def test_rejects_oversized_request_before_reading_it(self) -> None:
        handler = self._handler({"Content-Length": "1000001"}, b"")

        with self.assertRaises(ServiceError) as captured:
            handler._read_payload()

        self.assertEqual(captured.exception.status, 413)


class ProfileRegistryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_directory.name)
        self.reference = self.root / "reference.wav"
        with wave.open(str(self.reference), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(24000)
            audio.writeframes(b"\0\0" * (24000 * 4))
        self.service = SVoiceXttsService(self.root / "data")

    def _create_wav(
        self, name: str, duration_seconds: int, sample_rate: int = 100
    ) -> Path:
        path = self.root / name
        with wave.open(str(path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(sample_rate)
            audio.writeframes(b"\0\0" * (sample_rate * duration_seconds))
        return path

    def tearDown(self) -> None:
        self.temp_directory.cleanup()

    def test_profile_lifecycle_without_confirmation_fields(self) -> None:
        profile = self.service.add_profile(
            {
                "name": "Minha voz",
                "reference_paths": [str(self.reference)],
            }
        )

        self.assertEqual(profile["name"], "Minha voz")
        self.assertGreaterEqual(profile["duration_seconds"], 4)
        self.assertEqual(len(self.service.list_profiles()), 1)

        self.service.delete_profile(profile["id"])
        self.assertEqual(self.service.list_profiles(), [])

    def test_profile_name_is_optional_and_derived_from_file(self) -> None:
        profile = self.service.add_profile(
            {"reference_paths": [str(self.reference)]}
        )

        self.assertEqual(profile["name"], "reference")

    def test_duplicate_automatic_names_receive_a_suffix(self) -> None:
        first = self.service.add_profile(
            {"reference_paths": [str(self.reference)]}
        )
        second = self.service.add_profile(
            {"name": "   ", "reference_paths": [str(self.reference)]}
        )

        self.assertEqual(first["name"], "reference")
        self.assertEqual(second["name"], "reference (2)")

    def test_duplicate_source_paths_are_processed_once(self) -> None:
        profile = self.service.add_profile(
            {
                "reference_paths": [str(self.reference), str(self.reference)],
            }
        )

        self.assertEqual(profile["source_count"], 1)
        self.assertEqual(profile["reference_count"], 1)

    def test_rejects_missing_or_invalid_reference_paths(self) -> None:
        empty_reference = self.root / "empty.wav"
        empty_reference.touch()
        invalid_payloads = (
            {},
            {"reference_paths": []},
            {"reference_paths": "reference.wav"},
            {"reference_paths": [""]},
            {"reference_paths": [str(self.root / "missing.wav")]},
            {"reference_paths": [str(empty_reference)]},
        )

        for payload in invalid_payloads:
            with self.subTest(payload=payload), self.assertRaises(ServiceError):
                self.service.add_profile(payload)

    def test_long_requested_name_is_safely_shortened(self) -> None:
        profile = self.service.add_profile(
            {
                "name": "V" * 120,
                "reference_paths": [str(self.reference)],
            }
        )

        self.assertEqual(len(profile["name"]), 80)

    def test_accepts_more_than_five_source_files(self) -> None:
        references = [self._create_wav(f"reference_{index}.wav", 4) for index in range(6)]
        profile = self.service.add_profile(
            {
                "name": "Vários áudios",
                "reference_paths": [str(path) for path in references],
            }
        )

        self.assertEqual(profile["source_count"], 6)
        self.assertEqual(profile["reference_count"], 6)
        self.assertFalse(profile["truncated"])

    def test_splits_and_truncates_audio_at_duration_limit(self) -> None:
        long_reference = self._create_wav("long_reference.wav", 50)
        with (
            patch("service.MAX_REFERENCE_DURATION_SECONDS", 45),
            patch("service.REFERENCE_CHUNK_SECONDS", 20),
        ):
            profile = self.service.add_profile(
                {
                    "name": "Áudio longo",
                    "reference_paths": [str(long_reference)],
                }
            )

        self.assertEqual(profile["reference_count"], 3)
        self.assertAlmostEqual(profile["duration_seconds"], 45, delta=0.1)
        self.assertTrue(profile["truncated"])
        self.assertEqual(list(self.service.temp_dir.glob("import_*")), [])

    def test_accepts_m4a_using_integrated_converter(self) -> None:
        import imageio_ffmpeg

        compressed_reference = self.root / "reference.m4a"
        result = subprocess.run(
            [
                imageio_ffmpeg.get_ffmpeg_exe(),
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                str(self.reference),
                "-c:a",
                "aac",
                str(compressed_reference),
            ],
            capture_output=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr.decode(errors="replace"))

        profile = self.service.add_profile(
            {
                "name": "Referência M4A",
                "reference_paths": [str(compressed_reference)],
            }
        )

        self.assertGreaterEqual(profile["duration_seconds"], 3.9)
        self.assertEqual(profile["reference_count"], 1)

    def test_conditioning_uses_all_processed_chunks(self) -> None:
        class FakeTensor:
            def detach(self):
                return self

            def cpu(self):
                return self

        profile = self.service.add_profile(
            {
                "name": "Condicionamento completo",
                "reference_paths": [str(self.reference)],
            }
        )
        stored_profile = next(
            item
            for item in self.service._profiles["profiles"]
            if item["id"] == profile["id"]
        )
        fake_model = Mock()
        fake_model.get_conditioning_latents.return_value = (
            FakeTensor(),
            FakeTensor(),
        )
        fake_torch = Mock()
        self.service._torch = fake_torch

        self.service._conditioning_for_profile(stored_profile, fake_model, "cpu")

        fake_model.get_conditioning_latents.assert_called_once_with(
            audio_path=stored_profile["reference_paths"],
            max_ref_length=20,
            gpt_cond_len=-1,
            gpt_cond_chunk_len=6,
        )
        fake_torch.save.assert_called_once()

    def test_compute_mode_is_persisted(self) -> None:
        self.service.update_config({"compute_mode": "cpu"})
        restarted = SVoiceXttsService(self.root / "data")
        health = restarted.health()
        self.assertEqual(health["compute_mode"], "cpu")
        self.assertFalse(health["hardware_checked"])


if __name__ == "__main__":
    unittest.main()
