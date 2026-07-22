# SBOM reconciliation and renderer contract

## Bounded SBOM reader

Wave B reads a reconciliation subset; it does not create or validate a complete SBOM.

Default limits are 4 MiB input, JSON depth 64, 100,000 properties, and 10,000 components. JSON comments/trailing commas are rejected. Callers may choose positive limits, but a public frontend SHOULD retain the defaults unless policy explicitly tightens them.

### CycloneDX JSON subset

The reader consumes top-level and nested `components`, each with `name`, `version`, optional `bom-ref`, optional canonical `purl`, and optional `ecosystem`/`webuitoolkit:ecosystem` property. It records `serialNumber` and uses metadata component `bom-ref`, then serial number, then `CycloneDX` as the document reference. Conflicting ecosystem properties or malformed PURLs are rejected.

### SPDX JSON subset

The reader consumes document `SPDXID` and `packages`. Each package requires `SPDXID`, `name`, and `versionInfo`. Package URLs are taken only from `externalRefs` whose `referenceType` is `purl`. Conflicting PURL references are rejected. No serial number is synthesized.

Fields outside these subsets are ignored only after the bounded JSON tree has passed structural limits. Ignored fields do not become notice claims.

## Reconciliation

1. Sort inventory by canonical PURL and SBOM components by PURL, ecosystem, name, version, then component reference using ordinal comparison.
2. Match a unique exact canonical PURL first.
3. Only when the SBOM component has no PURL, allow a unique ecosystem/name/version fallback on both sides. NuGet names compare case-insensitively; other names and all versions compare ordinally.
4. Never use a fuzzy version, display-name-only, first-match, or position-based fallback.
5. A repeated SBOM component reference makes all occurrences unavailable and produces `WUTNOTICE5004`.
6. Produce stable missing, extra, and identity-mismatch diagnostics (`WUTNOTICE5001`-`5003`) and stable PURL-to-component-reference links.

Policy may configure whether a mismatch blocks a profile, but it may not hide the underlying diagnostic or rewrite the observed SBOM.

## Complete document schema v2

`dependency-notices.document.schema.v2.json` is additive relative to the Wave A v1 manual-output contract. It is the complete renderer/runtime model and contains:

- artifact name and optional version;
- dependencies with PURL, identity, ecosystem, scope, reachability, observed/effective/selected SPDX expressions;
- exact evidence assets with kind, SHA-256, media type, original text, origin, and override marker;
- policy decisions and optional SBOM component reference;
- modification status/notice; and
- stable diagnostics and optional document-level SBOM reference.

Version 2 does not change any Wave A v1 input schema. A v1 document is not relabeled as v2. A v2 producer must populate the complete model; null and empty values retain the distinction defined by the schema.

## Canonical model ordering

- Dependencies: normalized display name ordinal, version ordinal, then canonical PURL ordinal, preserving the Wave A notice ordering contract.
- Assets: declared kind order, SHA-256 ordinal, then origin ordinal.
- Decisions: subject ordinal, declared outcome order, then rule ordinal.
- Diagnostics: code, PURL, source, then message using ordinal comparison and null before non-null.
- SBOM links: canonical PURL, then component reference, ordinal.

No renderer may reorder the semantic model differently except presentation-only headings whose order is itself fixed by renderer version.

## Renderer canonicalization

All renderers consume one already evaluated v2 model. They MUST NOT rescan, reacquire, reevaluate policy, infer license text, or mutate the model.

### JSON

UTF-8 without BOM, LF, one terminal LF, invariant JSON tokens, documented property order, no insignificant trailing whitespace, lowercase SHA-256, and generated `System.Text.Json` metadata. Host-dependent fields and timestamps are forbidden.

### Plain text

UTF-8 without BOM, LF, one terminal LF. Fixed headings/separators and ordinal component order. Evidence text is emitted verbatim except that the renderer supplies deterministic boundary newlines; it does not reflow or normalize the body.

### HTML

UTF-8 without BOM, LF, one terminal LF. Every name, version, URL, expression, source, diagnostic, and evidence body is context-appropriately encoded. Package text is never raw HTML. Output contains no scripts, event attributes, remote CSS/fonts/images, active embeds, or dangerous URL schemes. Stable local CSS, if emitted, is versioned renderer content.

### Manifest

The manifest records renderer/contract version and SHA-256 for every generated output in ordinal normalized relative-path order. It contains no absolute path or clock. Verification recomputes bytes and reports `WUTNOTICE6002` with expected/actual digests on drift.

Outputs are generated in a contained staging location and atomically replace only explicitly declared destinations. An unsafe destination produces `WUTNOTICE6004`. Renderer failures do not leave mixed-version outputs.
