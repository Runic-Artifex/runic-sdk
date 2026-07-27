# Getting started

This guide covers both the repository and a new application created from a
template.

## Enter the pinned environment

On NixOS, the checked-in flake and direnv configuration provide .NET 10, Node,
Chromium, the native CsWebUi library, and the Linux WebView dependencies:

```bash
direnv allow
pwsh ./eng/setup-development.ps1
dotnet build WebUIToolkit.slnx
```

Run commands from the repository root after direnv has loaded. See
[development modes](../contributing/development.md) for the difference between
the ordinary inner loop and release verification.

`setup-development.ps1` packs and restores the repository-local
`dotnet webuitoolkit` command and installs the repository's template pack.
Later checkouts only need `dotnet tool restore` unless those packages changed.

## Run the learning path

Start with the native lifecycle and MVVM samples:

```bash
dotnet run --project samples/01-HelloLifecycle
dotnet run --project samples/04-NativeMvvmCounter
dotnet run --project samples/SimpleTodo
dotnet run --project samples/AdvancedTodo
```

The [sample index](../../samples/README.md) explains the complete ordered path.
SimpleTodo is the smallest realistic cwhtml application. AdvancedTodo adds
persistence, filtering, workflows, cancellation, and diagnostics.

## Use the coordinated development loop

Check the selected application's prerequisites, then start the coordinated
loop:

```bash
dotnet webuitoolkit doctor samples/SimpleTodo/SimpleTodo.csproj
dotnet webuitoolkit dev samples/SimpleTodo/SimpleTodo.csproj
```

The command restores frontend packages only when the lock identity changes,
then coordinates .NET, CsWebUi, generated contracts, Vite, `dotnet watch`,
diagnostics, and shutdown. It does not run a redundant production asset build
before starting a Vite development server. In a native window:

- CSS and JavaScript use Vite HMR;
- compatible `.cwhtml` renderer edits use .NET Hot Reload and refresh only the
  affected fragment over the private CsWebUi HTMX binding;
- compiler errors use Vite's browser overlay; and
- incompatible generated changes restart the native host safely.

Vite serves development assets only. Application actions do not become HTTP
endpoints.

## Create a new application

The template pack includes cwhtml/HTMX, React, Vue, Svelte, and Angular:

```bash
dotnet new webuitoolkit-cwhtml -n MyApp
cd MyApp
dotnet tool restore
dotnet webuitoolkit dev
```

Replace `webuitoolkit-cwhtml` with `webuitoolkit-react`,
`webuitoolkit-vue`, `webuitoolkit-svelte`, or `webuitoolkit-angular`.
The local tool restore is the one setup step; the development command owns the
locked frontend install and native-window startup. Run
`dotnet webuitoolkit doctor` if a prerequisite is missing.

Published packages use the same local-manifest model:

```bash
dotnet new tool-manifest
dotnet tool install WebUIToolkit.DotNet.WebUIToolkit
dotnet new install WebUIToolkit.Templates
```

## Try a framework frontend

React, Vue, Svelte, and Angular use the same Todo ViewModels and generated
contract:

```bash
dotnet run --project samples/Todo.React
dotnet run --project samples/Todo.Vue -- --advanced
dotnet run --project samples/Todo.Svelte
dotnet run --project samples/Todo.Angular -- --advanced
```

Continue with the [cwhtml guide](../guides/cwhtml.md), the
[frontend-framework guide](../guides/frontend-frameworks.md), or the
[WPF migration guide](../guides/wpf-migration.md).
