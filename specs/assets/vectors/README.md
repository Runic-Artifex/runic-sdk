# Runic Assets archive vectors

Portable vectors for the [reader rules](../archive-v1.md#reader-rules) of `runic.assets.archive/1` and `/2`. They are
normative. An implementation in any language checks its reader with them and reports, for each case id, its own
implementation identity and one outcome: pass, fail, unsupported or excluded.

| File | Cases |
| --- | --- |
| [`crafted.json`](crafted.json) | 49 archives that [`generate.mjs`](generate.mjs) builds byte by byte, with stored entries: 9 valid ones, and 40 invalid ones that each break one rule. 42 are the R4c lab's corpus; `valid-cache-modes`, `valid-manifest-bom`, `crc-mismatch-manifest`, `cache-mode-padded`, `length-exponent`, `manifest-duplicate-member-nested` and `manifest-invalid-utf8` were added with the reader rules |
| [`packer.json`](packer.json) | 2 valid archives that the .NET packer (runic-sdk de807a135) wrote, [`packer/basic.zip`](packer/basic.zip) and [`packer/shared.zip`](packer/shared.zip): deflated entries, a non-ASCII path, nested folders and shared content |

A file follows [`vector.schema.json`](vector.schema.json) (format `runic.assets.vector/1`). Its `id` is
`assets.{file}`, and each case's `id` starts with it. Every case has the `operation` `read`, an `input`, the archive's
bytes in base64, and an `expect`; `expectLazy` replaces `expect` for lazy reading where the two differ (only
`assets.crafted.digest-mismatch`, since lazy reading does not check digests).

## Running a case

A runner reads the archive in each [mode](../archive-v1.md#eager-and-lazy-reading) that the implementation has, with
its default limits:

- **Eager.** Read the archive. The result is `invalid` when the read fails.
- **Lazy.** Read the archive, then open every asset it reports, in order, and read its bytes. The result is `invalid`
  when the read or an open fails.

For `valid`, the reported assets must equal `expect.assets` in order and member by member: normalized `path`,
`mediaType`, `length`, `sha256` in lower case, `entryPoint` and `cacheMode`. Every asset's bytes must be `length` bytes
long and, in eager reading, have the SHA-256 digest `sha256`. For `invalid`, the read or an open must fail; a runner
compares `expect.error` only for an implementation that reports [error codes](../archive-v1.md#error-codes). An
implementation without codes, as the .NET reader (`InvalidDataException`), passes on any failure of its documented
kind and fails on any other exception.

In the language-neutral driver (`rax.conformance-adapter/1`) the adapter op is `assets.read {archive, mode}` with the
input's base64 and `eager` or `lazy`, answering `{result: "valid", assets}` or `{result: "invalid", error?}`; the
driver compares. Until that module exists, `ArchiveVectorTests` in `tests/dotnet/Runic.Assets.Tests` runs the vectors
against the .NET reader.

## Changing the vectors

Edit `generate.mjs` and run it from the repository root; commit its output:

```sh
bun specs/assets/vectors/generate.mjs
```

`bun run test eng/assets-vectors.test.mjs` fails when `crafted.json` or `packer.json` differs from the generator's
output, and checks every file against its schema and the valid manifests against
[`../manifest.schema.json`](../manifest.schema.json). The packer archives are inputs: the generator inflates every
entry, checks its CRC-32, length and digest against the archive's manifest, and takes the expectation from that
manifest. `ArchiveVectorTests` writes their files to a directory, packs it with the current writer and checks that
the new archive reads the same way.

A new case breaks exactly one rule, so its error code is unambiguous, and keeps the archives small.
