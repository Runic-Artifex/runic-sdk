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

The native script full-trim Native-AOT-publishes three executables and drives all
through persistent headless Chromium against a real CsWebUi server:

- the binary MVVM host proves the production `CsWebUiFrameChannel` round trip;
- SimpleTodo submits its compiled form through the shipped native HTMX bridge,
  executes a C# command, and verifies the replaced compiled-fragment DOM; and
- AdvancedTodo exercises its generated registration and high-level application
  lifetime through the same native bridge and compiled-fragment replacement.

`eng/verify-todo-frontends.ps1` then runs SimpleTodo and AdvancedTodo through
React, Vue, Svelte, and Angular using the production binary FrameChannel. Each
of the eight serial pinned-Chromium cases changes framework-rendered input,
executes the shared C# ViewModel, and verifies the resulting framework-rendered
task. The common harness also:

- rejects unlabeled controls/buttons, duplicate IDs, broken ARIA references,
  and invalid heading structure before and after dynamic updates;
- proves SimpleTodo command admission and AdvancedTodo validation projection;
- starts and cancels AdvancedTodo's delayed import, checks that cancellation
  did not partly persist, then separately observes a successful pushed import;
- replaces the native channel three times, requiring an authoritative
  reconnect snapshot each time and exactly one retained sentinel task; and
- disposes each generated adapter/ViewModel graph, forces collection through a
  weak reference, and treats browser/process/profile cleanup failures as gate
  failures.

`eng/verify-todo-frontend-hmr.ps1` exercises the coordinated development path
separately. It starts each real frontend development server, creates a Todo in
the native C# ViewModel, applies a live edit to the shared stylesheet, waits
for the framework's HMR result in the existing native document, and requires
the exact ViewModel-backed Todo to remain. React, Vue, and Svelte run through
Vite; Angular runs through `ng serve`. The stylesheet is restored in the
harness cleanup path even when a gate fails.

Run `eng/verify-todo-frontends-native-aot.ps1` in the Nix/direnv shell for the
release lane. It full-trim Native-AOT-publishes all four framework hosts with
owned trim/AOT warnings promoted to errors, then runs both Todo levels from
each native executable through the same eight browser gates. RID-specific
restore state stays under ignored `obj/todo-native-aot/` paths and each
portable lock-file hash is checked after publication. Both scripts print their
wall-clock cost; no browser or AOT case is skipped after the two pinned
prerequisites have been admitted.

Both compiled `.cwhtml` Todo levels now have managed and Chromium/Native-AOT
gates, and the framework adapters use the proven native path. The WPF
capability layer, frontend ergonomics, reusable sample components, and
in-repository asset/VFS extraction are implemented. Cross-frontend
developer-experience parity is now the next in-repository roadmap priority;
editor tooling remains deferred until it is complete. The older G0–G7 sections
below preserve cumulative historical release evidence; their existence does
not mark other product outcomes complete.

## Reordered roadmap acceptance

- `tests/WebUIToolkit.Desktop.Tests` covers complete capability reports,
  browser operations, guarded close, stopping cancellation, and deterministic
  secondary-window ownership.
- `tests/WebUIToolkit.Hosting.CsWebUi.NativeE2E` drives pinned Chromium through
  both binary MVVM and the browser-storage desktop bridge.
- `tests/WebUIToolkit.MVVM.Flow.Tests` covers observable navigation/dialog
  outlets and reuse of current navigation guards for application close.
- `npm test` builds and tests the direct TypeScript client plus React, Vue,
  Svelte, and Angular packages and Todo consumers.
- `npm run verify:frontend-production` performs two clean builds and enforces
  deterministic raw, gzip-9, and Brotli-11 budgets.
- `tests/WebUIToolkit.Samples.Cwhtml.Components.Tests` renders Bootstrap and
  deliberately non-Bootstrap component compositions.
- `tests/WebUIToolkit.Assets.Tests` covers hostile paths, deterministic
  manifests, embedded assets, development refresh/drift, symbolic links,
  cancellation, and dependency neutrality.
- `tests/WebUIToolkit.Assets.PackageConsumer/Test-PackageConsumer.sh` packs the
  independent asset package, restores it in isolation, Native-AOT publishes,
  and executes it.

## First-run developer experience

Priority 2 is covered by focused executable suites and repository checks:

