import array
from pathlib import Path
import tempfile
import unittest
import wave

from speech_audio import inspect_audio


class AudioEvidenceTests(unittest.TestCase):
    def test_silence_and_a_single_click_cannot_pass(self):
        for click in (False, True):
            with self.subTest(click=click), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "audio.wav"
                samples = array.array("h", [0] * 32000)
                if click:
                    samples[16000] = 30000
                self.write(path, samples)
                with self.assertRaisesRegex(ValueError, "No sustained audio"):
                    inspect_audio(path)

    def test_truncated_capture_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "audio.wav"
            self.write(path, array.array("h", [1000, -1000] * 16000))
            path.write_bytes(path.read_bytes()[:-100])
            with self.assertRaisesRegex(ValueError, "Truncated"):
                inspect_audio(path)

    def test_sustained_audio_is_not_itself_a_speech_claim(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "audio.wav"
            self.write(path, array.array("h", [1000, -1000] * 16000))
            self.assertEqual(inspect_audio(path)["audible_seconds"], 2)

    @staticmethod
    def write(path, samples):
        with wave.open(str(path), "wb") as wav:
            wav.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            wav.writeframes(samples.tobytes())


if __name__ == "__main__":
    unittest.main()
