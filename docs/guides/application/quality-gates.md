# Quality gates

These gates are cumulative. A milestone is incomplete until its implementation,
durable artifacts, documentation, tests, and required gate evidence are committed.

Workflows may implement changes and Bun may run the deterministic checks below.
Passing workflow checks is evidence for the current worktree, not authority to
activate a wave, publish, or deploy. The user makes those decisions explicitly.
Only evidence with independent future value needs to be committed; raw agent
transcripts, local journals, and successful intermediate logs remain transient.

## Current native product acceptance

The primary repository gate remains `eng/verify.ps1`. Inside the Nix/direnv
environment it also runs `eng/verify-cswebui-native-e2e.ps1`, which requires
the pinned `CSWEBUI_NATIVE_LIBRARY` and `WEBUI_BROWSER_PATH`.

The native script full-trim Native-AOT-publishes two executables and drives both
through persistent headless Chromium against a real CsWebUi server:

- the binary MVVM host proves the production `CsWebUiFrameChannel` round trip;
- SimpleTodo submits its compiled form through the shipped native HTMX bridge,
  executes a C# command, and verifies the replaced compiled-fragment DOM.

`eng/verify-todo-frontends.ps1` then runs SimpleTodo and AdvancedTodo through
React, Vue, Svelte, and Angular using the production binary FrameChannel. Each
of the eight serial pinned-Chromium cases changes framework-rendered input,
executes the shared C# ViewModel, and verifies the resulting framework-rendered
task. Advanced cases additionally observe the asynchronous import's pushed
busy-state transition and imported tasks without a refresh command. These cases
are managed browser gates; Native-AOT publication of the framework applications
remains Phase 6 work.

AdvancedTodo's compiled `.cwhtml`/native-HTMX source and managed self-test are
implemented, but that application is not yet part of this Chromium/Native-AOT
gate. The cwhtml development experience, WPF capability expansion, a
framework-adapter application on the proven native path, and asset/VFS
extraction remain future roadmap work. The older G0–G7 sections below preserve
cumulative historical release evidence; their existence does not mark those
re-centered product outcomes complete.

## Planned cwhtml developer-experience acceptance

Phase 3 adds a consumer-facing gate around the
[cwhtml development-experience plan](./cwhtml-development-experience.md). It
must prove, on SimpleTodo and AdvancedTodo, that:

- the shared frontend SDK removes application-authored cwhtml targets, toolkit
  asset-copy targets, native-HTMX descriptors, and render-plan registration;
- the development command coordinates CsWebUi, Vite, compilation, diagnostics,
  asset HMR, cwhtml reload, and application restart without routing native HTMX
  actions through HTTP;
- production assets are minified, content-hashed, deterministic, entirely
  local, and free of development-server references; and
- the resulting full-trim Native-AOT applications pass real-browser round trips
  while offline from external asset services.

Exact latency budgets will be frozen only after repeatable measurements exist
for the declared reference machine and application. Diagnostic identity and
source locations must already agree across build, command line, browser
overlay, and editor before the phase is accepted.

## G0: Repository and identity

- A clean clone restores without private feeds or machine-local inputs.
- SDK, package, frontend, and tool versions are centrally pinned with committed lock files.
- Owned root identifiers use `WebUIToolkit`; the external `cs-webui` project,
  `CsWebUi` package/namespace, and explicitly named adapter remain unchanged.
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
- `./eng/verify-cswebui-native-e2e.ps1` Native-AOT-publishes both a binary-MVVM host and the compiled SimpleTodo HTMX application, drives them with the Nix-pinned Chromium, executes C# commands through their production native transports, and verifies the resulting DOM. `eng/verify.ps1` includes this gate when the direnv-provided native library and browser paths are available.
- The core vertical matrix runs the same scenario through the framework-neutral web SDK, CommunityToolkit, and compiled HTMX.
- Clean-clone, empty-cache, offline, package-consumer, browser, security, fuzz, stress, leak, compatibility, and performance suites pass.
- Release evidence includes API/package approvals, AOT logs, deterministic hashes, benchmark deltas, SBOM, provenance, migration notes, and compatibility matrices.

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

Run `./eng/verify-wave-e.ps1` for the cumulative G5 acceptance surface. It
executes G4 first, then runs the React, Vue, and Svelte package suites, Chrome
and Firefox fixtures, and both ends of every supported framework-version range
in isolated, bounded-parallel jobs. The gate rejects cross-adapter dependencies,
missing fixture IDs, skipped verticals, package-layout drift, source mutation,
and evidence-hash drift. Source-bound logs and provenance are written to the
ignored `artifacts/wave-e/` directory; durable review artifacts live in
`docs/release/wave-e/`.

## G6: Angular and ReactiveUI expansion

- Angular passes the shared browser conformance, signals/directive lifecycle, package-consumer, production-build, and supported-version matrix.
- ReactiveUI proves generated-member visibility, command/result/fault projection, activation, scheduler behavior, deterministic subscription disposal, trimming, and Native AOT.
- The complete adapter matrix is documented and versioned independently from the already-approved core release candidate.

Run `./eng/verify-wave-f.ps1` for the cumulative G6 acceptance surface. It
executes G5 first, then runs the Angular signal/directive package and ReactiveUI
runtime suites in parallel, produces deterministic npm and NuGet packages, runs
Chrome and Firefox fixtures, builds isolated Angular production consumers at
both supported endpoints, and runs isolated ReactiveUI consumers with the upper
endpoint published and executed under full trimming and Native AOT. Source-bound
logs, provenance, and `SHA256SUMS` are written to the ignored
`artifacts/wave-f/` directory; durable review artifacts live in
`docs/release/wave-f/`.

## G7: Neutral reference application and release rehearsal

- A neutral reference application restores only from packed artifacts and contains no project references.
- Hosting, MVVM reconnect, Flow navigation, Text Resources, Command Line, and WebUi scenarios execute concurrently and without semantic exclusions.
- Clean-cache, offline, coherent-upgrade, managed cross-publish, current-host full-trim Native-AOT, deterministic-package, provenance, and checksum lanes pass.
- Cross-publish evidence is not treated as native execution; the same verifier records native execution only on a matching host RID.
- Technical readiness does not override ADR 0004. Publication remains blocked until an owner accepts a replacement license ADR and verifies NuGet/npm identity ownership.

Run `./eng/verify-wave-g.ps1` for the cumulative G7 acceptance surface. It executes
G6 first, produces the approved NuGet/npm package set twice, validates byte-stable
normalized artifacts, runs the package-only reference application from clean and
offline caches, rehearses a coherent patch upgrade, cross-publishes the managed
application for Linux, Windows, and macOS, and executes a fully trimmed Native-AOT
binary on the current host. Source-bound logs, provenance, and `SHA256SUMS` are
written to the ignored `artifacts/wave-g/` directory; durable review artifacts
live in `docs/release/wave-g/`.

## Required early spikes

1. Publish and run a real upstream `cs-webui` Native-AOT round trip before expanding generator breadth.
2. Prove the two-stage compiler sees CommunityToolkit.MVVM generated members before Wave C; defer the equivalent ReactiveUI proof to the Wave F entry gate.
3. Resolve the single HTMX package boundary and the two opposite-direction Hosting/CLI adapters.
4. Reserve target NuGet/npm identities, schema domain, and non-colliding diagnostic ranges.
5. Establish a central target-framework policy without weakening the Text Resources compatibility target.
