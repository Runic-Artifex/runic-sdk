# Samples

These projects are an ordered learning path. They consume exact published
Runic Toolkit, Command Line, and Flow packages without referencing any
product source checkout.

| Level | Project | What it demonstrates |
| --- | --- | --- |
| 1 | `01-HelloLifecycle` | Application startup, mode selection, shutdown, and Generic Host composition |
| 2 | `02-GreetingCommandLine` | Typed command catalog, parsing, execution, and human/JSON output |
| 3 | `03-MvvmCounterProjection` | CommunityToolkit.MVVM projected through the revisioned Runic Toolkit protocol |
| 4 | `04-NativeMvvmCounter` | The production binary CsWebUi FrameChannel driving the framework-neutral browser client |
| 5 | `Todo.React` | Both Todo levels through React hooks and the native binary channel |
| 5 | `Todo.Vue` | Both Todo levels through Vue computed refs and the same C# application models |
| 5 | `Todo.Svelte` | Both Todo levels through a Svelte readable store |
| 5 | `Todo.Angular` | Both Todo levels through Angular signals |

Start anywhere:

```bash
dotnet run --project samples/01-HelloLifecycle
dotnet run --project samples/02-GreetingCommandLine -- greet Ada
dotnet run --project samples/03-MvvmCounterProjection
dotnet run --project samples/04-NativeMvvmCounter
dotnet run --project samples/Todo.React
dotnet run --project samples/Todo.React -- --advanced
```

Replace `React` with `Vue`, `Svelte`, or `Angular` to inspect the other
framework adapters. The framework projects share [`Todo.Shared`](./Todo.Shared)
and one native host and projection map in
[`Todo.FrontendHost`](./Todo.FrontendHost). See the
[framework sample guide](./Todo.Frontends/README.md).

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

Package-only canaries under [`../integrations`](../integrations) retain the
deterministic verification role.
