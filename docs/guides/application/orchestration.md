# Parallel implementation orchestration

The user controls wave activation and the orchestrator owns shared repository files.
The concurrency ceiling is thirteen active agents. `eng/ownership.json` is the
authoritative path registry when this summary is ambiguous.

## Workflow-first execution

ADR 0010 and the
[workflow operating manifest](../eng/codex-workflows/workflow-operating-manifest.md)
define the current coordination model. Task-specific TypeScript workflows replace
manual coordination of separate Codex tasks and subagents for bounded parallel
work. They are one-off by default and are promoted only after repeated use shows a
stable recurring shape.

Planning is user-owned. A user may prepare `/plan`, HTML, Markdown, or conversational
guidance, but the workflow runtime does not enforce a separate Plan run, approved
JSON manifest, plan hash, or preparation commit.

Bun owns restores, builds, tests, formatting, architecture checks, Git inspection,
telemetry, and resume state. Agents own discovery, implementation, diagnosis,
semantic review, and synthesis. Model and reasoning selection is contextual: Luna,
Terra, or Sol may be selected independently for any phase.

Successful workflows normally leave reviewed changes uncommitted in the invoking
worktree. Path ownership and dependency order still apply, but they do not require
one long-lived task, branch, and worktree per domain. Automatic commit, merge, push,
publication, or wave activation requires explicit authorization.

The 2026-07-22 Wave C readiness and remediation definitions are historical
experiments. Their approved Plan, immutable-baseline, and offline-restore
architecture is not the template for future work. The next remediation attempt
will use a small one-off workflow with normal trusted-local development access.
Wave C remains inactive until the user explicitly starts it.

## Waves

| Wave | Purpose | Exit gate |
|---|---|---|
| A | Bootstrap, feasibility, and contract freeze | G1 approved APIs, schemas, diagnostic ranges, and conformance corpora; publication license hold recorded |
| B | Kernels, compilers, generators, and deterministic fixtures | G2 packed local artifacts with runtime/compiler tests |
| C | First-party integration: CommunityToolkit, HTMX, Hosting/Flow/projection edges | G3 first-party conformance and package-consumer evidence |
| D | Core vertical integration, hardening, and release candidate | G4 trim/AOT, offline reproducibility, notices/SBOM, API approvals |
| E | React, Vue, and Svelte adapters | G5 shared browser conformance and framework package consumers |
| F | Angular and ReactiveUI adapters | G6 framework/reactive lifecycle, AOT, and compatibility evidence |

## Logical ownership lanes

| Task | Owned paths | First wave | Entry dependency |
|---|---|---|---|
| orchestrator | Root solution/build/CI/ADRs/registry/local feed | A | None |
| mvvm-protocol-core | `protocol/mvvm/**`, `src/WebUIToolkit.MVVM/**`, matching tests | A | G0 and upstream `cs-webui` AOT spike |
| mvvm-compiler-build | `src/WebUIToolkit.MVVM.Build/**`, `tools/WebUIToolkit.MVVM.BindingCompiler/**` | B | G1 protocol and binding vocabulary |
| web-sdk-conformance | `web/packages/mvvm/**`, `web/packages/conformance/**` | B | G1 wire schema and fixtures |
| angular | `web/packages/mvvm-angular/**` | F | G5 web-adapter evidence plus stable G4 core contracts |
| react | `web/packages/mvvm-react/**` | E | G4 core release-candidate contracts and web conformance |
| vue | `web/packages/mvvm-vue/**` | E | G4 core release-candidate contracts and web conformance |
| svelte | `web/packages/mvvm-svelte/**` | E | G4 core release-candidate contracts and web conformance |
| communitytoolkit | `src/WebUIToolkit.MVVM.CommunityToolkit/**`, matching tests | C | G2 portable MVVM and compiler hooks |
| reactiveui | `src/WebUIToolkit.MVVM.ReactiveUI/**`, matching tests | F | G4 API freeze and a dedicated ReactiveUI compiler/lifecycle spike |
| template-engine | Explicit non-HTMX Html runtime/compiler/build/testing paths in `eng/ownership.json` | A | G0 feasibility; G1 integration edge |
| htmx | `src/WebUIToolkit.MVVM.Html.Htmx/**`, matching tests/sample | C | Template safety gate and G2 MVVM |
| hosting | Explicit Hosting project/test/sample paths in `eng/ownership.json` | A | G0; MVVM/CLI only through adapters |
| flow | Explicit Flow project/test/sample paths in `eng/ownership.json` | A | Minimal MVVM and Microsoft.Extensions abstractions; never Hosting |
| text-resources | `src/WebUIToolkit.TextResources*/**`, `spec/text-resources/**`, schemas/corpus/tests | A | G0; edge projections use versioned contracts |
| command-line | `src/WebUIToolkit.CommandLine*/**`, evaluation/tests | A | Parser evaluation ADR before kernel |
| collections | `src/WebUIToolkit.Collections*/**`, tests/benchmarks | A | G0; BCL only |

## Merge order

Bootstrap merges first. The core dependency order is neutral protocol/schema contracts, MVVM runtime, compiler and web SDK, independent runtimes, CommunityToolkit integration, template engine, HTMX, Flow adapters, Hosting integration, the vertical consumer, then core workspace hardening. React, Vue, and Svelte branch from the frozen G4 SDK in Wave E; Angular and ReactiveUI follow in Wave F. Deferred adapters do not block the Wave D core release candidate.

Workflow writers do not edit another owner's paths. A cross-owner change is
delivered as a versioned package, schema, corpus, manifest, public API approval, or
ADR request. Multiple owners may participate in one workflow while their edits
remain disjoint and Bun verifies the combined result.
