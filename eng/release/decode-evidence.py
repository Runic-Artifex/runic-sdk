"""Decode dispatch input without fetching URLs, changing evidence, or accepting gates."""
import base64
import gzip
import io
import json
import math
import os
import sys

MAX_ENCODED_CHARACTERS = 60000
MAX_EXPANDED_BYTES = 1048576


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON key")
        result[key] = value
    return result


def invalid_constant(value):
    raise ValueError("Non-finite JSON number")


def finite_float(value):
    number = float(value)
    if not math.isfinite(number):
        raise ValueError("Non-finite JSON number")
    return number


def finite_int(value):
    number = int(value)
    # Gates consume JavaScript numbers. A JSON integer must not become Infinity
    # there merely because Python can represent it with arbitrary precision.
    try:
        if math.isfinite(float(number)):
            return number
    except OverflowError:
        pass
    raise ValueError("Non-finite JSON number")


def decode_evidence(encoded):
    if not 0 < len(encoded) <= MAX_ENCODED_CHARACTERS:
        raise ValueError("Encoded evidence exceeds dispatch budget")
    compressed = base64.b64decode(encoded, validate=True)
    with gzip.GzipFile(fileobj=io.BytesIO(compressed)) as stream:
        payload = stream.read(MAX_EXPANDED_BYTES + 1)
    if len(payload) > MAX_EXPANDED_BYTES:
        raise ValueError("Expanded evidence exceeds 1 MiB")
    evidence = json.loads(payload.decode("utf-8"), object_pairs_hook=unique_object,
                          parse_constant=invalid_constant, parse_float=finite_float,
                          parse_int=finite_int)
    if not isinstance(evidence, dict) or evidence.get("schema") != "runic.preview-evidence/1":
        raise ValueError("Expected evidence envelope")
    # Preserve the submitted UTF-8 bytes, including formatting and numeric spelling.
    # The unchanged gate validator separately checks all source/hash/outcome fields.
    return payload


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Use decode-evidence.py OUTPUT; supply RECEIPTS_GZIP_BASE64 in the environment")
    payload = decode_evidence(os.environ["RECEIPTS_GZIP_BASE64"])
    with open(sys.argv[1], "xb") as output:
        output.write(payload)
