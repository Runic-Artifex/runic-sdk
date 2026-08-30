# 01 — Hello lifecycle

Build the smallest useful Runic Application: select one host, run it, and shut
it down in order.

## Prerequisites

Install .NET SDK 10.0.302, as pinned by the repository's `global.json`. This
sample uses `Runic.Application`.

## Run and verify

From the repository root:

```bash
dotnet run --project samples/01-HelloLifecycle
```

Expected output includes:

```text
Preparing the workspace...
Hello from Runic Application!
Running runic-examples-hello-lifecycle.
Closing the workspace...
```

The command is the smoke check: it exits with code 0 after printing the final
application state.

## What to inspect

`Program.cs` declares the generated application manifest, selects one
`IApplicationHost`, then calls `Build()` and `RunAsync()`. The host makes the
start, wait, stop, and disposal ordering explicit.

## Next

Continue to [02 — Typed greeting command](../02-GreetingCommandLine/) to add a
typed command catalog and machine-readable output.

[Hosting documentation](https://docs.runic-artifex.eu/products/runic-toolkit) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/01-HelloLifecycle) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
