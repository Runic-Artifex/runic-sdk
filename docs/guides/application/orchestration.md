# Parallel implementation orchestration

The orchestrator owns shared files and activates domain tasks only when their entry gate is satisfied. The concurrency ceiling is one orchestrator plus twelve active agents; long-lived tasks are parked between waves. `eng/ownership.json` is the operational branch/path registry and is authoritative when this summary is ambiguous.

## Waves

| Wave | Purpose | Exit gate |
|---|---|---|
| A | Bootstrap, feasibility, and contract freeze | G1 approved APIs, schemas, diagnostic ranges, and conformance corpora; publication license hold recorded |
| B | Kernels, compilers, generators, and deterministic fixtures | G2 packed local artifacts with runtime/compiler tests |
| C | Framework and integration adapters | G3 shared conformance and package-consumer evidence |
| D | Vertical integration, hardening, and release | G4 trim/AOT, offline reproducibility, notices/SBOM, API approvals |

## Long-lived task ownership

| Task | Owned paths | First wave | Entry dependency |
|---|---|---|---|
| orchestrator | Root solution/build/CI/ADRs/registry/local feed | A | None |
| mvvm-protocol-core | `protocol/mvvm/**`, `src/WebUIToolkit.MVVM/**`, matching tests | A | G0 and upstream `cs-webui` AOT spike |
| mvvm-compiler-build | `src/WebUIToolkit.MVVM.Build/**`, `tools/WebUIToolkit.MVVM.BindingCompiler/**` | B | G1 protocol and binding vocabulary |
| web-sdk-conformance | `web/packages/mvvm/**`, `web/packages/conformance/**` | B | G1 wire schema and fixtures |
| angular | `web/packages/mvvm-angular/**` | C | G2 compiler/runtime/conformance |
| react | `web/packages/mvvm-react/**` | C | G2 compiler/runtime/conformance |
| vue | `web/packages/mvvm-vue/**` | C | G2 compiler/runtime/conformance |
| svelte | `web/packages/mvvm-svelte/**` | C | G2 compiler/runtime/conformance |
| communitytoolkit | `src/WebUIToolkit.MVVM.CommunityToolkit/**`, matching tests | C | G2 portable MVVM and compiler hooks |
| reactiveui | `src/WebUIToolkit.MVVM.ReactiveUI/**`, matching tests | C | G2 portable MVVM and compiler hooks |
| template-engine | Explicit non-HTMX Html runtime/compiler/build/testing paths in `eng/ownership.json` | A | G0 feasibility; G1 integration edge |
| htmx | `src/WebUIToolkit.MVVM.Html.Htmx/**`, matching tests/sample | C | Template safety gate and G2 MVVM |
| hosting | Explicit Hosting project/test/sample paths in `eng/ownership.json` | A | G0; MVVM/CLI only through adapters |
| flow | Explicit Flow project/test/sample paths in `eng/ownership.json` | A | Minimal MVVM and Microsoft.Extensions abstractions; never Hosting |
| text-resources | `src/WebUIToolkit.TextResources*/**`, `spec/text-resources/**`, schemas/corpus/tests | A | G0; edge projections use versioned contracts |
| command-line | `src/WebUIToolkit.CommandLine*/**`, evaluation/tests | A | Parser evaluation ADR before kernel |
| collections | `src/WebUIToolkit.Collections*/**`, tests/benchmarks | A | G0; BCL only |
| dependency-notices | `src/WebUIToolkit.DependencyNotices*/**`, fixtures/tool/runtime/tests | A | G0 governance and locked fixtures |

## Merge order

Bootstrap merges first. The dependency order is neutral protocol/schema contracts, MVVM runtime, compiler and web SDK, independent runtimes, framework and MVVM adapters, template engine, HTMX, Flow adapters, Hosting integration, the vertical consumer, then workspace hardening.

Tasks do not edit another owner's paths. A cross-task change is delivered as a versioned package, schema, corpus, manifest, public API approval, or ADR request.
