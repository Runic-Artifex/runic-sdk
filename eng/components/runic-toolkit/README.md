# WebUIToolkit

Native-AOT-first reusable infrastructure for web UI applications, with compiled
C#/HTMX and TypeScript-framework frontend tracks, application flow, hosting,
text-resource, collection, and optional command-line packages built on top of
[cs-webui](https://github.com/ViktorJannicke/cs-webui).

Implementation uses the parent namespace and package identity `WebUIToolkit`. The planning documents retain the earlier `CsWebUi` draft name; [ADR 0001](./docs/adr/0001-webuitoolkit-identity.md) is authoritative.

The intended authoring model mirrors XAML-based MVVM: a framework component or compiled HTML template is the View, TypeScript or C# is optional code-behind, a C# ViewModel acts as the DataContext, and generated bindings connect properties, collections, validation, commands, and lifecycle without handwritten transport code.

The primary product goal is building CsWebUi applications and porting WPF
applications. [ADR 0012](./docs/adr/0012-native-html-and-frontend-direction.md)
defines the two supported frontend tracks, the native cwhtml/HTMX transport,
the future asset-package and cs-webui organization direction, the Bootstrap
5.3 and Font Awesome sample baseline, and the initial repository scope
reduction. The ordered work is maintained in the
[product roadmap](./docs/product-roadmap.md). The
[cwhtml development-experience plan](./docs/cwhtml-development-experience.md)
defines the intended Vite-powered development loop, generated HTMX authoring
surface, production asset pipeline, and editor experience. The
[WPF migration guide](./docs/wpf-migration.md) maps familiar desktop concepts
such as `DataContext`, bindings, commands, collections, validation, resources,
navigation, dialogs, and window services onto the toolkit.

The repository includes the implemented binary FrameChannel path and the native
compiled-C#/HTMX path alongside the original standalone HTML plans. Open the
plans directly in a browser; they have no external runtime dependencies and
include print styling for PDF export:

- [Overall implementation plan](./index.html)
- [Maud-inspired typed template engine and HTMX plan](./template-engine.html)
- [Reusable library candidate portfolio](./library-candidates.html)
- [Microsoft.Extensions.Hosting-based application host plan](./application-host.html)
- [Navigation, dialogs, operations, and workflows plan](./application-flow.html)
- [Generated Native-AOT text resources plan](./text-resources.html)
- [Native command-line hosting candidate plan](./command-line.html)
- [Observable range collection utility plan](./observable-range-collection.html)

The concrete desktop boundary is the `CsWebUi` NuGet package, not ASP.NET Core;
[ADR 0011](./docs/adr/0011-cs-webui-host-boundary.md) records the package and
runtime separation. The planned move of its repository into a WebUIToolkit
organization does not by itself rename the existing NuGet package or namespace.

Each detailed plan is a self-contained greenfield implementation specification. It defines its own contracts, project layout, dependency direction, diagnostics, tests, Native AOT gates, delivery phases, and acceptance criteria; implementation must not rely on an unpublished predecessor codebase or local machine context.

Agentic implementation follows
[ADR 0010](./docs/adr/0010-workflow-first-agent-orchestration.md) and the portable
[workflow operating manifest](./eng/codex-workflows/workflow-operating-manifest.md):
small one-off TypeScript workflows are preferred for bounded parallel work, Bun
runs deterministic checks, and model agents handle implementation and semantic
judgment. Planning remains user-owned, and workflows do not start delivery waves
automatically.

## Scope

This repository is intended to contain only reusable infrastructure:

- a versioned .NET-to-web protocol and runtime;
- a XAML-like binding compiler for direct C# dispatch, subscriptions, serializer metadata, and TypeScript contracts;
- portable BCL MVVM bindings for `INotifyPropertyChanged`, `INotifyCollectionChanged`, `INotifyDataErrorInfo`, and `ICommand`;
- first-party generator/runtime integrations for CommunityToolkit.MVVM and ReactiveUI;
- deterministic TypeScript contract generation;
- a framework-neutral TypeScript client;
- Angular signals/directives, React hooks, Vue composables, and Svelte runes/store integrations;
- a Maud-inspired, compile-time typed HTML engine with C# code-behind and HTMX fragment integration;
- an optional application host that composes Microsoft.Extensions.Hosting, cs-webui, MVVM, Application Flow, and hosted CLI execution;
- optional reusable mechanics for ViewModel-first navigation, typed dialogs, operations, and multi-step workflows;
- a separately packageable source-generated text-resource engine;
- an evaluated, separately packageable command-line/typed process-protocol candidate;
- Native AOT, package-consumer, and end-to-end validation;
- a neutral reference application.

Product-specific components, branding, application-flow definitions, resource
content, assets, operating-system capabilities, command implementations, and
consumer applications remain outside this repository. Generic mechanics may
incubate here while their plans and namespaces preserve a later split into
independent repositories.

## Build

The repository pins .NET SDK 10.0.302 and Node.js 24.18 or newer. Every
first-party .NET runtime, tool, build task, generator, sample, and test targets
`net10.0`; consuming generator builds therefore also require a .NET 10-capable
compiler host.

For normal development, enter the Nix/direnv shell and use the lightweight
managed inner loop:

```powershell
direnv allow
npm ci
./eng/dev.ps1
```

Individual projects and samples also work with ordinary `dotnet build`,
`dotnet test`, and `dotnet run` commands. Development restores may update lock
files and do not run release-only auditing, trim analysis, Native AOT analysis,
or package-feed rehearsals. See [development and verification
modes](./docs/development.md).

Run the strict release-facing checks explicitly:

```powershell
./eng/verify.ps1
```

When a dependency changes, refresh and commit lock files before verification:

```powershell
./eng/update-locks.ps1
```

## Samples

The [`samples`](./samples) directory is an ordered learning path built with
source project references. Every sample runs directly; start with the lifecycle
hello world, continue through command-line and MVVM projection examples, then
build the Simple and Advanced ToDo applications.

SimpleTodo and AdvancedTodo share their C# model/ViewModel layer with React,
Vue, Svelte, and Angular versions. Each framework project runs both teaching
levels over the same native CsWebUi binary channel and generated typed
contract, making adapter ergonomics directly comparable. Derived state and
background changes are pushed without browser polling, while
`WebUIToolkit.Frontend.Sdk` owns their Vite builds and manifests. The
implementation notes and remaining gaps are captured in the
[frontend Todo findings](./docs/frontend-todo-findings.md).

Bootstrap 5.3 and Font Awesome are the default visual language for desktop
samples because they match the WPF applications motivating this toolkit. They
remain locally served sample dependencies rather than mandatory core
dependencies; shadcn, Tailwind, raw CSS, and other component systems remain
supported directions.

Executable release and acceptance harnesses live separately under
[`tests/Fixtures`](./tests/Fixtures).

## Status

The repository has completed the narrowed Phase 0–2 product baseline:

- `CsWebUiFrameChannel` provides the production binary MVVM connection used by
  `04-NativeMvvmCounter`;
- the compiled `.cwhtml` runtime projects CommunityToolkit properties,
  commands, validation, and collections through one bounded native HTMX
  transport per window; and
- SimpleTodo exercises that stack without ASP.NET or handwritten operation
  callbacks. Its release gate Native-AOT-publishes the application, launches
  the Nix-pinned Chromium against the real CsWebUi server, submits its form,
  executes C#, and verifies the replaced DOM.

AdvancedTodo's source has also moved to compiled `.cwhtml` and the single native
HTMX transport. Its persistence, filtering, workflow, cancellation, and
diagnostic self-test remain useful migration coverage, but it is not yet part
of the real-browser/Native-AOT acceptance gate.

The next product work is the cwhtml developer-experience phase: extend the now
implemented frontend SDK and Vite asset pipeline to generate the remaining
native-HTMX plumbing and add a coordinated watch/HMR loop. Broader WPF
migration capability coverage, reusable sample components and styling
alternatives, alignment of the TypeScript framework adapters with the proven
native path, and the separately designed asset/VFS extraction follow. Those
are roadmap items, not capabilities implied by the completed SimpleTodo gate.

The existing Wave G implementation records the earlier G7 neutral-reference
and release-rehearsal gate. It remains historical verification evidence. The
cumulative [`eng/verify-wave-g.ps1`](./eng/verify-wave-g.ps1) entry point
preserves the complete G6 adapter matrix, then restores a package-only reference
application from isolated NuGet artifacts. It exercises Hosting, MVVM reconnect,
Flow navigation, Text Resources, Command Line, and WebUi,
rehearses clean/offline restore and coherent upgrades, cross-publishes the managed
consumer for the supported operating-system matrix, and executes the current-host
binary with full trimming and Native AOT. Generated, source-bound evidence is
written under `artifacts/wave-g/`.

ADR 0004 records an explicit publication hold: no source license has been granted, and public visibility does not grant reuse or redistribution rights. API ownership, dependency licensing, package names, notices, SBOM linkage, and publication terms must be reviewed before reusable artifacts are published.
