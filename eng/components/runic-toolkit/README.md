# WebUIToolkit

Native-AOT-first reusable infrastructure for web UI applications, with Angular, React, Vue, Svelte, compiled HTML/HTMX, application flow, hosting, command-line, text-resource, collection, and dependency-notice packages built on top of [cs-webui](https://github.com/ViktorJannicke/cs-webui).

Implementation uses the parent namespace and package identity `WebUIToolkit`. The planning documents retain the earlier `CsWebUi` draft name; [ADR 0001](./docs/adr/0001-webuitoolkit-identity.md) is authoritative.

The intended authoring model mirrors XAML-based MVVM: a framework component or compiled HTML template is the View, TypeScript or C# is optional code-behind, a C# ViewModel acts as the DataContext, and generated bindings connect properties, collections, validation, commands, and lifecycle without handwritten transport code.

The repository now includes the Wave A contract/runtime baseline alongside the original standalone HTML plans. Open the plans directly in a browser; they have no external runtime dependencies and include print styling for PDF export:

- [Overall implementation plan](./index.html)
- [Maud-inspired typed template engine and HTMX plan](./template-engine.html)
- [Reusable library candidate portfolio](./library-candidates.html)
- [Microsoft.Extensions.Hosting-based application host plan](./application-host.html)
- [Navigation, dialogs, operations, and workflows plan](./application-flow.html)
- [Generated Native-AOT text resources plan](./text-resources.html)
- [Native command-line hosting candidate plan](./command-line.html)
- [Observable range collection utility plan](./observable-range-collection.html)
- [Dependency notices and SBOM-linked generator plan](./dependency-notices.html)

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

The repository pins .NET SDK 10.0.302 and Node.js 24.18 or newer.

```powershell
npm ci
./eng/verify.ps1
```

When a project or dependency changes, refresh and commit lock files before verification:

```powershell
./eng/update-locks.ps1
```

## Status

Wave G implements the G7 neutral-reference and release-rehearsal gate. The
cumulative [`eng/verify-wave-g.ps1`](./eng/verify-wave-g.ps1) entry point
preserves the complete G6 adapter matrix, then restores a package-only reference
application from isolated NuGet artifacts. It exercises Hosting, MVVM reconnect,
Flow navigation, Text Resources, Command Line, Dependency Notices, and WebUi,
rehearses clean/offline restore and coherent upgrades, cross-publishes the managed
consumer for the supported operating-system matrix, and executes the current-host
binary with full trimming and Native AOT. Generated, source-bound evidence is
written under `artifacts/wave-g/`.

ADR 0004 records an explicit publication hold: no source license has been granted, and public visibility does not grant reuse or redistribution rights. API ownership, dependency licensing, package names, notices, SBOM linkage, and publication terms must be reviewed before reusable artifacts are published.
