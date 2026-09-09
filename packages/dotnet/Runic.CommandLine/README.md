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

## A complete command-line application

The package includes its incremental generator. Define a static method and run the
catalog; no custom options binder, result DTO, JSON context, service provider or
Generic Host is needed for this example:

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Generated;

return await new CommandApp(GeneratedCommandCatalog.Create())
{
    Name = "hello",
    Version = "1.0.0",
}.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello.", Examples = ["hello greet Ada"])]
    [DefaultCommand]
    internal static string Greet([Argument] string name = "world") => $"Hello, {name}!";
}
```

`hello`, `hello Ada`, and `hello greet Ada` execute the default command.
`hello help greet` and `hello greet --help` show catalog help. `hello --output json
greet Ada` emits one versioned response. An application without a default command
shows help for an empty invocation; the lower-level parser retains its existing
empty-input classification for hosted UI launch decisions.

See the runnable [command-line example](../../../examples/command-line/README.md).

## Method-first inputs and results

- Scalars: strings, integers, long integers, decimals, doubles, GUIDs, enums and
  nullable scalars. Boolean options are presence flags; nullable Boolean options
  accept an explicit Boolean value.
- C# defaults and nullable inputs are optional. A non-nullable scalar option with
  no default is required. `Required = true` explicitly requires flags and lists.
- Arrays and `IReadOnlyList<T>` support the scalar types above. Options append one
  value per occurrence; `AllowMultipleValues = true` also accepts several values
  after one occurrence. A variadic positional must be last.
- `[Option(..., Description = "...", ValueName = "PATH", Choices = ["a", "b"],
  EnvironmentVariable = "APP_VALUE", Sensitive = true)]` supplies shared metadata.
  Explicit input wins over the environment, then C# defaults. Sensitive defaults
  and choices are excluded from help and completion.
- Generated numeric metadata permits separated negative numbers. Other
  option-looking data still requires `--` or an equals value. Unknown options and
  duplicate scalar options remain errors.
- `[Command("config show")]` creates a help-only `config` group automatically.
  `GeneratedCommandCatalog.Create(builder => builder.GlobalOption("verbose",
  "--verbose", CommandArity.Zero))` adds an option to all commands; it may precede
  the command. Read shared bindings from `ParsedInvocation.Options` in a binder.
- `[ConvertWith(typeof(Converter))]` selects an `ICommandValueConverter<T>` with
  static `Parse(string)`. `[ValidateWith(typeof(Validator))]` selects an
  `ICommandValueValidator<T>` with static `IsValid(T)`. Converted values are stored
  once during binding and reused by the handler.
- `string`, `void`, `Task` and `ValueTask` results have built-in codecs. Typed
  application payloads retain `[CommandResult("example.result/1", typeof(JsonContext))]`
  and source-generated JSON metadata. An `int` result is payload data, never an
  implicit exit code. Use `CommandOutcome<T>` and an exit policy for domain exits.

CancellationToken, CommandExecutionContext and ICommandConsole parameters are
injected automatically; other services use `[FromServices]`. Instance methods and
runtime assembly discovery are intentionally outside the generated model.

## Presentation and testing

`CommandApp` owns parsing, output environment selection, help, version, Ctrl+C,
execution and exit mapping. Configure `ScopeFactory`, `ExitCodePolicy`,
`OutcomeSink`, or `PresentFrameworkRequest` to preserve existing application
contracts during migration. Set `HandleCancelKeyPress = false` when an embedding
host already owns process signals.

Install optional `Runic.CommandLine.Spectre`, then set `Console = new
SpectreCommandConsole()` and `HelpPresenter = new SpectreHelpPresenter()`.
The adapter supports literal text, Spectre renderables, progress, and prompts.
ANSI is disabled for redirected output, `NO_COLOR`, and `TERM=dumb`.
For handler presentation, wrap the **injected** console so the current invocation's
capabilities and stream routing are retained.

JSON output reserves stdout for the envelope. The injected handler console routes
incidental output to stderr and disables reads. Direct process-global Console
writes remain the application's responsibility. `ExceptionObserver` receives
internal failures for application logging while public faults remain sanitized.

`completion bash|zsh|fish|powershell` generates static word-list completions from
the catalog. Set `CompletionExecutableName` when the help-facing name contains
spaces (for example, `dotnet runic`). These scripts offer declared commands,
options and simple choices; they do not perform dynamic filesystem or service
lookups and do not restrict candidates to the current subcommand.

`Runic.CommandLine.Testing` supplies `CommandAppTester`, a configurable
`TestCommandConsole` with queued input, and strict JSON frame assertions. Supply
explicit ParseSettings to keep environment-dependent tests deterministic.

### Migrating from 0.2

Existing explicit catalogs, payload identities and exit policies continue to work.
Replace duplicated startup with `CommandApp` progressively. Retain domain outcome
sinks and custom framework presenters when scripts depend on an existing envelope.

Generated nullable inputs now bind absence as null, optional positional defaults
are honored, and required non-nullable scalar options fail during parsing. Binding
errors identify the parameter and expected type without echoing its value. `help
<command-path>`, prefix output selection, and empty default-command invocations
are now accepted. The version-1 machine envelope has not changed.

For applications with their own `--output` option, keep
`new ParseSettings(transportOutputOptionName: "--runic-output")`. Set its
`GetEnvironmentVariable` callback if that application also declares parameter
environment fallbacks. `ParseSettings` itself never reads the process environment.

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
[complete runnable example](https://github.com/Runic-Artifex/runic-sdk/tree/main/tests/native/Runic.CommandLine.AotSmoke)
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
see [examples](https://github.com/Runic-Artifex/runic-sdk/tree/main/tests/dotnet/Runic.CommandLine.Tests),
or [report an issue](https://github.com/Runic-Artifex/runic-sdk/issues).
Runic.CommandLine is maintained by Runic Artifex and licensed under the
[MIT License](https://github.com/Runic-Artifex/runic-sdk/blob/main/LICENSE).

## Commands inside a Runic application

Use `CommandLineHostingAdapter` when the application already owns startup,
services, cancellation and the choice between CLI and UI. It uses the same
catalog, generated binders, executor and framework presentation as `CommandApp`.
It does not start a Generic Host, subscribe to Ctrl+C, or dispose your application.

```csharp
using Runic.CommandLine;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;

