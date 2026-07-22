# Restored inventory adapter contract

Adapters report observed facts. They do not restore, acquire evidence, select a legal interpretation, edit locks, or read undeclared global state. Every result is sorted by canonical Package URL using ordinal comparison; diagnostics use their stable comparer.

## Common component model

An adapter produces an exact canonical Package URL, display name, exact resolved version, source kind, scope, direct/transitive flag, observed license expression when present, integrity when present, sanitized source reference, and zero or more evidence descriptors. Duplicate canonical Package URLs in one selected graph are errors.

Input JSON MUST reject comments and trailing commas. Text inputs MUST use strict decoding. Paths MUST pass the Wave A containment boundary. File enumeration MUST be explicitly ordinal-sorted before a choice or output is made.

## NuGet adapter

### Inputs and target selection

- Required: a locked `packages.lock.json`, restored `obj/project.assets.json`, and exact target framework.
- Optional: one explicit runtime identifier and one explicit packages root.
- With no RID, the selected target key is the requested target framework. With a RID, it is `<target-framework>/<runtime-identifier>`.
- Target matching is case-insensitive only for locating one exact requested key. Zero or multiple matching keys produce `WUTNOTICE1004`; no nearest-framework or first-target fallback is allowed.
- The adapter MUST cross-check the selected lock graph and assets graph by exact package ID/version and integrity. Missing entries or hash disagreement produce `WUTNOTICE1005`.

### Supported subset

- Lock entries require an exact `resolved` version and a dependency `type`; unresolved ranges produce `WUTNOTICE1007`.
- Dependency edges must resolve within the selected target. Malformed/duplicate case-folded identities or invalid edge maps produce `WUTNOTICE1008`.
- Only assets libraries of type `package` are inventoried. Scope is derived from restored runtime/compile/native versus build/analyzer assets; development-only package metadata is respected.
- Package identity is `pkg:nuget/<encoded-id>@<encoded-version>`. NuGet package-name comparison is case-insensitive for graph matching while emitted ordering and canonical identities are ordinal.
- Local package metadata may be read only beneath the single declared/restored packages root. The package manifest is the unique `.nuspec` candidate.
- Embedded license files are preferred and hashed. A license expression remains metadata; a license URL is only an acquisition lead and produces `WUTNOTICE2004` when it is the sole evidence.
- Multiple manifest or evidence candidates are preserved/reported with `WUTNOTICE2003`, never selected by filesystem order.

### Limits

NuGet JSON depth is capped at 128. Path containment and the 16 MiB per-evidence limit apply. A future lowering of a limit is breaking for inputs that previously succeeded and therefore requires contract review.

## npm adapter

### Inputs and selection

- Required: repository root, relative `package-lock.json` or `npm-shrinkwrap.json`, explicit workspace relative path (`.` for root), and `Runtime` or `Development` profile.
- Only lockfile versions 2 and 3 with a `packages` map are supported. Other formats produce `WUTNOTICE1006`.
- The selected workspace must have exactly one matching lock entry and a contained `package.json`; the adapter never infers a workspace.
- The workspace manifest and lock declaration are cross-checked. Runtime inventory excludes development-only reachability; development inventory includes it. Optional, peer, bundled, and development classifications are retained.
- Links must resolve to contained entries in the same lock graph. Missing exact resolutions produce `WUTNOTICE1007`; malformed graphs, duplicate canonical identities, invalid integrity, or conflicting entries produce `WUTNOTICE1008`.

### Identity and local inspection

- Identity is `pkg:npm/<encoded-name>@<encoded-version>`; scoped names retain their decoded `@scope/name` semantics and canonical percent encoding.
- Restored packages are inspected only below the selected `node_modules` graph. The adapter MUST NOT execute Node.js, npm, install hooks, package scripts, or executable package content.
- `license` must be a single non-empty SPDX expression string. URL-only values are not expressions and produce `WUTNOTICE2004`.
- License, notice, attribution, author, and modification evidence candidates are read as raw bytes, SHA-256 hashed, then strictly decoded. Multiple license files produce `WUTNOTICE2003`; invalid/oversized text produces `WUTNOTICE2005`.

### Limits

| Resource | Limit |
|---|---:|
| Lock or package JSON file | 16 MiB |
| Evidence file | 16 MiB |
| JSON depth | 64 |
| JSON properties | 250,000 |

## Manual adapter

The Wave A version 1 manual configuration and scanner remain normative and unchanged. Manual components participate in the same canonical PURL deduplication and ordering as ecosystem results. Wave B orchestration MAY merge manual, NuGet, and npm results only after each adapter has independently validated its selected source. Cross-source duplicate identities are errors; source precedence MUST NOT silently discard a component.

## Adapter version/cache key

An inventory cache key includes the unmodified lockfile digest, assets/manifest digest as applicable, selected target/workspace/profile, adapter contract version, and every local metadata/evidence digest used. It excludes absolute roots. Changing an adapter's interpretation or limit invalidates its cache version. `--no-cache` MUST produce byte-identical results.
