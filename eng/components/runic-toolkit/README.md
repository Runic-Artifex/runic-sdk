# WebUIToolkit

WebUIToolkit is Native-AOT-first infrastructure for building desktop
applications on [CsWebUi](https://github.com/ViktorJannicke/cs-webui) and for
moving WPF applications to a modern browser-based frontend.

The intended model remains familiar to desktop MVVM developers: a C# ViewModel
is the application-facing state and behavior, a compiled HTML or framework
component is the View, and generated bindings connect properties, collections,
validation, commands, and lifecycle without handwritten transport code.

CsWebUi—not ASP.NET Core—owns the native window, embedded browser, and private
JavaScript bindings.

## Frontend tracks

| Track | Authoring model | Native transport |
| --- | --- | --- |
| C# markup / cwhtml + HTMX | TSX-shaped mixed C# (`.cwuix`) or declarative typed HTML views and fragments | One private HTMX binding per window |
| React, Vue, Svelte, Angular | Framework components over generated TypeScript contracts | Binary MVVM FrameChannel |

Both tracks share C# models and ViewModels, CommunityToolkit integration,
commands, validation, collections, application Flow, hosting, and desktop
capability services. Vite is an optional development and asset-build tool; it
does not become the application host.

## Start

On NixOS, use the checked-in flake and direnv environment:

```bash
direnv allow
pwsh ./eng/setup-development.ps1
dotnet build WebUIToolkit.slnx
dotnet run --project samples/SimpleTodo
```

Run the repository-local coordinated development tool with:

```bash
dotnet webuitoolkit doctor samples/SimpleTodo/SimpleTodo.csproj
dotnet webuitoolkit dev samples/SimpleTodo/SimpleTodo.csproj
```

The development loop coordinates .NET, CsWebUi, Vite, generated contracts,
browser diagnostics, CSS/JavaScript HMR, state-preserving compatible compiled-markup
renderer replacement, and safe restart fallback.

See [Getting started](./docs/getting-started/README.md) for the complete first
run and framework examples.

## Documentation

- [Documentation index](./docs/README.md)
- [Getting started](./docs/getting-started/README.md)
- [cwhtml development guide](./docs/guides/cwhtml.md)
- [C# markup 1.0 language contract](./spec/csharp-markup/language/1.0/README.md)
- [Frontend framework integration](./docs/guides/frontend-frameworks.md)
- [WPF migration guide](./docs/guides/wpf-migration.md)
- [Architecture](./docs/architecture/README.md)
- [Reference](./docs/reference/README.md)
- [Current product roadmap](./docs/roadmap/README.md)
- [Contributing and verification](./docs/contributing/README.md)

[ADR 0011](./docs/adr/0011-cs-webui-host-boundary.md) defines the CsWebUi host
boundary. [ADR 0012](./docs/adr/0012-native-html-and-frontend-direction.md)
defines the current compiled-HTML and frontend-framework direction.

## Samples

The [`samples`](./samples) directory is an ordered learning path:

1. application lifecycle and Generic Host composition;
2. typed command-line execution;
3. framework-neutral MVVM projection;
4. the production native binary FrameChannel;
5. SimpleTodo and AdvancedTodo through compiled C# markup and cwhtml/HTMX; and
6. both Todo levels through React, Vue, Svelte, and Angular.

The Todo variants share their C# model/ViewModel layer and use locally pinned
Bootstrap 5.3 and Font Awesome assets. Those libraries are sample and
customer-migration defaults, not toolkit dependencies. Tailwind, shadcn, raw
CSS, and other design systems remain valid consumer choices.

Executable release and acceptance harnesses live under
[`tests/Fixtures`](./tests/Fixtures), not in the teaching sequence.

## Build and verification

The repository pins .NET SDK 10.0.302 and Node.js 24.18 or newer. Every
first-party .NET runtime, tool, build task, generator, sample, and test targets
`net10.0`.

Use the fast managed inner loop while editing:

```powershell
./eng/dev.ps1
```

Run strict release-facing verification explicitly:

```powershell
./eng/verify.ps1
```

Refresh and commit dependency lock files when dependencies change:

```powershell
./eng/update-locks.ps1
```

The [development modes](./docs/contributing/development.md) and
[quality gates](./docs/contributing/quality-gates.md) explain the difference.

## Current direction

The native transports, shared ViewModels, frontend integrations, Vite pipeline,
browser diagnostics, three live-feedback tiers, high-level application
composition, project templates, and environment doctor are implemented. The
WPF migration capability layer, generated aggregate framework façades,
reusable sample components, and framework-neutral asset/VFS boundary are also
implemented. The cs-webui organization transfer remains external maintainer
coordination.

Developer-experience parity across C# markup/cwhtml/HTMX, React, Vue, Svelte, and Angular
is implemented. The production compiler backs a Language Server Protocol host
and a packaged first-party VS Code extension with project-aware C# semantics,
safe cross-language rename, generated-artifact inspection, and inspection of
the live fragment rendered in the native window. All roadmap work controlled
by this repository is complete; the [roadmap](./docs/roadmap/README.md) retains
the external cs-webui organization transfer for maintainer coordination.

Earlier wave plans and release evidence remain available under
[`docs/roadmap/archive`](./docs/roadmap/archive) and
[`docs/release`](./docs/release), but they do not describe current priorities.

## Publication status

[ADR 0004](./docs/adr/0004-license-pending.md) records an explicit publication
hold. Public visibility does not grant reuse or redistribution rights. API
ownership, dependency licensing, package identities, notices, SBOM linkage,
and publication terms must be reviewed before reusable artifacts are
published.