- `tests/WebUIToolkit.Templates.Tests` packs, installs, and instantiates all
  five `dotnet new` templates, then restores their actual package graphs from
  an isolated feed and completes every production build while rejecting
  repository paths, project references, sample harnesses, and missing locks;
- `tests/WebUIToolkit.DotNet.WebUIToolkit.Tests` verifies environment-doctor
  health, Node-free projects, generated contracts, and actionable failures;
- `tests/WebUIToolkit.Frontend.Sdk.Tests` verifies lock-identity install caching,
  parallel shared-workspace serialization, and that Vite development suppresses
  the production asset build;
- `eng/setup-development.ps1` proves the repository-local manifest and template
  setup path; and
- `eng/verify-solution.ps1` plus `eng/verify-architecture.ps1` keep the template
  pack and its acceptance project inside the canonical solution and ownership
  graph.

## Current cwhtml developer-experience acceptance

The cwhtml DX track adds a consumer-facing gate around the
[cwhtml development guide](../guides/cwhtml.md). It
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
source locations already agree across build, command line, and browser
overlay; the deferred editor must consume the same contracts.

## Experimental C# markup Milestone 0

Run `eng/verify-csharp-markup-milestone0.ps1` in the Nix/direnv environment to
verify the recursive C#/markup feasibility spike. The focused gate builds and
runs the ambiguity, deterministic-projection, source-location, and fail-closed
HTML-safety compiler tests; builds and runs the complete managed `.cwuix`
vertical with a CommunityToolkit peer generator; then full-trim Native-AOT
publishes and executes that same vertical with owned warnings treated as
errors. RID-specific restore state remains isolated and the portable lock file
must not change.

