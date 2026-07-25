# Dependency Notices contracts

Wave A freezes major schema version `1` for the offline manual-component path. Schema `$id` values are intentionally absent until an owned schema domain is approved. Readers must reject unsupported major versions and generators must not rewrite input contracts implicitly.

## Versioned contracts

| Contract | File | Version |
|---|---|---:|
| Configuration and manual components | `dependency-notices.schema.v1.json` | 1 |
| Policy | `dependency-notices.policy.schema.v1.json` | 1 |
| Evidence lock | `dependency-notices.lock.schema.v1.json` | 1 |
| Generated notice document | `dependency-notices.document.schema.v1.json` | 1 |
| Generated notice document with complete evidence and SBOM linkage | `dependency-notices.document.schema.v2.json` | 2 |
| Machine-readable diagnostics | `dependency-notices.diagnostics.schema.v1.json` | 1 |

Canonical JSON uses UTF-8 without BOM, LF newlines, ordinal property and collection ordering defined by the producing contract, invariant values, lowercase SHA-256, and normalized `/` relative paths. Generated documents contain no timestamps, host names, home directories, restore roots, or temporary paths.

The initial diagnostic catalog is in `diagnostics-v1.md`; the security boundary is in `threat-model-v1.md`.

## Wave C package-consumer evidence boundary

`generate` and `verify` may receive one explicit, already-restored NuGet graph through `--nuget-lock`, `--nuget-assets`, `--nuget-framework`, and `--nuget-packages-root`. All four are required together. They are local, bounded inputs: the tool checks lock/assets agreement and reads only locally restored package license evidence. Neither command has a transport path; `acquire --allow-network` remains the sole acquisition operation.

External Text Resources packs are deliberately outside that adapter. A consumer that distributes one records it as a manual component with an exact canonical PURL, non-empty revision, local SHA-256-pinned evidence, and explicit origin. Dependency Notices does not open, unpack, validate, fetch, or sign the pack; Text Resources retains its own parser and publication limits.

The package-consumer bridge does not add SBOM reconciliation, release aggregation, vulnerability scanning, or publishing work.
