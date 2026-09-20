from __future__ import annotations

import tempfile
import unittest
import wave
from pathlib import Path
import subprocess
import sys
from unittest.mock import Mock, patch

SERVICE_DIRECTORY = Path(__file__).resolve().parents[1]
if str(SERVICE_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIRECTORY))

from service import SVoiceXttsService


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
