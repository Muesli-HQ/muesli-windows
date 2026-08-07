import importlib.util
import pathlib
import unittest
from unittest import mock


WORKER_PATH = pathlib.Path(__file__).parents[1] / "transcribe_worker.py"
SPEC = importlib.util.spec_from_file_location("muesli_transcribe_worker", WORKER_PATH)
WORKER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(WORKER)


class TranscriptionTruthfulnessTests(unittest.TestCase):
    def test_whisper_failure_is_not_converted_to_fake_success(self):
        with mock.patch.object(WORKER, "transcribe_whisper", side_effect=ModuleNotFoundError("missing runtime")):
            with self.assertRaisesRegex(RuntimeError, "dependency is unavailable"):
                WORKER.transcribe("Dictation", "input.wav", "microphone", "whisper", "base", "final", "en", "")

    def test_parakeet_failure_is_not_converted_to_fake_success(self):
        with mock.patch.object(WORKER, "transcribe_parakeet", side_effect=RuntimeError("missing model")):
            with self.assertRaisesRegex(RuntimeError, "Parakeet transcription failed"):
                WORKER.transcribe("Dictation", "input.wav", "microphone", "parakeet-v3", "base", "final", "en", "")


if __name__ == "__main__":
    unittest.main()
