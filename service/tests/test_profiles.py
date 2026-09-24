from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import wave
from pathlib import Path
from unittest.mock import patch

SERVICE_DIRECTORY = Path(__file__).resolve().parents[1]
if str(SERVICE_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(SERVICE_DIRECTORY))

from svoice_xtts import audio  # noqa: E402
from svoice_xtts.errors import CancelledError, ServiceError  # noqa: E402
from svoice_xtts.paths import DATA_SCHEMA_VERSION, DataPaths  # noqa: E402
from svoice_xtts.profiles import ProfileRegistry, cleanup_temp_files  # noqa: E402


def write_wav(path: Path, seconds: float, sample_rate: int = 24000) -> Path:
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(b"\x10\x00" * int(sample_rate * seconds))
    return path


def ffmpeg_available() -> bool:
    try:
        audio.ffmpeg_executable()
        return True
    except ServiceError:
        return False


class RegistryFixture(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.paths = DataPaths(self.root / "data")
        self.paths.ensure()
        self.reference = write_wav(self.root / "reference.wav", 4)

    def tearDown(self) -> None:
        self.temp.cleanup()


@unittest.skipUnless(ffmpeg_available(), "imageio-ffmpeg não disponível")
class ProfileLifecycleTests(RegistryFixture):
    def setUp(self) -> None:
        super().setUp()
        self.speech_detector = patch.object(
            audio,
            "detect_speech_intervals",
            side_effect=lambda path, **kwargs: [(0.0, audio.audio_duration(path))],
        )
        self.speech_detector.start()

    def tearDown(self) -> None:
        self.speech_detector.stop()
        super().tearDown()

    def test_create_list_rename_delete(self) -> None:
        registry = ProfileRegistry(self.paths)
        messages: list[str] = []
        profile = registry.create(
            {"name": "Minha voz", "reference_paths": [str(self.reference)]},
            progress=lambda message, value: messages.append(message),
        )
        self.assertEqual(profile["name"], "Minha voz")
        self.assertGreaterEqual(profile["duration_seconds"], 3.9)
        self.assertTrue(messages)
        listed = registry.list()
        self.assertEqual(len(listed), 1)
        self.assertEqual(listed[0]["status"], "ok")
        self.assertEqual(listed[0]["missing_references"], 0)

        renamed = registry.rename(profile["id"], "Outra")
        self.assertEqual(renamed["name"], "Outra")
        with self.assertRaises(ServiceError):
            registry.rename(profile["id"], "   ")

        registry.delete(profile["id"])
        self.assertEqual(registry.list(), [])
        self.assertFalse((self.paths.voices_dir / profile["id"]).exists())

    def test_name_is_optional_and_deduplicated(self) -> None:
        registry = ProfileRegistry(self.paths)
        first = registry.create({"reference_paths": [str(self.reference)]})
        second = registry.create({"name": "  ", "reference_paths": [str(self.reference)]})
        self.assertEqual(first["name"], "reference")
        self.assertEqual(second["name"], "reference (2)")

    def test_duplicate_sources_processed_once(self) -> None:
        registry = ProfileRegistry(self.paths)
        profile = registry.create({"reference_paths": [str(self.reference), str(self.reference)]})
        self.assertEqual(profile["source_count"], 1)
        self.assertEqual(len(profile["reference_paths"]), 1)

    def test_cancel_during_import_cleans_up(self) -> None:
        registry = ProfileRegistry(self.paths)

        def cancelled() -> None:
            raise CancelledError()

        with self.assertRaises(CancelledError):
            registry.create({"reference_paths": [str(self.reference)]}, check_cancelled=cancelled)
        self.assertEqual(registry.list(), [])
        self.assertEqual(list(self.paths.voices_dir.iterdir()), [])
        self.assertEqual(list(self.paths.temp_dir.iterdir()), [])

    def test_too_short_reference_is_rejected(self) -> None:
        registry = ProfileRegistry(self.paths)
        short = write_wav(self.root / "short.wav", 1)
        with self.assertRaises(ServiceError) as captured:
            registry.create({"reference_paths": [str(short)]})
        self.assertEqual(captured.exception.code, "reference_too_short")
        self.assertEqual(registry.list(), [])

    def test_long_reference_is_chunked_without_truncation(self) -> None:
        registry = ProfileRegistry(self.paths)
        long_reference = write_wav(self.root / "long.wav", 65, sample_rate=8000)
        profile = registry.create({"reference_paths": [str(long_reference)]})
        self.assertFalse(profile["truncated"])
        self.assertGreaterEqual(profile["duration_seconds"], 64.9)
        self.assertGreaterEqual(profile["input_duration_seconds"], 64.9)
        self.assertGreaterEqual(len(profile["reference_paths"]), 3)

    def test_duration_above_thirty_minutes_is_not_truncated(self) -> None:
        registry = ProfileRegistry(self.paths)
        long_reference = write_wav(self.root / "over-thirty-minutes.wav", 4)

        def fake_split(source, processing_dir, source_index, **kwargs):
            chunks = []
            for index in range(64):
                chunks.append(write_wav(processing_dir / f"source_{source_index:04d}_{index:04d}.wav", 0.34))
            return chunks

        real_duration = audio.audio_duration

        def fake_duration(path):
            candidate = Path(path)
            if candidate.name == long_reference.name:
                return 1900.0
            if candidate.name.startswith("source_"):
                return 30.0
            return real_duration(candidate)

        with patch.object(audio, "split_reference_audio", side_effect=fake_split), patch.object(
            audio,
            "audio_duration",
            side_effect=fake_duration,
        ):
            profile = registry.create({"reference_paths": [str(long_reference)]})

        self.assertFalse(profile["truncated"])
        self.assertEqual(profile["input_duration_seconds"], 1900.0)
        self.assertEqual(profile["duration_seconds"], 1920.0)
        self.assertEqual(len(profile["reference_paths"]), 64)

    def test_invalid_content_is_rejected(self) -> None:
        registry = ProfileRegistry(self.paths)
        fake = self.root / "fake.mp3"
        fake.write_bytes(b"not audio at all" * 100)
        with self.assertRaises(ServiceError) as captured:
            registry.create({"reference_paths": [str(fake)]})
        self.assertIn(captured.exception.code, {"reference_invalid", "reference_processing_failed"})


class ReferenceValidationTests(RegistryFixture):
    def test_rejects_invalid_paths(self) -> None:
        empty = self.root / "empty.wav"
        empty.touch()
        cases = [
            None,
            [],
            "reference.wav",
            [""],
            ["relative.wav"],
            [str(self.root / "missing.wav")],
            [str(empty)],
            [str(self.root)],
            ["C:\\a\x00b.wav"],
            [str(self.reference)] * (audio.MAX_REFERENCE_FILES + 1),
        ]
        for case in cases:
            with self.subTest(case=case), self.assertRaises(ServiceError):
                audio.validate_reference_sources(case)

    def test_rejects_unsupported_extension(self) -> None:
        text = self.root / "notes.txt"
        text.write_text("x")
        with self.assertRaises(ServiceError) as captured:
            audio.validate_reference_sources([str(text)])
        self.assertEqual(captured.exception.code, "unsupported_format")

    def test_accepts_valid_reference(self) -> None:
        sources = audio.validate_reference_sources([str(self.reference)])
        self.assertEqual(sources, [self.reference.resolve()])


class AudioSegmentationTests(RegistryFixture):
    def test_ffmpeg_process_is_stopped_when_import_is_cancelled(self) -> None:
        checks = 0

        def cancelled() -> None:
            nonlocal checks
            checks += 1
            if checks >= 2:
                raise CancelledError()

        started = time.monotonic()
        with self.assertRaises(CancelledError):
            audio._run_ffmpeg_cancellable(
                [sys.executable, "-c", "import time; time.sleep(30)"],
                timeout=60,
                check_cancelled=cancelled,
            )
        self.assertLess(time.monotonic() - started, 5)

    def test_groups_never_exceed_xtts_reference_limit(self) -> None:
        groups = audio.group_speech_intervals([(0.0, 65.0)])
        self.assertEqual(len(groups), 3)
        self.assertTrue(all(audio._clip_duration(group) <= 30.0 for group in groups))

    def test_short_tail_is_merged_when_there_is_room(self) -> None:
        groups = audio.group_speech_intervals([(0.0, 24.0), (30.0, 32.0)])
        self.assertEqual(len(groups), 1)
        self.assertAlmostEqual(audio._clip_duration(groups[0]), 26.15, places=2)

    @unittest.skipUnless(ffmpeg_available(), "imageio-ffmpeg não disponível")
    def test_split_discards_long_silence_between_detected_spans(self) -> None:
        source = write_wav(self.root / "pauses.wav", 15)
        output = self.root / "processed"
        with patch.object(audio, "detect_speech_intervals", return_value=[(0.0, 4.0), (10.0, 15.0)]):
            chunks = audio.split_reference_audio(source, output, 0)
        self.assertEqual(len(chunks), 1)
        self.assertAlmostEqual(audio.audio_duration(chunks[0]), 9.15, places=1)

    @unittest.skipUnless(ffmpeg_available(), "imageio-ffmpeg não disponível")
    def test_split_rejects_audio_without_detected_speech(self) -> None:
        source = write_wav(self.root / "silent.wav", 4)
        with patch.object(audio, "detect_speech_intervals", return_value=[]):
            with self.assertRaises(ServiceError) as captured:
                audio.split_reference_audio(source, self.root / "processed", 0)
        self.assertEqual(captured.exception.code, "reference_no_speech")


class MigrationTests(RegistryFixture):
    def _legacy_registry(self, references: list[str]) -> None:
        payload = {
            "profiles": [
                {
                    "id": "abc123",
                    "name": "Voice 1",
                    "reference_paths": references,
                    "duration_seconds": 12.5,
                    "source_count": 1,
                    "truncated": False,
                    "created_at": "2026-09-20T22:07:36+00:00",
                }
            ]
        }
        self.paths.registry_path.write_text(json.dumps(payload), encoding="utf-8")

    def test_legacy_registry_is_upgraded_with_backup(self) -> None:
        voice_dir = self.paths.voices_dir / "abc123"
        voice_dir.mkdir(parents=True)
        reference = write_wav(voice_dir / "reference_0001.wav", 4)
        self._legacy_registry([str(reference)])

        registry = ProfileRegistry(self.paths)
        report = registry.migration_report
        self.assertEqual(report["schema_before"], 1)
        self.assertEqual(report["schema_after"], DATA_SCHEMA_VERSION)
        self.assertTrue(report["changed"])
        self.assertTrue(Path(report["backup"]).is_file())
        stored = json.loads(self.paths.registry_path.read_text(encoding="utf-8"))
        self.assertEqual(stored["schema_version"], DATA_SCHEMA_VERSION)
        self.assertEqual(registry.list()[0]["status"], "ok")

        # Running again must be a no-op (idempotent).
        again = ProfileRegistry(self.paths)
        self.assertFalse(again.migration_report["changed"])
        self.assertEqual(len(list(self.paths.backups_dir.glob("profiles-*.json"))), 1)

    def test_moved_data_directory_relinks_references(self) -> None:
        voice_dir = self.paths.voices_dir / "abc123"
        voice_dir.mkdir(parents=True)
        write_wav(voice_dir / "reference_0001.wav", 4)
        self._legacy_registry(["C:\\Users\\Outro\\AppData\\Local\\SVoice\\XTTS\\voices\\abc123\\reference_0001.wav"])

        registry = ProfileRegistry(self.paths)
        profile = registry.list()[0]
        self.assertEqual(profile["status"], "ok")
        self.assertEqual(profile["missing_references"], 0)
        self.assertEqual(registry.usable_references(registry.get("abc123")), [str(voice_dir / "reference_0001.wav")])

    def test_missing_references_are_reported_not_deleted(self) -> None:
        self._legacy_registry([str(self.paths.voices_dir / "abc123" / "reference_0001.wav")])
        registry = ProfileRegistry(self.paths)
        profile = registry.list()[0]
        self.assertEqual(profile["status"], "missing_references")
        self.assertEqual(len(registry.list()), 1)

    def test_corrupted_registry_is_preserved_without_a_backup(self) -> None:
        self.paths.registry_path.write_text("{not json", encoding="utf-8")
        with self.assertRaises(ServiceError) as captured:
            ProfileRegistry(self.paths)
        self.assertEqual(captured.exception.code, "profile_registry_corrupted")
        self.assertEqual(self.paths.registry_path.read_text(encoding="utf-8"), "{not json")

    def test_empty_registry_recovers_orphaned_profile_from_backup(self) -> None:
        voice_dir = self.paths.voices_dir / "abc123"
        voice_dir.mkdir(parents=True)
        reference = write_wav(voice_dir / "reference_0001.wav", 4)
        self.paths.registry_path.write_text(
            json.dumps({"schema_version": DATA_SCHEMA_VERSION, "profiles": []}),
            encoding="utf-8",
        )
        self.paths.backups_dir.mkdir(parents=True, exist_ok=True)
        backup = self.paths.backups_dir / "profiles-previous.json"
        backup.write_text(
            json.dumps({
                "schema_version": DATA_SCHEMA_VERSION,
                "profiles": [{
                    "id": "abc123",
                    "name": "Voice 1",
                    "reference_paths": [str(reference)],
                    "duration_seconds": 4,
                    "source_count": 1,
                    "truncated": False,
                    "created_at": "2026-09-20T22:07:36+00:00",
                }],
            }),
            encoding="utf-8",
        )

        registry = ProfileRegistry(self.paths)

        self.assertEqual(len(registry.list()), 1)
        self.assertEqual(registry.migration_report["recovered_from"], str(backup))
        stored = json.loads(self.paths.registry_path.read_text(encoding="utf-8"))
        self.assertEqual(stored["profiles"][0]["id"], "abc123")

    def test_empty_registry_does_not_restore_deleted_profile(self) -> None:
        self.paths.registry_path.write_text(
            json.dumps({"schema_version": DATA_SCHEMA_VERSION, "profiles": []}),
            encoding="utf-8",
        )
        self.paths.backups_dir.mkdir(parents=True, exist_ok=True)
        (self.paths.backups_dir / "profiles-previous.json").write_text(
            json.dumps({"schema_version": DATA_SCHEMA_VERSION, "profiles": [{"id": "deleted", "name": "Old"}]}),
            encoding="utf-8",
        )

        registry = ProfileRegistry(self.paths)

        self.assertEqual(registry.list(), [])
        self.assertIsNone(registry.migration_report["recovered_from"])


class TempCleanupTests(RegistryFixture):
    def test_cleanup_removes_imports_and_wavs(self) -> None:
        (self.paths.temp_dir / "import_x").mkdir()
        write_wav(self.paths.temp_dir / "utterance_1.wav", 1)
        (self.paths.temp_dir / "keep.txt").write_text("x")
        removed = cleanup_temp_files(self.paths)
        self.assertEqual(removed, 2)
        self.assertEqual([p.name for p in self.paths.temp_dir.iterdir()], ["keep.txt"])


if __name__ == "__main__":
    unittest.main()
