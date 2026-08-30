# 02 — Typed greeting command

Turn command-line input into a typed request, validate it once, and render the
same result for people or JSON consumers.

## Prerequisites

Install .NET SDK 10.0.302. The project uses the released preview
`Runic.CommandLine` and `Runic.CommandLine.Abstractions` packages.

## Run and verify

From the repository root:

```bash
dotnet run --project samples/02-GreetingCommandLine
dotnet run --project samples/02-GreetingCommandLine -- greet Ada --times 2
dotnet run --project samples/02-GreetingCommandLine -- greet Ada --output json
dotnet run --project samples/02-GreetingCommandLine -- --help
```

The first command defaults to `greet World`. The second prints `Hello, Ada!`
twice, and the JSON command returns a typed greeting payload. A value outside
`--times 1` through `--times 5` returns a validation error and usage text.

Use this as the headless smoke command:

```bash
dotnet run --project samples/02-GreetingCommandLine -- greet Runic
```

It exits with code 0 and prints `Hello, Runic!`.

## What to inspect

`GreetingCommand.cs` declares the catalog, typed binder, handler, and result
codec. `Program.cs` parses once and hands the invocation to `CommandExecutor`.
The handler stays independent of console and JSON presentation.

## Next

Continue to the [React Setup Application](../03-SetupApplication/) to use a
validated contract between a web frontend and a Runic Desktop host.

[Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line) ·
[Examples](https://github.com/Runic-Artifex/runic-toolkit-examples/tree/main/samples/02-GreetingCommandLine) ·
[Issues](https://github.com/Runic-Artifex/runic-toolkit-examples/issues)
