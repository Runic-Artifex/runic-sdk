#!/usr/bin/env python3
"""Locally cross-check Orca recordings with pinned Whisper; never prompt with expected labels."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

from speech_audio import inspect_audio

MODEL_SHA256 = "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002"


def words(text):
    return " ".join(re.findall(r"\w+", text.casefold()))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path, help="Guest result directory containing speech.json and WAV files")
    parser.add_argument("--model", type=Path, required=True, help="Pinned ggml-base.en.bin")
    parser.add_argument("--whisper", default="whisper-cli")
    parser.add_argument("--output", type=Path, required=True, help="New directory for transcripts and recognition logs")
    args = parser.parse_args()
    with args.model.open("rb") as model:
        if hashlib.file_digest(model, "sha256").hexdigest() != MODEL_SHA256:
            parser.error("Model does not match the pinned base.en checksum")
    args.output.mkdir(parents=True, exist_ok=False)
    results = []
    failure = None
    try:
        suite = json.loads((args.capture / "results.json").read_text())
        if suite["failure"] is not None or not any("Orca label/role speech" in name for name in suite["passed"]):
            raise ValueError("Native Orca/PCM checks must pass before transcription")
        manifest = json.loads((args.capture / "speech.json").read_text())
        if not manifest["captures"]:
            raise ValueError("No speech captures were supplied")
        for index, capture in enumerate(manifest["captures"]):
            wav = (args.capture / capture["audio"]).resolve()
            if wav.parent != args.capture.resolve():
                raise ValueError("Audio must belong to this capture directory")
            metrics = inspect_audio(wav)
            prefix = args.output / f"speech-{index}"
            with prefix.with_suffix(".log").open("w") as log:
                subprocess.run([args.whisper, "--model", str(args.model), "--file", str(wav),
                                "--language", "en", "--threads", "4", "--no-gpu", "--no-fallback",
                                "--temperature", "0", "--output-json", "--output-file", str(prefix)],
                               stdout=log, stderr=subprocess.STDOUT, check=True, timeout=60)
            data = json.loads(prefix.with_suffix(".json").read_text())
            text = " ".join(segment["text"] for segment in data["transcription"])
            matches = (" " + words(capture["label"]) + " ") in (" " + words(text) + " ")
            role_matches = (" " + words(capture["role"]) + " ") in (" " + words(text) + " ")
            results.append({"label": capture["label"], "transcript": text.strip(),
                            "label_recognized": matches, "role_recognized": role_matches, "metrics": metrics})
            print(("PASS " if matches and role_matches else "MISMATCH ") + capture["label"] + ": " + text.strip(), flush=True)
        if not all(item["label_recognized"] and item["role_recognized"] for item in results):
            raise ValueError("A captured label or role was not recognized; inspect audio and Orca logs before attributing it to Runic")
    except Exception as error:
        failure = str(error)
    finally:
        (args.output / "results.json").write_text(json.dumps({"model_sha256": MODEL_SHA256,
            "captures": results, "failure": failure,
            "scope": "Supplementary acoustic word check, not a judgment of screen-reader usability"}, indent=2) + "\n")
    if failure:
        raise SystemExit("FAIL " + failure)


if __name__ == "__main__":
    main()
