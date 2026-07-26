# Samples

These projects are an ordered learning path. They reference repository source
projects so editing the toolkit and rerunning a demo is immediate.

| Level | Project | What it demonstrates |
| --- | --- | --- |
| 1 | `01-HelloLifecycle` | Application startup, mode selection, shutdown, and Generic Host composition |
| 2 | `02-GreetingCommandLine` | Typed command catalog, parsing, execution, and human/JSON output |
| 3 | `03-MvvmCounterProjection` | CommunityToolkit.MVVM projected through the revisioned WebUIToolkit protocol |
| 4 | `SimpleTodo` | A complete CsWebUi desktop window with local assets, validation, commands, and observable state |
| 5 | `AdvancedTodo` | A larger CsWebUi application with persistence, filtering, editing, async work, and diagnostics |

Start anywhere:

```bash
dotnet run --project samples/01-HelloLifecycle
dotnet run --project samples/02-GreetingCommandLine -- greet Ada
dotnet run --project samples/03-MvvmCounterProjection
dotnet run --project samples/SimpleTodo
dotnet run --project samples/AdvancedTodo
```

The two desktop applications follow the same progressive-teaching idea as
Avalonia's [Simple ToDo](https://github.com/AvaloniaUI/Avalonia.Samples/tree/main/src/Avalonia.Samples/CompleteApps/SimpleToDoList)
and [Advanced ToDo](https://github.com/AvaloniaUI/Avalonia.Samples/tree/main/src/Avalonia.Samples/CompleteApps/AdvancedToDoList)
examples, but are original implementations of WebUIToolkit's own architecture.
Their UI is local HTML/CSS/JavaScript hosted by CsWebUi. They do not start an
ASP.NET Core server.

On NixOS, run them inside the repository's direnv environment. The flake supplies
the pinned .NET SDK, native WebUI library, Chromium, and Linux WebView libraries.
Both ToDo applications also provide a documented headless smoke mode for checking
their C# state and command paths without opening a window.

The former projects in this directory were release and acceptance harnesses.
They now live under [`tests/Fixtures`](../tests/Fixtures) and retain their
deterministic verification role.
