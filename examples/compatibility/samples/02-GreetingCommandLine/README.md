# 02 — Typed greeting command

**Difficulty:** Beginner / intermediate

This sample builds a small command-line application with a typed catalog,
options binder, handler, and separate human/JSON presentation. It uses project
references, so editing the command-line libraries and rerunning the sample is
an immediate inner loop.

## Run it

The no-argument form greets `World`:

```console
dotnet run --project samples/02-GreetingCommandLine
```

Then try real command-line input:

```console
dotnet run --project samples/02-GreetingCommandLine -- greet Ada --times 2
dotnet run --project samples/02-GreetingCommandLine -- greet Ada --output json
dotnet run --project samples/02-GreetingCommandLine -- --help
```

## Guided code tour

1. `GreetingCommand.cs` declares an immutable command catalog. Names, aliases,
   arity, binding, execution, and result presentation are explicit.
2. `GreetingOptionsBinder` converts parser-neutral strings into a typed
   `GreetingOptions` value and returns a friendly validation fault for bad
   input.
3. `GreetingHandler` contains only application behavior. It does not know
   whether its result will be rendered for a person or a machine.
4. `GreetingResultCodec` writes human output and supplies source-generated JSON
   metadata for `--output json`.
5. `Program.cs` parses once, then hands the typed invocation to
   `CommandExecutor`. `CommandInfrastructure.cs` adapts the system console and
   supplies a minimal per-invocation scope.

## Try next

Add a `--greeting` option and carry it from `GreetingOptionsBinder` through the
handler into both output formats.
