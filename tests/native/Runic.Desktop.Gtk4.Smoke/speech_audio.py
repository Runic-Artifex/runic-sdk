"""Checks on captured PCM, independent of speech recognition or desktop APIs."""
import array
import math
import sys
import wave


def inspect_audio(path):
    with wave.open(str(path), "rb") as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, 16000):
            raise ValueError("Expected mono 16-bit 16kHz PCM")
        frames = wav.getnframes()
        data = wav.readframes(frames)
        if len(data) != frames * 2:
            raise ValueError("Truncated recording")
    samples = array.array("h", data)
    if sys.byteorder != "little":
        samples.byteswap()
    if len(samples) < 16000:
        raise ValueError("Recording is shorter than one second")
    peak = max(abs(value) for value in samples)
    rms = math.sqrt(sum(value * value for value in samples) / len(samples))
    # Measure 20ms windows so a click cannot satisfy the audible-duration check.
    audible = sum(math.sqrt(sum(x*x for x in samples[i:i+320]) / 320) >= 100
                  for i in range(0, len(samples) - 319, 320)) * 0.02
    result = {"duration_seconds": len(samples) / 16000, "peak": peak,
              "rms": rms, "audible_seconds": round(audible, 2)}
    if rms < 50 or audible < 0.3:
        raise ValueError("No sustained audio captured: " + str(result))
    return result
