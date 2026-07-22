# Dependency Notices contracts

Wave A freezes major schema version `1` for the offline manual-component path. Schema `$id` values are intentionally absent until an owned schema domain is approved. Readers must reject unsupported major versions and generators must not rewrite input contracts implicitly.

## Versioned contracts

| Contract | File | Version |
|---|---|---:|
| Configuration and manual components | `dependency-notices.schema.v1.json` | 1 |
| Policy | `dependency-notices.policy.schema.v1.json` | 1 |
| Evidence lock | `dependency-notices.lock.schema.v1.json` | 1 |
| Generated notice document | `dependency-notices.document.schema.v1.json` | 1 |
| Machine-readable diagnostics | `dependency-notices.diagnostics.schema.v1.json` | 1 |

Canonical JSON uses UTF-8 without BOM, LF newlines, ordinal property and collection ordering defined by the producing contract, invariant values, lowercase SHA-256, and normalized `/` relative paths. Generated documents contain no timestamps, host names, home directories, restore roots, or temporary paths.

The initial diagnostic catalog is in `diagnostics-v1.md`; the security boundary is in `threat-model-v1.md`. Acquisition, NuGet, npm, SBOM adapters, MSBuild integration, and runtime packaging are later phases.
