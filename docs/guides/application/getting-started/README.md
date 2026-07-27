# Getting started

This guide runs the repository itself. Package templates and a published tool
installation are roadmap work, so repository-local commands are shown first.

## Enter the pinned environment

On NixOS, the checked-in flake and direnv configuration provide .NET 10, Node,
Chromium, the native CsWebUi library, and the Linux WebView dependencies:

```bash
direnv allow
npm ci
dotnet build WebUIToolkit.slnx
```

Run commands from the repository root after direnv has loaded. See
[development modes](../contributing/development.md) for the difference between
the ordinary inner loop and release verification.

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

Until the tool is published or installed into a local tool manifest, run it
directly from its project:

```bash
dotnet run --project tools/dotnet-webuitoolkit -- \
  dev samples/SimpleTodo/SimpleTodo.csproj
```

An installed tool provides the shorter equivalent:

```bash
dotnet webuitoolkit dev samples/SimpleTodo/SimpleTodo.csproj
```

The command coordinates the initial .NET and Vite builds, CsWebUi, generated
contracts, `dotnet watch`, diagnostics, and shutdown. In a native window:

- CSS and JavaScript use Vite HMR;
- compatible `.cwhtml` renderer edits use .NET Hot Reload and refresh only the
  affected fragment over the private CsWebUi HTMX binding;
- compiler errors use Vite's browser overlay; and
- incompatible generated changes restart the native host safely.

Vite serves development assets only. Application actions do not become HTTP
endpoints.

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