Passing this focused gate is evidence for the experimental language direction
only. It does not add a supported package, stable syntax, editor integration,
or CWHTML compatibility claim. Those remain gated by the later milestones in
the [C# markup components plan](../roadmap/csharp-markup-components.md).

## Experimental C# markup Milestone 1

Run `eng/verify-csharp-markup-milestone1.ps1` in the Nix/direnv environment to
verify the shared semantic-core milestone. It includes the Milestone 0 gate and
adds the frozen CWHTML compiler/corpus suite, cross-language semantic/render-plan
differentials, contextual-safety parity, compiled HTMX, reusable CWHTML sample
components, the complete CWHTML language-server regression suite, and a
full-trim Native-AOT executable generated from an authored `.cwhtml` file.

The gate treats owned trim/AOT warnings as errors for both native verticals,
uses isolated RID restore state, and rejects portable lock-file changes. Passing
it preserves CWHTML 1.0 and approves the internal shared core and render-plan
prototype; it does not stabilize the `.cwuix` parser, source map, runtime, or
package surface.

## Experimental C# markup Milestone 2

Run `eng/verify-csharp-markup-milestone2.ps1` in the Nix/direnv environment to
verify the lossless parser and projection-map milestone. It includes the full
Milestone 1 gate, then requires the frozen language 0.1 clean, invalid, and
ambiguity corpus; delimiter-deletion recovery matrix; bidirectional and
discontinuous interval-map vectors; nested-island ancestry; exact preservation
of copied C# tokens; and 2,000 seeded property/fuzz inputs for termination,
determinism, losslessness, and span containment.

Passing this gate freezes the experimental 0.1 parsing and mapping contract for
Milestone 3 binding work. It does not stabilize component resolution, prop
binding, the materialized runtime, editor integration, a package, or a public
API.

## Experimental C# markup Milestone 3

Run `eng/verify-csharp-markup-milestone3.ps1` in the Nix/direnv environment to
verify consumer-compilation Roslyn binding. It includes the complete Milestone
2 gate and adds structure/context projection equivalence, component symbol and
candidate identities, generic inference, overload and accessibility behavior,
named/default/duplicate/missing props, nullable and implicit conversions,
supported and rejected child categories, null content, capture/ref-like
classification, cross-file `.cwuix` binding, ordinary-source components,
peer-generator deferral, exact diagnostic/edit mapping, generator-driver
emission, and semantic cache invalidation.

Passing this gate freezes the experimental binding decisions used by Milestone
4. It does not stabilize the materialized runtime, direct-writer ABI, capture
lowering, performance, editor integration, package, or public API.

## Experimental C# markup Milestone 4

Run `eng/verify-csharp-markup-milestone4.ps1` in the Nix/direnv environment to
verify deterministic generation and the typed runtime ABI. It includes the
complete Milestone 3 gate and adds frozen direct-writer output shape, static and
captured-state factories, typed sequences, component writer entries,
left-to-right/exactly-once evaluation, repeated-render behavior, ref-like
escape legality, zero-allocation static construction, render allocation and
throughput budgets against an equivalent CWHTML writer plan, trim analysis,
full-trim Native AOT, and hostile-input regressions.

Passing this gate freezes the experimental Milestone 4 lowering contract used
by application-parity work. It does not stabilize dynamic URL syntax, HTMX
application generation, editor integration, a package, or a public API.

## Experimental C# markup Milestone 5

Run `eng/verify-csharp-markup-milestone5.ps1` in the Nix/direnv environment to
verify HTMX/MVVM application parity. It includes the complete Milestone 4 gate,
then builds and runs the generated C# markup variants of SimpleTodo and
AdvancedTodo through their managed scenarios and the repository-pinned native
CsWebUi/Chromium path. The scenarios cover closed view/document registration,
typed fragments/actions/fields/commands/collections, opaque routes, validation,
persistence, Flow navigation, safe import cancellation, fragment replacement,
assets, and teardown without changing either ViewModel.

The gate also full-trim Native-AOT publishes both sample executables with ILC
warnings treated as errors, then repeats their managed and real-browser C#
markup paths. The inherited HTMX and Todo runtime suites retain reconnect,
validation, cancellation, accessibility, leak, cleanup, and lifecycle coverage.
The gate requires the pinned `CSWEBUI_NATIVE_LIBRARY` and
`WEBUI_BROWSER_PATH`, isolates RID-specific state under `obj`, and rejects
portable lock-file changes.

Passing this gate approves Milestone 6 build and development-loop work. It does
not stabilize the source-injected application attributes, generated type names,
transitive build/package integration, editor experience, or a public API.

The SimpleTodo native Chromium gate now covers all three delivered
live-feedback paths: CSS/JavaScript HMR, the versioned cwhtml diagnostics
overlay, and compatible renderer replacement with affected-fragment refresh.
It proves that the replacement renders new output only after .NET Hot Reload
acknowledges it, uses the private CsWebUi binding, and retains the browser
document and ViewModel state. Editor diagnostic parity remains acceptance work.

## C# markup Milestone 6

Run `eng/verify-csharp-markup-milestone6.ps1`. The gate builds the transitive
build package and development CLI, exercises all templates, performs repeated
sample builds, compares manifest/diagnostics/hot-reload hashes, and verifies
that CLI inspection reads the stable production artifact.

## C# markup Milestone 7

Run `eng/verify-csharp-markup-milestone7.ps1`. In addition to Milestone 6, the
gate runs the complete C# markup editor suite, the CWHTML regression suite, and
the VS Code extension build and tests. It covers nested/incomplete projections,
Roslyn navigation and rename, combined semantic tokens, formatting, quick
fixes, cancellation, stale documents, and the 1,000-element stress case.

## C# markup Milestone 8 / stable 1.0

Run `eng/verify-csharp-markup-milestone8.ps1` from the Nix/direnv environment.
It is cumulative: parser fuzz and hostile corpora, runtime/performance/trim
analysis, locked isolated package-only restore and build, deterministic clean
roots/cultures, Native AOT, both native Chromium applications, editor and
template coverage, and full repository verification must pass. Local iteration
may use `-SkipNativeAot` or `-SkipNativeBrowser`; neither switch is permitted
for release evidence.

## G0: Repository and identity

- A clean clone restores without private feeds or machine-local inputs.
- SDK, package, frontend, and tool versions are centrally pinned with committed lock files.
- Owned root identifiers use `WebUIToolkit`; the external `cs-webui` project,
  `CsWebUi` package/namespace, and explicitly named adapter remain unchanged.
- Architecture checks enforce dependency direction and exclusive path ownership.
- ADR 0014 records the repository's MIT license; publication remains separately
  gated by identity, dependency, notice, security, and release review.

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
- `./eng/verify-cswebui-native-e2e.ps1` Native-AOT-publishes a binary-MVVM host plus both compiled Todo HTMX applications, drives them with the Nix-pinned Chromium, executes C# commands through their production native transports, and verifies the resulting DOM. `eng/verify.ps1` includes this gate when the direnv-provided native library and browser paths are available.
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
- Technical readiness does not itself authorize a release. Publication remains
  blocked until NuGet/npm identity ownership and the remaining release gates are verified.

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
