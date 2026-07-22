# Wave B diagnostic additions

These codes extend, but do not alter, `diagnostics-v1.md`. Default severity is Error. Codes, category, source meaning, and argument meaning are compatibility contracts; wording may improve without disclosing sensitive data.

## Policy 3003-3004 and 4006-4011

| Code | Meaning | Required context and remediation |
|---|---|---|
| `WUTNOTICE3003` | Policy configuration is malformed or violates a bounded contract | Sanitized JSON source/path and violated rule; validate against policy schema v1 |
| `WUTNOTICE3004` | Policy schema version is unsupported | Observed and supported versions; migrate explicitly without implicit rewrite |
| `WUTNOTICE4006` | Exact policy override is expired at the caller-supplied evaluation date | Override identity, fixed evaluation date, and expiry condition; renew or remove after review |
| `WUTNOTICE4007` | Exact policy override is stale for the selected component version | Override and canonical PURL; create a separately reviewed exact-version override |
| `WUTNOTICE4008` | Multiple exact overrides conflict | Canonical PURL and bounded override identities; resolve explicitly without last-rule-wins |
| `WUTNOTICE4009` | Declared exact override matched no component | Override identity and canonical target; remove or correct the stale rule |
| `WUTNOTICE4010` | Override rationale, approver, creation date, expiry, or evidence metadata is incomplete | Override identity and missing metadata category; complete the review record |
| `WUTNOTICE4011` | Override evidence digest does not match linked evidence | Override identity plus expected/actual lowercase digest; review and pin exact bytes |

## Inventory 1004-1008

| Code | Meaning | Required context and remediation |
|---|---|---|
| `WUTNOTICE1004` | Requested target/workspace is missing or ambiguous | Sanitized source plus exact requested target/workspace; require one explicit matching target |
| `WUTNOTICE1005` | Lock/restored graph drift | Package identity/target and the mismatching entry or integrity; restore/update the reviewed portable lock explicitly |
| `WUTNOTICE1006` | Unsupported inventory format/version | Sanitized source and observed format/version; produce a supported locked format |
| `WUTNOTICE1007` | Dependency lacks an exact resolution or an edge cannot resolve | Requester/dependency or identity; regenerate a complete locked graph |
| `WUTNOTICE1008` | Dependency graph is structurally invalid | Sanitized entry/path and violated invariant; repair or regenerate the graph |

## Evidence 2003-2005

| Code | Meaning | Required context and remediation |
|---|---|---|
| `WUTNOTICE2003` | Multiple evidence/manifest candidates prevent deterministic selection | PURL and sanitized candidates/category; declare an exact override or remove ambiguity |
| `WUTNOTICE2004` | Metadata supplies only a URL rather than pinned local evidence | PURL and sanitized origin/source; run explicit reviewed acquisition and pin its digest |
| `WUTNOTICE2005` | Evidence text or metadata has invalid encoding/shape or exceeds its text limit | PURL, kind, sanitized source; provide bounded strict UTF-8/text evidence |

## SBOM 5001-5004

| Code | Meaning | Required context and remediation |
|---|---|---|
| `WUTNOTICE5001` | Inventory component is absent from the SBOM | Canonical PURL and SBOM document reference; regenerate the matching-artifact SBOM |
| `WUTNOTICE5002` | SBOM component is absent from inventory | Component reference/PURL and SBOM document reference; select matching inputs or review policy |
| `WUTNOTICE5003` | Ecosystem/name agrees but identity/version conflicts | Inventory PURL and SBOM component reference/identity; correct the stale side |
| `WUTNOTICE5004` | SBOM component reference is duplicated | Document/component reference and occurrence count; make references unique |

## Output 6002-6004

| Code | Meaning | Required context and remediation |
|---|---|---|
| `WUTNOTICE6002` | Generated output differs from canonical expected bytes | Normalized relative output plus expected/actual SHA-256 and bounded first difference; regenerate/review |
| `WUTNOTICE6003` | Input/output schema major is incompatible | Contract kind, observed and supported major versions; use supported input or explicit migration |
| `WUTNOTICE6004` | Output destination is unsafe, escaping, duplicated, or aliases an input | Declared sanitized relative path and rule; choose a unique contained destination |

## Acquisition 7002-7005

| Code | Meaning | Required context and remediation |
|---|---|---|
| `WUTNOTICE7002` | Origin scheme, credentials, or exact host violates acquisition policy | Sanitized scheme/host only; add a reviewed exact host or use HTTPS |
| `WUTNOTICE7003` | Redirect is missing, invalid, blocked, cyclic, or over the limit | Sanitized current/next hosts and redirect count; correct origin/policy without weakening build isolation |
| `WUTNOTICE7004` | Declared or streamed evidence exceeds acquisition byte limit | Configured limit and bounded observed size; obtain reviewed bounded evidence |
| `WUTNOTICE7005` | Acquired bytes do not match required SHA-256 | Sanitized origin plus expected/actual lowercase SHA-256; review upstream change and update explicitly |

## Stable mapping and sanitization

Adapters, library operations, and CLI exit codes map the same underlying condition to the same diagnostic. A frontend may promote severity by profile but preserves `Code` and original category. Diagnostics MUST NOT contain full resolved host paths, URL credentials, authorization headers, tokens, sensitive query values, response bodies, package-manager environment, or unbounded hostile strings. Invalid strings are bounded and control characters are escaped or replaced.

Ordering is code, canonical PURL, sanitized source, then message using ordinal comparison and null-before-value. Offset and remediation remain serialized compatibility fields but do not change primary diagnostic order. Duplicate diagnostics with identical contract fields MAY be coalesced only when occurrence count is not semantically relevant.
