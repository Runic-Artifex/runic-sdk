# Quality gates

These gates are cumulative. A milestone is incomplete until its implementation,
durable artifacts, documentation, tests, and required gate evidence are committed.

Workflows may implement changes and Bun may run the deterministic checks below.
Passing workflow checks is evidence for the current worktree, not authority to
activate a wave, publish, or deploy. The user makes those decisions explicitly.
Only evidence with independent future value needs to be committed; raw agent
transcripts, local journals, and successful intermediate logs remain transient.

## G0: Repository and identity

- A clean clone restores without private feeds or machine-local inputs.
- SDK, package, frontend, and tool versions are centrally pinned with committed lock files.
- Owned identifiers use `WebUIToolkit`; external lowercase `cs-webui` remains unchanged.
- Architecture checks enforce dependency direction and exclusive path ownership.
- ADR 0004 records an explicit publication hold; reusable publication remains blocked until a later license ADR replaces it.

## G1: Contracts and test foundations

- Public APIs, schema/protocol versions, diagnostics, event IDs, and serialized keys are registered before becoming stable.
- Tests assert observable order, exact-once ownership/disposal, cancellation, races, failure precedence, reconnect, and stale-message rejection.
- Generator/compiler diagnostics have stable IDs, exact spans, golden output, deterministic incremental behavior, and hostile-input coverage.
- Language-neutral conformance corpora are checked in and reproducible without hidden services.

## G2: Packages and kernels

- Build and pack first; empty consumers restore only from isolated local feeds and caches.
- Package content, dependency direction, analyzer/build asset transitivity, metadata, docs, and public API baselines are approved.
- Runtime paths use closed generated factories and source-generated serialization; reflection discovery and dynamic code are forbidden.
- Deterministic outputs are byte-identical across clean roots, cultures, repeated runs, and supported operating systems.

## G3: First-party integration and security

- CommunityToolkit, HTMX, Hosting, Flow, and projection integrations pass their mandatory conformance fixture IDs without skipped rows.
- Protocol, HTML/HTMX, CLI/process, dependency evidence, archives, paths, URLs, and external packs have explicit limits and hostile-input tests.
- Stable faults are sanitized; logs and telemetry use bounded tags and never include sensitive payload values.
- Integration packages remain independent and share one protocol major and conformance kit.

Run `./eng/verify-wave-c.ps1` for the durable Wave C acceptance surface. It
validates the mandatory 4-by-9 first-party matrix, focused executable suites,
isolated package consumers, samples, pinned browser assets, CSP configuration,
and browser-fixture wiring. This is G3 evidence only: the script deliberately
does not claim the Native-AOT package-consumer requirements of G4.

## G4: Native AOT and core release candidate

- Packed consumers publish and run actual executables with full trimming and Native AOT, zero owned trim/AOT warnings, deterministic cleanup, and exit code zero.
- Repository smoke projects use `./eng/verify-native-aot.ps1`; RID-specific restore state is isolated under ignored `obj/aot.packages.lock.json` files so committed locks remain portable.
- The core vertical matrix runs the same scenario through the framework-neutral web SDK, CommunityToolkit, and compiled HTMX.
- Clean-clone, empty-cache, offline, package-consumer, browser, security, fuzz, stress, leak, compatibility, and performance suites pass.
- Release evidence includes API/package approvals, AOT logs, deterministic hashes, benchmark deltas, SBOM, dependency notices, provenance, migration notes, and compatibility matrices.

Run `./eng/verify-wave-d.ps1` for the cumulative G4 acceptance surface. The
verifier executes the G3 regression gate, replays the repository from a clean
temporary root and empty caches, runs the shared three-consumer vertical,
publishes repository and packed-consumer Native-AOT executables, exercises the
offline package consumer and hardening suites in bounded parallel batches, and
applies the performance gate. It writes source-bound logs, provenance, and
`SHA256SUMS` to the ignored `artifacts/wave-d/` directory; durable review
artifacts live in `docs/release/wave-d/`.

React, Vue, Svelte, Angular, and ReactiveUI are explicitly outside the Wave D core release-candidate gate. Their later packages may not weaken or silently revise the frozen G4 contracts.

## G5: React, Vue, and Svelte expansion

- React, Vue, and Svelte consume the frozen framework-neutral SDK without cross-adapter dependencies.
- All three adapters pass the same browser conformance IDs, package-consumer checks, lifecycle/leak suites, generated-type checks, and supported-version matrices.
- The vertical scenario runs through each adapter with no semantic exclusions; framework-specific skips require an explicit compatibility decision.

## G6: Angular and ReactiveUI expansion

- Angular passes the shared browser conformance, signals/directive lifecycle, package-consumer, production-build, and supported-version matrix.
- ReactiveUI proves generated-member visibility, command/result/fault projection, activation, scheduler behavior, deterministic subscription disposal, trimming, and Native AOT.
- The complete adapter matrix is documented and versioned independently from the already-approved core release candidate.

## Required early spikes

1. Publish and run a real upstream `cs-webui` Native-AOT round trip before expanding generator breadth.
2. Prove the two-stage compiler sees CommunityToolkit.MVVM generated members before Wave C; defer the equivalent ReactiveUI proof to the Wave F entry gate.
3. Resolve the single HTMX package boundary and the two opposite-direction Hosting/CLI adapters.
4. Reserve target NuGet/npm identities, schema domain, and non-colliding diagnostic ranges.
5. Establish a central target-framework policy without weakening the Text Resources compatibility target.
