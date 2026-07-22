# Wave B G2 handoff contract

Wave B is ready for an owned-path handoff only when implementation and evidence satisfy this document. This file defines the expected report; it does not assert that a particular commit has passed.

## Required handoff report

- Branch and clean commit SHA, plus merge-base/main commit.
- Owned-path-only diff summary and confirmation that root/shared build, CI, solution, package graph, and other owners' locks were untouched.
- Implemented project/package manifest and dependency directions.
- Schema versions: Wave A v1 files preserved; complete document v2 additive; origin index v1; CLI/diagnostic contract versions.
- Exact restore/build/test/package-consumer/AOT commands and results, including total passed/failed/skipped tests.
- Portable locked-restore result for every committed project lock after AOT execution.
- Golden output hashes and repeated-root/culture/offline comparison result.
- Native executable RID/path/result and owned trim/AOT warning count.
- Security/adversarial fixture coverage and any residual risk.
- Deferred Wave C edges listed below, with no implicit claim of implementation.
- Final `git status --short` result.

## G2 acceptance evidence

| Area | Minimum evidence |
|---|---|
| Inventory | Deterministic NuGet/npm/manual fixtures including target ambiguity, drift, unresolved/malformed graph, scope/workspace/link cases, duplicate identity, hostile paths, encoding, and limits |
| Acquisition/store | Explicit-deny matrix, exact host/scheme/credential rules, every redirect revalidated, size/timeout/digest failure, atomic/concurrent store, corruption and cancellation |
| SBOM | CycloneDX/SPDX subset goldens; exact PURL, unique fallback, missing/extra/mismatch, duplicate reference, malformed and bounded inputs |
| Policy/model | Observed/effective/selected expressions remain distinct; stable decisions/diagnostics; complete v2 model |
| Renderers | Byte-golden JSON/text/HTML/manifest; hostile encoding; unchanged evidence body; drift and unsafe destination; atomic output set |
| CLI/runtime | Command mutation/network matrix, stable exit/JSON diagnostics, stream/path runtime load, unsupported schema, embedded resource and lookup |
| Distribution | Local packs consumed from a clean feed; tool and runtime behavior executed from packages |
| AOT/offline | Actual native binaries run; zero owned warnings; empty-cache/network-denied reproduction; portable locks revalidated afterward |

## Deferred Wave C edges

These are integration or breadth edges and MUST NOT be smuggled into G2 by editing another task's paths:

- root solution, shared `Directory.*`, root lockfile/package graph, repository CI, release automation, and shared governance changes;
- root integration of the thin `buildTransitive` package into arbitrary consumer projects and cross-task build orchestration;
- framework/UI adapters for displaying runtime notices;
- additional npm lock managers/legacy formats, NuGet central/transitive-lock breadth beyond the documented selected graph, and new package ecosystems;
- authenticated/private registry acquisition, credential providers, proxy credentials, origin discovery/search, signatures, transparency logs, and archive/decompression pipelines;
- complete CycloneDX/SPDX generation, validation of every external SBOM feature, SBOM signing, or notice annotation back into an SBOM;
- migration tooling between schema majors, hosted schema IDs/domain, remote schema resolution, and extension/plugin SDKs;
- legal classification, SPDX identifier/deprecation catalog updates, license-text equivalence, or automatic acceptance of discovered evidence;
- multi-RID release matrix, signing, provenance publication, and stable public NuGet release;
- performance baselines and cache persistence formats beyond correctness-neutral local acceleration.

Any Wave C consumer must depend on packed Wave B artifacts and versioned contracts. It may not reach into internal models or require an edit that changes a frozen Wave A contract.

## Known approval gates

The license/publication hold remains active. Document v2 and every new public API/diagnostic require the checklist in `public-api-approval.md`. Local G2 pack/AOT success demonstrates engineering fitness only; it does not approve legal publication or a stable 1.0 compatibility promise.