// applicationCommandScopes supplies the application's services to command handlers.
var cli = new CommandLineHostingAdapter(catalog, new CommandExecutor(applicationCommandScopes))
{
    Presentation = new()
    {
        Name = "my-app",
        Version = version,
        HelpPresenter = new SpectreHelpPresenter(),
        ExceptionObserver = RecordInternalException,
    },
};
var console = new SpectreCommandConsole();
var launch = new HostedCommandLineLaunchInput(args,
    outputEnvironmentValue: Environment.GetEnvironmentVariable("RUNIC_COMMANDLINE_OUTPUT"),
    emptyInputFallback: EmptyInputFallback.UserInterface)
{
    EnvironmentVariables = new Dictionary<string, string?>
    {
        ["MY_APP_ENV"] = Environment.GetEnvironmentVariable("MY_APP_ENV"),
    },
};
var decision = cli.Classify(launch);
if (decision.Kind == HostedCommandLineDecisionKind.UserInterface)
    return await OpenApplicationUiAsync(applicationStopping);
if (!decision.CanExecute)
    return await cli.PresentAsync(decision, console, culture, correlationId, applicationStopping);
return (await cli.ExecuteAsync(new(decision, console, culture, correlationId, new CommandOutputDispatcher())
{
    ExceptionObserver = RecordInternalException,
}, applicationStopping)).ExitCode;
```

The application captures only the environment values its options declare.
`EnvironmentVariables` copies that dictionary; missing keys never fall back to the
process environment. Explicit arguments override captured values, which override
handler defaults. Output-format selection continues to use the separate captured
`outputEnvironmentValue`. Names in the parameter snapshot are matched exactly.

`Classify` creates no service scope. `PresentAsync` handles scoped help, version,
usage failures and `completion bash|zsh|fish|powershell`, also without a scope.
Help and errors respect human/JSON output selection; completion intentionally
writes the raw script. Completion remains a static word list. Existing hosts
may keep their own presenters and use the decision's public diagnostics instead.
`PresentAsync` accepts framework decisions created by that same adapter and
rejects UI and invocation decisions. It is available on the concrete adapter;
the existing `IHostedCommandLineAdapter` classify/execute contract is unchanged.

`ExecuteAsync` owns only the invocation scope returned by your scope factory.
Use `[FromServices]` for handler dependencies. Pass your host's cancellation token
and set `ExceptionObserver` to your internal logging callback; exception details
stay out of public faults. If you customize exit codes, supply the same policy to
`CommandExecutor` and `Presentation.ExitCodePolicy`. An explicit empty-input UI
policy wins even when the catalog declares a default command.

See the runnable [hosted example](../../../examples/command-line/HostedExample.cs)
for service injection and the complete launch flow. The example's UI branch is a
console placeholder for an application's existing UI launcher.

### Framework presentation failures and command ownership

Help, version, usage-error and completion presentation honor cancellation in both
runners. Cancellation returns the configured `Cancelled` exit code. Other nonfatal
presentation failures return the configured `HostFailure` exit code and notify
`CommandApp.ExceptionObserver` or, for hosted presentation,
`CommandPresentation.ExceptionObserver`. Execution continues to use the observer
on `HostedCommandLineExecutionInput`. Observer failures do not replace the original
exit result. Fatal runtime failures propagate.

A presenter or output stream may already have written partial output when it
fails. The framework therefore does not retry output or append a second error
frame; use the exit code and your internal observer to detect these failures.
Argument/decision ownership errors in `PresentAsync` remain API usage exceptions.

A catalog-owned root command or alias named `completion` takes precedence over the
built-in script command. This works for manual and generated catalogs. Built-in
scripts use the configured transport selector (for example `--runic-output`).
With a custom selector, they retain `--output` only if it is an actual catalog
option. Direct callers can use
`CommandCompletion.Generate(catalog, executable, shell, outputOptionName)`.

When a global option reuses a command-local definition, its name, arity and alias
set must match. Alias ordering is irrelevant; mismatches fail catalog validation
with `RCLI0019`, rather than producing command-dependent parsing behavior.

## Discovery, validation and custom results

`Hidden = true` on `[Command]`, `[Option]` or `[Argument]` omits that entry from
help listings and completion. Hidden commands/options still parse when explicitly
specified; this is discoverability metadata, not access control. A command's
`LongDescription` adds extended help below its summary. Close command and option
typos receive suggestions from visible catalog spellings only. Suggestions never
execute corrections and never include option values.

`FileInfo` and `DirectoryInfo` parameters bind directly and infer file/directory
completion metadata. Strings can opt in with `PathKind = CommandPathKind.File`
or `.Directory`. `MustExist = true` checks the requested path kind before the
handler runs. These are input checks, not security guarantees: the handler still
opens the file and handles permissions, replacement and filesystem races.

Use `Minimum`/`Maximum` for inclusive numeric input bounds. `Requires` and
`ConflictsWith` refer to stable option IDs, not spellings: parameter `dryRun`
gets ID `dry-run`. Presence includes captured environment fallback; an environment
flag set to `false` is absent. Dependencies do not make an optional flag implicit.
The builder uses the same `CommandHelp` properties. Range/path/relationship checks
run during execution before binding/handler invocation; hosted classification
remains free of filesystem reads. Invalid input returns a safe `RCLI2002` usage
fault naming the parameter, without echoing its value.

```csharp
[Command("copy", Description = "Copy a file.")]
internal static Task Copy(
    [Option("--source", MustExist = true)] FileInfo source,
    [Option("--destination", PathKind = CommandPathKind.File)] string destination,
    [Option("--overwrite", ConflictsWith = ["dry-run"])] bool overwrite,
    [Option("--dry-run")] bool dryRun,
    CancellationToken cancellationToken) => /* application operation */ Task.CompletedTask;
```

Customize a generated command's human result without another handler or codec:

```csharp
var catalog = GeneratedCommandCatalog.Create(builder =>
    builder.Present<Report>("report", (report, console, culture, token) =>
        console.WriteOutAsync($"Processed {report.Count} items\n".AsMemory(), token)));
```

The command's declared result payload identity and source-generated JSON metadata
remain unchanged. The delegate only runs in human mode. Register against a
canonical command path; a mismatched result type is a catalog validation error.

Completion scripts add filesystem hints for visible, non-sensitive path options
and aliases, including directory-only hints. Bash and PowerShell also handle
`--option=value`. Other candidate lists remain static across the catalog; this is
not a context-aware CLI parser embedded in each shell. Install scripts explicitly
in your shell's completion setup (Zsh requires `compinit`); Runic never modifies
shell profiles. The shell's ordinary filename fallback serves positional paths.
See the [complete examples](../../../examples/command-line/README.md).
