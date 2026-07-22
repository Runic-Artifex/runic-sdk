# Manual component fixture corpus

This directory contains the language-neutral Wave A fixtures for manual dependency notices. The corpus has 44 cases: 24 structurally valid inputs and 20 inputs that must fail closed. `case-manifest.json` is the test oracle; each file under `cases/` is a directly consumable `dependency-notices.json`-style input.

All names, origins, revisions, review records, and evidence bodies are synthetic. The evidence bodies and fixture metadata are dedicated to the public domain under CC0-1.0. They quote no third-party license text and identify no real dependency. `example.invalid` and `fixture://` origins are inert labels, not acquisition sources.

## Layout

- `case-manifest.json` declares every case, tags, expected WUTNOTICE diagnostics, canonical PURLs, deterministic order, and the fixed policy evaluation date.
- `cases/*.json` contains schema-versioned manual input documents. Case 43 deliberately uses an unsupported schema version; all other documents declare `schemaVersion` 1.
- `policies/*.json` contains schema-versioned policy inputs for OR-selection and exact override/expiry cases.
- `dependency-notices.assets/sha256/*` contains exact evidence bytes named by their lowercase SHA-256 digest.
- `.gitattributes` marks content-addressed assets as binary so checkout newline conversion cannot change their bytes or digests.

Evidence paths in cases are resolved from this corpus directory, not from the directory containing an individual case. Generation and verification must read only the named local bytes. Case 35 is the deliberate remote-path rejection case and must report `WUTNOTICE7001` without opening a socket.

## Evidence inventory

| SHA-256 | Bytes | Intended kinds |
| --- | ---: | --- |
| `2926e284cf97e9a85e260eea758038038adecbc43eb83a9072ba7e87464af474` | 167 | license |
| `e746e4978d77417c2dd71a916012faea77fe9fa9f5256ba20cc1c96886fb3656` | 174 | license |
| `f248c096ddbc58c275aa2396bb5570bb24dffaa71add46f8e790d55deaaa8129` | 197 | license / LicenseRef definition |
| `ff1e2988263bb7858aa9013c3fb0a3980f3dd5355f8a45ea082b796580a2c53b` | 127 | notice / attribution / authors |
| `864350f389d2b6b51b9a871ad9ace0d7c4cc9a9f38aab617eb855e0e66c98a6d` | 141 | modification |

The byte counts and digests include the final LF. Evidence is never normalized before hashing.

## Coverage

The valid inputs cover vendored source, a modified fork, native libraries, fonts, images, generic/NuGet/npm/scoped-npm PURLs, qualifier and percent normalization, `LicenseRef`, `DocumentRef`, `AND`, `OR`, `WITH`, all evidence kinds, exact overrides, active and expired overrides, deterministic ordering, HTML-like text, and Unicode normalization boundaries.

The rejection inputs cover missing required fields, absent evidence, malformed and mismatched digests, absolute/traversal/device/remote paths, exact and canonical duplicate PURLs, malformed PURLs and SPDX, unresolved references, null components, schema mismatch, and embedded control text.

`inputValid` in the manifest describes parsing and inventory validity. Policy can still reject a valid input: case 13 has an unselected `OR`, and case 21 has an expired override. Policy expectations are kept in `policyContext` so test-only fields never enter product input.

## Deterministic use

Tests should enumerate only the manifest, resolve paths with ordinal semantics, and compare diagnostic code sets and canonical PURL sequences exactly. Policy expiry tests use the manifest's fixed `evaluationDate` (`2026-07-22`), never the machine clock. The ordering oracle for case 16 intentionally demonstrates ordinal version ordering (`10.0.0` before `2.0.0`); it is not semantic-version sorting.
