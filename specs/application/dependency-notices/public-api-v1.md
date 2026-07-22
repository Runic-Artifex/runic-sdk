# Dependency Notices Wave A public API v1

Package identities are `WebUIToolkit.DependencyNotices.Core` and `WebUIToolkit.DependencyNotices.Engine`, both targeting `net10.0`. They are BCL-only and opt into trim and Native-AOT analyzers. Publication remains blocked by ADR 0004.

## Core

- `PackageUrl.Parse` / `TryParse` produce an exact-version canonical identity with decoded components and immutable, ordinal qualifiers.
- `SpdxParser.Parse` produces `SpdxExpression` over immutable identifier, `WITH`, `AND`, and `OR` nodes. `SpdxParseException` exposes zero-based `Offset` and `Expected`.
- `EvidenceDigest` computes and validates lowercase SHA-256 identities over unmodified bytes.
- `DependencyComponentComparer` orders manual components by ordinal display name, version, then canonical Package URL.
- `LicensePolicyEvaluator.Evaluate` keeps observed/effective/selected expressions distinct and returns stable `WUTNOTICE` diagnostics.
- `NoticeDiagnosticCodes` contains the Wave A identities frozen in `diagnostics-v1.md`.

## Engine

- `ManualComponentScanner.Scan(rootDirectory, configRelativePath)` reads schema version 1 manual inputs, verifies evidence containment and exact digests, resolves custom license references, rejects duplicate identities, and returns deterministically ordered components and diagnostics.
- `SafePath.ResolveContainedPath` rejects rooted, device, ADS, dot-segment, prefix-confusion, symlink, and reparse-point escapes.
- `NetworkPolicy.EnsurePermitted` denies networking to scan/evaluate/generate/verify under every flag combination. It admits only the later acquisition operation with explicit opt-in; Wave A contains no transport implementation.

Acquisition, NuGet, npm, SBOM, renderers, CLI/MSBuild, and runtime packages are not part of this API version.
