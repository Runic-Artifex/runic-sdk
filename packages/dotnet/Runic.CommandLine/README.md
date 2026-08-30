# Runic.CommandLine

`Runic.CommandLine` is the command kernel for .NET 10 applications that need
portable command behavior and predictable output. Define an immutable catalog,
bind parsed values into typed options, execute a closed handler, and emit human
text or a machine-readable response without coupling the public model to a
parser, Generic Host, or UI framework.

## Install

```bash
dotnet add package Runic.CommandLine --prerelease
```

The package targets `net10.0` and includes its portable contracts and host
launch classification. It is a preview package: test updates before adopting
them in a production command contract.

## Register and run a command

## Method-first commands

The package includes its incremental generator as an analyzer asset. Mark a
non-private static method and create the generated catalog; no assembly scan,
reflection activation, or handwritten binder is involved. Supported input types
are `string`, `int`, `long`, `decimal`, `double`, `Guid`, and Boolean flags.
Decimals always use invariant culture. Services must be explicit with
`[FromServices]`; cancellation, `CommandExecutionContext`, and
`ICommandConsole` are supplied by the invocation.

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Generated;

[Command("greet")]
[CommandResult("example.greeting/1", typeof(AppJsonContext))]
internal static Greeting Greet([Argument] string name, [Option("--formal")] bool formal) =>
    new(formal ? $"Good day, {name}." : $"Hello, {name}!");

CommandCatalog catalog = GeneratedCommandCatalog.Create();

[JsonSerializable(typeof(Greeting))]
internal partial class AppJsonContext : JsonSerializerContext;
```

Generated registrations compose at the catalog boundary: use the existing
explicit builder API for commands needing custom binders, result presentation,
or parser-specific integration, and combine its root registrations before
building your application catalog. The generator reports duplicate command
names, invalid method signatures, and unsupported parameter types at build
time. Mark one method with `[DefaultCommand]` when it receives positional input
without a command token. Scalar options may use C# defaults, and repeated
`IReadOnlyList<string>` options preserve encounter order. A trailing
`[Argument(AllowMultipleValues = true)] IReadOnlyList<string>` receives all
remaining positional tokens in order, including option-looking values after
`--`. The remaining subset
excludes instance methods, subcommands, optional positional values, repeated
non-string values, custom conversion, and custom human result formatting.

Set `[Option("--source", Required = true)]` when syntax, rather than binding,
owns presence validation. Missing required options yield the parser-owned
`RCLI1012` diagnostic with the canonical option spelling and command path;
required flags must be present and required repeated options need an occurrence.

`CommandParsePresentation` maps parser diagnostics to application-owned human
text and exit codes without reparsing. Handlers retain `CommandOutcome<T>` and
the application exit policy for domain codes such as 2/3/4/5.

For an application-owned `--output` option, choose a different framework
transport option in the captured settings, for example
`new ParseSettings(transportOutputOptionName: "--runic-output")`. The parser
then reserves `--runic-output human|json` and leaves `--output` to the command.
Built-in `--help` and `--version` cannot be used as transport spellings.
The reserved transport is scanned before command syntax is reported (up to
`--`), so parse errors retain the selected output classification regardless of
where a valid transport value occurs. Use `parse.OutputClassification` to choose
human or JSON presentation without re-reading captured arguments. Invalid,
missing, and duplicate transport values retain only safe parser diagnostics.
Help and root-version outcomes use that same classification, whether the
transport appears before or after the special token.
The root-only legacy token `help` is equivalent to `--help`; `help` remains a
reserved catalog command name. It accepts only framework transport options;
other following tokens yield the safe parser-owned `RCLI1013` diagnostic.
Generated `IReadOnlyList<string>` options use one value per occurrence by
default; add `[Option("--documents", AllowMultipleValues = true)]` to accept
`--documents a b` as well as repeated occurrences. Values end at the next
option and remain in encounter order.
Set `AllowMultipleOccurrences = false` to keep variadic values for a single
occurrence while rejecting a second occurrence with the canonical option
diagnostic.
For application forwarding, use one trailing
`[Argument(AllowMultipleValues = true)] IReadOnlyList<string> appArgs`; this
preserves `dotnet runic dev -- --application-option -3` without a second parser.

## Register and run a command

Register a command with its binder, handler factory, and source-generated result
codec. Parse captured arguments, then pass a successful invocation to the
executor and `CommandOutputDispatcher`.

```csharp
CommandCatalog catalog = new CommandCatalogBuilder()
    .Command<HelloOptions, HelloHandler, Greeting>("hello", command => command
        .Describe("command.hello")
        .BindWith(HelloBinder.Instance)
        .CreateHandlerWith(HelloHandlerFactory.Instance)
        .Produces(GreetingCodec.Instance))
    .Build();

ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
    catalog,
    args,
    new ParseSettings(Environment.GetEnvironmentVariable(
        CommandOutputClassifier.EnvironmentVariableName)));

if (parse.Kind == ParseOutcomeKind.Invocation && parse.Invocation is not null)
{
    var request = new CommandExecutionRequest(
        parse.Invocation, console, CultureInfo.InvariantCulture, "request-42");
    CommandExecutionResult result = await executor.ExecuteAsync(
        request, new CommandOutputDispatcher(), cancellationToken);
    return result.ExitCode;
}
```

`console` is your `ICommandConsole` implementation and `executor` is a
`CommandExecutor` configured with your `ICommandExecutionScopeFactory`. See the
[complete runnable example](https://github.com/Runic-Artifex/runic-command-line/tree/main/tests/Runic.CommandLine.AotSmoke)
for implementations of the binder, handler, source-generated codec, scope, and
console.

Set `RUNIC_COMMANDLINE_OUTPUT=json` to write a single UTF-8 JSON response frame
to stdout; the default is human output. The portable adapter also recognizes an
explicit `--output human` or `--output json` value, which takes precedence over
the captured environment value.

## When to use it

Choose this package for the command model, host launch classification, and
execution pipeline. Add
[`Runic.CommandLine.Processes`](https://www.nuget.org/packages/Runic.CommandLine.Processes)
The portable contracts remain directly available from this package when your
own integration exposes or implements them.

Catalog validation reports invalid names, duplicate spellings, invalid arity,
and incomplete registrations together in deterministic definition order.
Execution creates and disposes exactly one scope for each valid invocation; a
success is the only semantic outcome that maps to exit code zero.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
see [examples](https://github.com/Runic-Artifex/runic-command-line/tree/main/tests),
or [report an issue](https://github.com/Runic-Artifex/runic-command-line/issues).
Runic.CommandLine is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-command-line/blob/main/LICENSE).
