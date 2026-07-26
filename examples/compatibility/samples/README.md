# Samples

These projects are an ordered learning path. They reference repository source
projects so editing the toolkit and rerunning a demo is immediate.

| Level | Project | What it demonstrates |
| --- | --- | --- |
| 1 | `01-HelloLifecycle` | Application startup, mode selection, shutdown, and Generic Host composition |
| 2 | `02-GreetingCommandLine` | Typed command catalog, parsing, execution, and human/JSON output |
| 3 | `03-MvvmCounterProjection` | CommunityToolkit.MVVM projected through the revisioned WebUIToolkit protocol |
| 4 | `04-NativeMvvmCounter` | The production binary CsWebUi FrameChannel driving the framework-neutral browser client |
| 5 | `SimpleTodo` | Compiled C#/HTMX, one native transport, validation, commands, collections, local assets, and a real-browser gate |
| 6 | `AdvancedTodo` | The same compiled native path expanded with persistence, filtering, workflows, cancellation, and diagnostics |
| 7 | `Todo.React` | Both Todo levels through React hooks and the native binary MVVM channel |
| 7 | `Todo.Vue` | Both Todo levels through Vue computed refs and the same C# ViewModels |
| 7 | `Todo.Svelte` | Both Todo levels through a Svelte readable store |
| 7 | `Todo.Angular` | Both Todo levels through Angular signals |

Start anywhere:

```bash
dotnet run --project samples/01-HelloLifecycle
dotnet run --project samples/02-GreetingCommandLine -- greet Ada
dotnet run --project samples/03-MvvmCounterProjection
dotnet run --project samples/04-NativeMvvmCounter
dotnet run --project samples/SimpleTodo
dotnet run --project samples/AdvancedTodo
dotnet run --project samples/Todo.React
dotnet run --project samples/Todo.React -- --advanced
```

Replace `React` with `Vue`, `Svelte`, or `Angular` to inspect the other
framework adapters. The framework projects share
[`Todo.Shared`](./Todo.Shared) with the cwhtml demos and share one native host
and projection map in [`Todo.FrontendHost`](./Todo.FrontendHost). See the
[framework sample guide](./Todo.Frontends/README.md) and recorded
[DX findings](../docs/frontend-todo-findings.md).

The two desktop applications follow the same progressive-teaching idea as
Avalonia's [Simple ToDo](https://github.com/AvaloniaUI/Avalonia.Samples/tree/main/src/Avalonia.Samples/CompleteApps/SimpleToDoList)
and [Advanced ToDo](https://github.com/AvaloniaUI/Avalonia.Samples/tree/main/src/Avalonia.Samples/CompleteApps/AdvancedToDoList)
examples, but are original implementations of WebUIToolkit's own architecture.
The original two now render compiled `.cwhtml` documents and fragments and submit through
one bounded CsWebUi/HTMX binding. Their pinned HTML, CSS, and browser scripts
are served by CsWebUi; neither application starts ASP.NET Core or exposes
manually named browser callbacks.

SimpleTodo is the completed golden-path acceptance sample. In addition to its
managed smoke test, `--browser-smoke-test` launches the real CsWebUi server and
Nix-pinned Chromium, submits the visible form through the shipped HTMX bridge,
executes C#, and verifies that the compiled fragment replaced the DOM.
AdvancedTodo's compiled conversion is implemented and its `--self-test`
exercises application behavior, but it is not yet included in the native
Chromium/Native-AOT acceptance gate.

Bootstrap 5.3 and Font Awesome are the default visual baseline for desktop
samples. Assets must be pinned and served locally. Samples should prefer
Bootstrap's established layout, form, validation, navigation, modal, toast,
and accessibility patterns before adding custom CSS. Font Awesome icons must
retain accessible text or labels where the icon carries meaning.

This is a sample and migration convention, not a toolkit restriction. Core
packages remain styling-neutral, and their extension points must support
shadcn, Tailwind, raw CSS, or any consumer-owned design system.

On NixOS, run them inside the repository's direnv environment. The flake supplies
the pinned .NET SDK, native WebUI library, Chromium, and Linux WebView libraries.
SimpleTodo provides managed and real-browser smoke modes; AdvancedTodo provides
a deterministic application self-test without opening a window.

The former projects in this directory were release and acceptance harnesses.
They now live under [`tests/Fixtures`](../tests/Fixtures) and retain their
deterministic verification role.
