# Quality gates

These gates are cumulative. A milestone is incomplete until its implementation, packed artifacts, documentation, tests, and evidence are committed.

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

## G3: Adapters and security

- Every supported adapter passes the same mandatory conformance fixture IDs without skipped rows.
- Protocol, HTML/HTMX, CLI/process, dependency evidence, archives, paths, URLs, and external packs have explicit limits and hostile-input tests.
- Stable faults are sanitized; logs and telemetry use bounded tags and never include sensitive payload values.
- Framework packages remain independent and share one protocol major and conformance kit.

## G4: Native AOT and release

- Packed consumers publish and run actual executables with full trimming and Native AOT, zero owned trim/AOT warnings, deterministic cleanup, and exit code zero.
- The vertical matrix runs the same scenario through Angular, React, Vue, Svelte, and compiled HTMX.
- Clean-clone, empty-cache, offline, package-consumer, browser, security, fuzz, stress, leak, compatibility, and performance suites pass.
- Release evidence includes API/package approvals, AOT logs, deterministic hashes, benchmark deltas, SBOM, dependency notices, provenance, migration notes, and compatibility matrices.

## Required early spikes

1. Publish and run a real upstream `cs-webui` Native-AOT round trip before expanding generator breadth.
2. Prove the two-stage compiler sees CommunityToolkit.MVVM and ReactiveUI generated members.
3. Resolve the single HTMX package boundary and the two opposite-direction Hosting/CLI adapters.
4. Reserve target NuGet/npm identities, schema domain, and non-colliding diagnostic ranges.
5. Establish a central target-framework policy without weakening the Text Resources compatibility target.
