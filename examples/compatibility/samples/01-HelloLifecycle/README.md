# 01 — Hello lifecycle

Build the smallest useful Runic Toolkit application: prepare application
services, run a selected UI mode, and shut everything down in order.

## Prerequisites

Install .NET SDK 10.0.302, as pinned by the repository's `global.json`. This
sample uses `RunicToolkit.Hosting` and `RunicToolkit.Hosting.GenericHost`.

## Run and verify

From the repository root:

```bash
dotnet run --project samples/01-HelloLifecycle
```

Expected output includes:

```text
Preparing the workspace...
Hello from Runic Toolkit!
The launcher selected the UserInterface mode.
Closing the workspace...
```

The command is the smoke check: it exits with code 0 after printing the final
application state.

## What to inspect

`Program.cs` creates `GenericHostRunicToolkitApplicationBuilder`, adds a
startup participant, adds a `LaunchKind.UserInterface` mode runner, then calls
`Build()` and `RunAsync()`. The participant's `Infrastructure` phase makes the
start/stop ordering explicit.

## Next

Continue to [02 — Typed greeting command](../02-GreetingCommandLine/) to add a
typed command catalog and machine-readable output.

[Hosting documentation](https://docs.runic-artifex.eu/products/runic-toolkit) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/01-HelloLifecycle) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
