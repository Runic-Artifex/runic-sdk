using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
namespace Runic.Application.Tool;

internal static class Program
{
    internal const int Success = 0;
    internal const int DevelopmentFailure = 1;
    internal const int UsageFailure = 2;
    internal const int InternalFailure = 3;

    internal static async Task<int> Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var console = new ProcessCommandConsole();
        ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
            GeneratedCommandCatalog.Create(),
            arguments.Length == 0 ? ["dev"] : arguments,
            new ParseSettings(Environment.GetEnvironmentVariable(CommandOutputClassifier.EnvironmentVariableName)));
        if (parse.Kind == ParseOutcomeKind.Help)
        {
            await console.WriteOutAsync(Help.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return Success;
        }
        if (parse.Kind == ParseOutcomeKind.Version)
        {
            await console.WriteOutAsync("dotnet-runic\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return Success;
        }
        if (parse.Kind != ParseOutcomeKind.Invocation || parse.Invocation is null)
        {
            return await PresentParseFailureAsync(parse, console).ConfigureAwait(false);
        }

        CommandExecutionResult result = await new CommandExecutor(EmptyScopeFactory.Instance, ToolExitCodePolicy.Instance).ExecuteAsync(
            new CommandExecutionRequest(parse.Invocation, console, CultureInfo.InvariantCulture, "runic"),
            new CommandOutputDispatcher()).ConfigureAwait(false);
        return result.ExitCode;
    }

    [Command("dev")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Dev(
        [Option("--no-restore")] bool noRestore,
        [Option("--no-contracts")] bool noContracts,
        [Option("--no-frontend-watch")] bool noFrontendWatch,
        [Option("--no-dotnet-watch")] bool noDotNetWatch,
        [Option("--dry-run")] bool dryRun,
        [Argument(AllowMultipleValues = true)] IReadOnlyList<string> applicationArguments,
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--configuration")] string configuration = "Debug")
    {
        return ExecuteAsync("dev", async () =>
        {
            var options = new DevOptions(
            string.IsNullOrWhiteSpace(project) ? null : project,
            configuration,
            !noRestore,
            !noContracts,
            !noFrontendWatch,
            !noDotNetWatch,
            dryRun,
                applicationArguments);
            return await DevApplication.RunAsync(options, cancellationToken).ConfigureAwait(false);
        });
    }

    [Command("doctor")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Doctor(
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--configuration")] string configuration = "Debug")
    {
        return ExecuteAsync("doctor", async () => await DoctorApplication.RunAsync(
            new DoctorOptions(string.IsNullOrWhiteSpace(project) ? null : project, configuration), cancellationToken).ConfigureAwait(false));
    }

    [Command("inspect")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Inspect(
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--configuration")] string configuration = "Debug",
        [Option("--artifact")] string artifact = "manifest")
    {
        return ExecuteAsync("inspect", async () => await InspectApplication.RunAsync(
            string.IsNullOrWhiteSpace(project) ? null : project,
            configuration,
            artifact,
            cancellationToken).ConfigureAwait(false));
    }

    [Command("migrate")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Migrate(
        [Option("--check")] bool check,
        [Option("--apply")] bool apply,
        [Option("--dry-run")] bool dryRun,
        [Option("--project", "-p")] string project = "")
    {
        return Task.FromResult(MigrateCore(check, apply, dryRun, project));
    }

    [Command("support")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static async Task<CommandOutcome<ToolCommandResult>> Support(
        CancellationToken cancellationToken,
        [Option("--mode")] string mode = "preview",
        [Option("--editor-diagnostics")] string editorDiagnostics = "",
        [Option("--destination")] string destination = "")
    {
        try
        {
            SupportCommandResult result = await SupportApplication.ExecuteAsync(
                new SupportOptions(mode, string.IsNullOrWhiteSpace(editorDiagnostics) ? null : editorDiagnostics, string.IsNullOrWhiteSpace(destination) ? null : destination),
                cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new ToolCommandResult("support", Success, result.ToHumanOutput()));
        }
        catch (SupportUsageException exception)
        {
            return Failure(CommandExitCategory.Usage, exception.Code, exception.Message);
        }
        catch (IOException)
        {
            return Failure(CommandExitCategory.CommandFailure, "RAPPSUP025", "The local support envelope could not access a required file.");
        }
    }

    private static CommandOutcome<ToolCommandResult> MigrateCore(
        bool check,
        bool apply,
        bool dryRun,
        string project)
    {
        try
        {
            MigrationResult result = MigrationApplication.Execute(
                string.IsNullOrWhiteSpace(project) ? null : project,
                apply,
                dryRun,
                check);
            if (check && result.HasChanges)
            {
                return CommandOutcome.Failure<ToolCommandResult>(
                    CommandExitCategory.CommandFailure,
                    new CommandFault("RAPPMIG001", "Legacy application migration is required."),
                    [new CommandDiagnostic(
                        "RCLI9001",
                        "migration",
                        "Legacy application migration is required.",
                        CommandDiagnosticPhase.Execution,
                        CommandDiagnosticSeverity.Error)],
                    result.Output);
            }

            return CommandOutcome.Success(new ToolCommandResult("migrate", Success, result.Output));
        }
        catch (DevUsageException exception)
        {
            return Failure(CommandExitCategory.Usage, exception.Code, exception.Message);
        }
    }

    private static async Task<CommandOutcome<ToolCommandResult>> ExecuteAsync(string command, Func<Task<int>> operation)
    {
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            Console.SetError(output);
            int exitCode = await operation().ConfigureAwait(false);
            return exitCode == Success
                ? CommandOutcome.Success(new ToolCommandResult(command, exitCode, output.ToString().TrimEnd()))
                : CommandOutcome.Failure<ToolCommandResult>(
                    CommandExitCategory.CommandFailure,
                    new CommandFault("RAPPCLI1000", $"The {command} command did not complete successfully."),
                    diagnostics: null,
                    humanOutput: BoundedHumanOutput(output));
        }
        catch (DevUsageException exception)
        {
            return Failure(CommandExitCategory.Usage, exception.Code, exception.Message, BoundedHumanOutput(output));
        }
        catch (DevDevelopmentException exception)
        {
            return Failure(CommandExitCategory.CommandFailure, exception.Code, exception.Message, BoundedHumanOutput(output));
        }
        catch (System.Text.Json.JsonException)
        {
            return CommandOutcome.Failure<ToolCommandResult>(CommandExitCategory.HostFailure, new CommandFault("RAPPCLI1003", "MSBuild returned invalid configuration JSON."));
        }
        catch (System.IO.IOException)
        {
            return CommandOutcome.Failure<ToolCommandResult>(CommandExitCategory.CommandFailure, new CommandFault("RAPPCLI1008", "The command could not access a required local file."));
        }
        catch (UnauthorizedAccessException)
        {
            return CommandOutcome.Failure<ToolCommandResult>(CommandExitCategory.CommandFailure, new CommandFault("RAPPCLI1008", "The command could not access a required local file."));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static CommandOutcome<ToolCommandResult> Failure(
        CommandExitCategory category,
        string code,
        string message,
        string? humanOutput = null) =>
        CommandOutcome.Failure<ToolCommandResult>(
            category,
            new CommandFault(code, message),
            [new CommandDiagnostic(
                "RCLI9001",
                "tool-validation",
                message,
                CommandDiagnosticPhase.Execution,
                CommandDiagnosticSeverity.Error)],
            humanOutput);

    private static async Task<int> PresentParseFailureAsync(ParseOutcome parse, ICommandConsole console)
    {
        if (parse.OutputClassification is { IsValid: true, Mode: CommandOutputMode mode } &&
            parse.Diagnostics.Count > 0)
        {
            CommandDiagnostic diagnostic = parse.Diagnostics[0];
            CommandResponse<ToolCommandResult> response = CommandResponse.Failed<ToolCommandResult>(
                "runic",
                "runic",
                UsageFailure,
                new CommandFault(diagnostic.Code, diagnostic.Message),
                parse.Diagnostics);
            await CommandOutputDispatcher.DispatchAsync(
                mode,
                console,
                CultureInfo.InvariantCulture,
                response,
                ToolCommandResultCodec.Instance).ConfigureAwait(false);
            return UsageFailure;
        }

        await CommandParsePresentation.WriteHumanAsync(
            parse,
            console,
            static (diagnostic, _) => $"dotnet runic: {diagnostic.Code}: {diagnostic.Message}\n",
            CultureInfo.InvariantCulture).ConfigureAwait(false);
        return UsageFailure;
    }

    private static string? BoundedHumanOutput(StringWriter output)
    {
        string text = output.ToString();
        const int maximum = 64 * 1024;
        if (text.Length == 0) return null;
        return text.Length <= maximum ? text : text[..maximum] + "\n[output truncated]\n";
    }

    private const string Help = """
        Usage:
          dotnet runic dev [options] [-- <application-args>...]
          dotnet runic inspect [options]
          dotnet runic doctor [options]
          dotnet runic support [--mode preview|collect|remove] [--editor-diagnostics <zip>] [--destination <path>]
          dotnet runic migrate [--project path] [--check|--dry-run|--apply]

        Commands are generated with Runic Command Line.
        """;
}

internal sealed record ToolCommandResult(string Command, int ExitCode, string? Output = null)
{
    public override string ToString() => Output ?? (ExitCode == 0 ? $"{Command}: complete" : $"{Command}: failed ({ExitCode})");
}

internal sealed class ToolExitCodePolicy : IExitCodePolicy
{
    internal static ToolExitCodePolicy Instance { get; } = new();
    public int GetExitCode(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Success => Program.Success,
        CommandExitCategory.Usage or CommandExitCategory.Validation => Program.UsageFailure,
        CommandExitCategory.CommandFailure or CommandExitCategory.Unavailable => Program.DevelopmentFailure,
        _ => Program.InternalFailure,
    };
}

[JsonSerializable(typeof(ToolCommandResult))]
internal sealed partial class ToolCommandJsonContext : JsonSerializerContext;

internal sealed class ToolCommandResultCodec : ICommandResultCodec<ToolCommandResult>
{
    internal static ToolCommandResultCodec Instance { get; } = new();

    public string PayloadType => "runic.application.tool/1";
    public JsonTypeInfo<ToolCommandResult> TypeInfo => ToolCommandJsonContext.Default.ToolCommandResult;

    public ValueTask WriteHumanAsync(
        ToolCommandResult value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken) =>
        console.WriteOutAsync((value.ToString() + "\n").AsMemory(), cancellationToken);
}

internal sealed class ProcessCommandConsole : ICommandConsole
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public bool IsInputRedirected => Console.IsInputRedirected;
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    public bool IsErrorRedirected => Console.IsErrorRedirected;
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Console.ReadLine());
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { Console.Out.Write(value.Span); return ValueTask.CompletedTask; }
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) { Console.OpenStandardOutput().Write(value.Span); return ValueTask.CompletedTask; }
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { Console.Error.Write(value.Span); return ValueTask.CompletedTask; }
}

internal sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
{
    internal static EmptyScopeFactory Instance { get; } = new();
    public ICommandExecutionScope CreateScope() => EmptyScope.Instance;
    private sealed class EmptyScope : ICommandExecutionScope
    {
        internal static EmptyScope Instance { get; } = new();
        public IServiceProvider Services { get; } = new EmptyServices();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
}
