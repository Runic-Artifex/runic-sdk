![Runic Command Line banner](.github/assets/brand/banner.png)

# Runic Command Line

Build command applications that keep their behavior consistent when you change a
parser, host, or delivery target. Runic Command Line gives .NET applications an
explicit command catalog, closed typed execution, and predictable human or JSON
output—without a UI framework, reflection activation, or a parser dependency in
the public model.

## Start here

Runic Command Line targets **.NET 10 (`net10.0`)**. Install the command kernel:

```bash
dotnet add package Runic.CommandLine --prerelease
```

The packages are currently preview releases. The `runic.commandline/1` JSON
protocol and `RCLI####` diagnostic identities are deliberate compatibility
surfaces; as with any pre-1.0 dependency, test upgrades against your application.
The architecture is designed and smoke-tested for NativeAOT. Keep application
result serialization source-generated through `ICommandResultCodec<T>` and audit
your own dependencies for NativeAOT compatibility.

## Choose a package

| Package | Install | Choose it when… |
| --- | --- | --- |
| [Runic.CommandLine](https://www.nuget.org/packages/Runic.CommandLine) | `dotnet add package Runic.CommandLine --prerelease` | You need the contracts, catalog, portable parsing, host launch classification, typed execution, and human/JSON output. |
| [Runic.CommandLine.Processes](https://www.nuget.org/packages/Runic.CommandLine.Processes) | `dotnet add package Runic.CommandLine.Processes --prerelease` | A command safely runs bounded local child processes. |
| [Runic.CommandLine.Testing](https://www.nuget.org/packages/Runic.CommandLine.Testing) | `dotnet add package Runic.CommandLine.Testing --prerelease` | You need deterministic in-memory helpers for command tests. |

`Runic.CommandLine` is the single runtime package. Portable contracts and host
launch classification live in its `Runic.CommandLine` and
`Runic.CommandLine.Hosting` namespaces without separate compatibility packages.

`Runic.CommandLine.Testing` supplies deterministic in-memory console, scope,
captured-environment, cancellation, and response-envelope helpers for tests.

## A minimal command application

## Method-first generated commands

`Runic.CommandLine` includes its generator as an analyzer asset. Attribute a
non-private static method, then call
`Runic.CommandLine.Generated.GeneratedCommandCatalog.Create()` to obtain
a catalog with closed binders, handler factories, and source-generated JSON
metadata. It supports `string`, `int`, `long`, `decimal`, `double`, `Guid`, and
Boolean flag inputs; decimal conversion is invariant. `[FromServices]` makes a
service dependency explicit, while `CancellationToken`,
`CommandExecutionContext`, and `ICommandConsole` are injected by the runtime.
`[CommandResult]` also explicitly identifies the application-owned
source-generated `JsonSerializerContext` used for machine output.

`[DefaultCommand]` selects one generated command for positional invocation
without a leading command name. Scalar option defaults and repeated
`IReadOnlyList<string>` options are supported. One trailing
`[Argument(AllowMultipleValues = true)] IReadOnlyList<string>` receives all
remaining positional tokens, including application options after `--`. The remaining v0.2 subset
excludes instance methods, subcommands, optional positional values, repeated
non-string values, custom conversion, and custom generated human formatting.
Use `[Option("--source", Required = true)]` for parser-owned presence checks;
missing required options report the canonical spelling and command path as safe
diagnostic arguments.
`CommandParsePresentation` lets an application map parser diagnostics to its own
human error text and 2/3/4/5-style exit policy without reparsing.

The following complete program registers `hello`, parses its arguments, executes
one typed handler, and writes either human text or one JSON response frame. It
uses the portable adapter; an application can instead supply its own
`ICommandSyntaxAdapter` while retaining the same catalog and execution model.

```csharp
using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
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

    if (parse.Kind != ParseOutcomeKind.Invocation || parse.Invocation is null)
    {
        Console.Error.WriteLine("Use: hello");
        return CommandExitCodes.Usage;
    }

    var request = new CommandExecutionRequest(
        parse.Invocation,
        new SystemConsole(),
        CultureInfo.InvariantCulture,
        "demo-1");
    CommandExecutionResult result = await new CommandExecutor(new AppScopeFactory())
        .ExecuteAsync(request, new CommandOutputDispatcher());
    return result.ExitCode;
}

sealed class HelloOptions;
sealed record Greeting(string Message);

sealed class HelloBinder : ICommandOptionsBinder<HelloOptions>
{
    public static HelloBinder Instance { get; } = new();

    public ValueTask<CommandOutcome<HelloOptions>> BindAsync(
        ParsedInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(CommandOutcome.Success(new HelloOptions()));
}

sealed class HelloHandlerFactory : ICommandHandlerFactory<HelloHandler>
{
    public static HelloHandlerFactory Instance { get; } = new();

    public HelloHandler Create(IServiceProvider services) => new();
}

sealed class HelloHandler : ICommandHandler<HelloOptions, Greeting>
{
    public ValueTask<CommandOutcome<Greeting>> ExecuteAsync(
        HelloOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CommandOutcome.Success(new Greeting("Hello, world!")));
}

sealed class GreetingCodec : ICommandResultCodec<Greeting>
{
    public static GreetingCodec Instance { get; } = new();

    public string PayloadType => "example.greeting/1";
    public JsonTypeInfo<Greeting> TypeInfo => DemoJsonContext.Default.Greeting;

    public ValueTask WriteHumanAsync(
        Greeting value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken) =>
        console.WriteOutAsync($"{value.Message}\n".AsMemory(), cancellationToken);
}

[JsonSerializable(typeof(Greeting))]
internal partial class DemoJsonContext : JsonSerializerContext;

sealed class AppScopeFactory : ICommandExecutionScopeFactory
{
    public ICommandExecutionScope CreateScope() => new AppScope();

    private sealed class AppScope : ICommandExecutionScope
    {
        public IServiceProvider Services { get; } = EmptyServices.Instance;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}

sealed class SystemConsole : ICommandConsole
{
    public bool IsInputRedirected => Console.IsInputRedirected;
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    public bool IsErrorRedirected => Console.IsErrorRedirected;
    public bool IsInteractive => !IsInputRedirected && !IsOutputRedirected;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Console.ReadLine());
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        new(Console.Out.WriteAsync(value, cancellationToken));
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        new(Console.Error.WriteAsync(value, cancellationToken));
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        Console.OpenStandardOutput().WriteAsync(value, cancellationToken);
}
```

Run it in either output mode:

```bash
dotnet run -- hello
RUNIC_COMMANDLINE_OUTPUT=json dotnet run -- hello
```

Human output is `Hello, world!`. JSON output is one UTF-8, LF-terminated
`runic.commandline/1` object (shown without its final newline):

```json
{"protocol":"runic.commandline/1","requestId":"demo-1","command":"hello","success":true,"exitCode":0,"payloadType":"example.greeting/1","payload":{"Message":"Hello, world!"},"fault":null,"diagnostics":[]}
```

## What stays portable

The catalog defines names, aliases, arity, and stable parameter IDs independently
of a parser. Binders convert the parser-neutral `ParsedInvocation`; handlers and
result codecs are closed generic registrations, so execution does not scan
assemblies, activate types by reflection, or serialize arbitrary objects. Result
codecs provide source-generated `JsonTypeInfo` for JSON output.

`RUNIC_COMMANDLINE_OUTPUT` selects `human` or `json`. An explicit command
argument wins over the captured environment value, which wins over the caller
default. JSON stdout is deliberately limited to the response frame; send logs and
progress elsewhere.

The framework output option defaults to `--output`, but captured
`ParseSettings` or `HostedCommandLineLaunchInput` can choose another long
spelling such as `--runic-output` when the application owns `--output`.

Host launch classification is included in `Runic.CommandLine`. For local child
processes, add `Runic.CommandLine.Processes`; it intentionally requires an
executable policy and is not a sandbox.

## Documentation and support

Read the [Runic Command Line documentation](https://docs.runic-artifex.eu/products/runic-command-line/),
explore the [NativeAOT smoke example](https://github.com/Runic-Artifex/runic-command-line/tree/main/tests/Runic.CommandLine.AotSmoke),
or [report an issue](https://github.com/Runic-Artifex/runic-command-line/issues).
Runic Command Line is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-command-line/blob/main/LICENSE).
