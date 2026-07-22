# Web MVVM conformance fixtures

This directory is the deterministic, framework-neutral fixture corpus for
`webuitoolkit.mvvm/1`. Node, browser, and framework-adapter test runners can
consume the same files without importing a particular SDK implementation.

## Contents

- `protocol/v1/` is a byte-identical mirror of `protocol/mvvm/corpus/v1/`.
  Its manifest contains 33 schema cases and 12 protocol semantic cases.
- `vectors/state-lifecycle.json` contains eight state, revision, patch,
  collection, command-state, and validation scenarios.
- `vectors/command-lifecycle.json` contains six deterministic command,
  cancellation, timeout, and exactly-once settlement scenarios.
- `vectors/reconnect-lifecycle.json` contains five reconnect and snapshot
  recovery scenarios.
- `vectors/hostile-input.json` contains 28 hostile framing and limit cases.
- `manifest.json` is the machine-readable entry point. It records suite counts
  and a SHA-256 digest and byte length for every data file.

The complete corpus has 92 cases: 45 upstream protocol cases and 47 web SDK
cases. A lifecycle `scenario` may contain several ordered steps; the case total
counts scenarios, not individual steps or assertions.

## Runner contract

Lifecycle files use `webuitoolkit.mvvm.conformance-scenarios/1` and carry a
stable `initial` state followed by ordered `steps`. A step's `action` describes
the stimulus. Its `expect` object describes externally observable state,
outbound message kinds, and promise settlement. Request UUIDs and the manual
clock are fixed so no wall clock, randomness, locale, or network is involved.

Expected revisions are decimal strings. Wire messages remain JSON numbers or
exact UTF-8 frame strings. This deliberately prevents a runner from treating a
signed 64-bit revision as a JavaScript `number`.

Hostile input files use `webuitoolkit.mvvm.hostile-input/1`. An input is one of:

- `hex`: exact frame bytes, lowercase and without separators;
- `utf8`: the exact text to UTF-8 encode; or
- `generated`: a bounded deterministic recipe with all parameters recorded.

Generated recipes avoid committing megabyte-sized or ten-thousand-element
documents. Generators emit compact JSON, use ASCII object keys in the listed
order, and must assert the generated count or UTF-8 length before exercising
the SDK. `spacePaddedDocument` appends ASCII spaces after `document` until the
specified total byte length. The other generator names state the JSON shape
they produce and repeat the supplied scalar/value exactly `count` or `repeat`
times.

`expect.accepted` concerns framing, lossless parsing, and hard-limit validation.
It does not imply that an otherwise arbitrary generated JSON value is a valid
protocol envelope. Cases with `dispatchCount: 0` must be rejected before any
consumer or application callback.

## Integrity and updates

All paths in `manifest.json` are relative to this directory and use `/`.
Integrity is computed over the raw committed bytes, including line endings.
The manifest itself is excluded to avoid a recursive digest.

To verify from Node without dependencies:

```js
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";

const root = new URL("./", import.meta.url);
const manifest = JSON.parse(await readFile(new URL("manifest.json", root), "utf8"));
for (const entry of manifest.files) {
  const bytes = await readFile(new URL(entry.path, root));
  const digest = createHash("sha256").update(bytes).digest("hex");
  if (bytes.byteLength !== entry.bytes || digest !== entry.sha256) {
    throw new Error(`Fixture integrity mismatch: ${entry.path}`);
  }
}
```

When the normative protocol corpus changes, replace the mirror byte-for-byte,
update the upstream manifest digest, recompute every file entry sorted by
ordinal path, and update suite counts only when cases were added or removed.
Do not normalize the mirrored JSON: byte identity is an intentional cross-
runtime guarantee.
