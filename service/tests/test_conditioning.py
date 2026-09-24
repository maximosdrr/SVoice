from __future__ import annotations

import sys
import tempfile
import unittest
import wave
from pathlib import Path

SERVICE_DIRECTORY = Path(__file__).resolve().parents[1]
if str(SERVICE_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIRECTORY))

import torch  # noqa: E402

from svoice_xtts.engine import ConfigStore, XttsEngine  # noqa: E402
from svoice_xtts.errors import ServiceError  # noqa: E402
from svoice_xtts.paths import DataPaths  # noqa: E402
from svoice_xtts.profiles import CONDITIONING_FILE  # noqa: E402


def write_wav(path: Path, seconds: float) -> Path:
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(24000)
        output.writeframes(b"\x20\x00" * int(24000 * seconds))
    return path


class FakeRegistry:
    def usable_references(self, profile):
        return list(profile["reference_paths"])


class FakeModel:
    def __init__(self):
        self.calls = 0

    def to(self, device):
        return self

    def get_conditioning_latents(self, **kwargs):
        self.calls += 1
        value = float(self.calls)
        return torch.full((1, 32, 8), value), torch.full((1, 8, 1), value)


class PermanentConditioningCacheTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.paths = DataPaths(Path(self.temp.name) / "data")
        self.paths.ensure()
        self.profile_dir = self.paths.voices_dir / "voice1"
        self.profile_dir.mkdir()
        references = [
            str(write_wav(self.profile_dir / f"reference_{index:04d}.wav", 30))
            for index in range(1, 5)
        ]
        self.profile = {"id": "voice1", "reference_paths": references}
        self.model = FakeModel()
        self.engine = XttsEngine(self.paths, FakeRegistry(), ConfigStore(self.paths.config_path))
        self.engine.torch = torch
        self.engine.model = self.model
        self.engine.device = torch.device("cpu")
        self.engine.loaded_backend = "cpu"

    def tearDown(self) -> None:
        self.temp.cleanup()

    def test_conditioning_is_batched_and_then_loaded_from_permanent_cache(self) -> None:
        latent, embedding = self.engine.conditioning_for_profile(self.profile)
        self.assertEqual(self.model.calls, 2)
        self.assertTrue(torch.allclose(latent, torch.full_like(latent, 1.25)))
        self.assertTrue(torch.allclose(embedding, torch.full_like(embedding, 1.25)))

        cache = self.profile_dir / CONDITIONING_FILE
        self.assertTrue(cache.is_file())
        first_bytes = cache.read_bytes()

        cached_latent, cached_embedding = self.engine.conditioning_for_profile(self.profile)
        self.assertEqual(self.model.calls, 2)
        self.assertTrue(torch.equal(cached_latent, latent))
        self.assertTrue(torch.equal(cached_embedding, embedding))
        self.assertEqual(cache.read_bytes(), first_bytes)
        self.assertFalse(cache.with_suffix(".pt.tmp").exists())

    def test_corrupted_cache_is_preserved_and_reported(self) -> None:
        cache = self.profile_dir / CONDITIONING_FILE
        cache.write_bytes(b"cache-corrompido")

        with self.assertRaises(ServiceError) as captured:
            self.engine.conditioning_for_profile(self.profile)

        self.assertEqual(captured.exception.code, "conditioning_cache_corrupted")
        self.assertEqual(cache.read_bytes(), b"cache-corrompido")
        self.assertEqual(self.model.calls, 0)

    def test_legacy_cache_is_reused_without_expiration(self) -> None:
        cache = self.profile_dir / CONDITIONING_FILE
        expected_latent = torch.full((1, 32, 8), 7.0)
        expected_embedding = torch.full((1, 8, 1), 8.0)
        torch.save(
            {
                "gpt_cond_latent": expected_latent,
                "speaker_embedding": expected_embedding,
                "model": "an-older-model-label",
                "cache_version": 1,
            },
            cache,
        )

        latent, embedding = self.engine.conditioning_for_profile(self.profile)

        self.assertEqual(self.model.calls, 0)
        self.assertTrue(torch.equal(latent, expected_latent))
        self.assertTrue(torch.equal(embedding, expected_embedding))


if __name__ == "__main__":
    unittest.main()
