from __future__ import annotations

import tempfile
import unittest
import wave
from pathlib import Path
import sys

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

    def test_compute_mode_is_persisted(self) -> None:
        self.service.update_config({"compute_mode": "cpu"})
        restarted = SVoiceXttsService(self.root / "data")
        health = restarted.health()
        self.assertEqual(health["compute_mode"], "cpu")
        self.assertFalse(health["hardware_checked"])


if __name__ == "__main__":
    unittest.main()
