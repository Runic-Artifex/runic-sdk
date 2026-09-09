using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Spectre;
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
        if (arguments.Length > 0 && arguments[0] == "__bridge-inspect")
            return await BridgeInspectionClient.RunAsync(arguments[1..]).ConfigureAwait(false);
        return await new CommandApp(GeneratedCommandCatalog.Create())
        {
            Name = "dotnet runic",
            CompletionExecutableName = "dotnet-runic",
            Version = Version,
        HelpPresenter = new Runic.CommandLine.Spectre.SpectreHelpPresenter(),
            Console = new SpectreCommandConsole(),
            ExitCodePolicy = ToolExitCodePolicy.Instance,
        }.RunAsync(arguments).ConfigureAwait(false);
    }

    [Command("dev", Description = "Run the application with development watchers.")]
    [DefaultCommand]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Dev(
        CommandExecutionContext context,
        [Option("--no-restore")] bool noRestore,
        [Option("--no-contracts")] bool noContracts,
        [Option("--no-frontend-watch")] bool noFrontendWatch,
        [Option("--no-dotnet-watch")] bool noDotNetWatch,
        [Option("--dry-run")] bool dryRun,
        [Argument(AllowMultipleValues = true)] IReadOnlyList<string> applicationArguments,
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--configuration")] string configuration = "Debug",
        [Option("--host")] string host = "")
    {
        return ExecuteAsync(context, "dev", async () =>
        {
            var options = new DevOptions(
            string.IsNullOrWhiteSpace(project) ? null : project,
            configuration,
            !noRestore,
            !noContracts,
            !noFrontendWatch,
            !noDotNetWatch,
            dryRun,
                applicationArguments) { Host = host };
            return await DevApplication.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }, stream: !dryRun);
    }

    [Command("size", Description = "Measure a published application and write a size report.")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Size(
        CommandExecutionContext context,
        [Option("--no-aot")] bool noAot,
        [Option("--verify-argument", AllowMultipleValues = true)] IReadOnlyList<string> verifyArguments,
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--runtime", "-r")] string runtime = "",
        [Option("--host")] string host = "",
        [Option("--profile")] string profile = "default",
        [Option("--configuration")] string configuration = "Release",
        [Option("--report")] string report = "runic-size.json",
        [Option("--verify")] string verify = "") =>
        ExecuteAsync(context, "size", () => SizeApplication.RunAsync(new SizeOptions(
            string.IsNullOrWhiteSpace(project) ? null : project, runtime, host, profile,
            configuration, report, !noAot, verify, verifyArguments), cancellationToken), stream: true);

    [Command("doctor", Description = "Check the project and development environment.", Examples = ["dotnet runic doctor --project ./MyApp.csproj"])]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Doctor(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--configuration")] string configuration = "Debug")
    {
        return ExecuteAsync(context, "doctor", async () => await DoctorApplication.RunAsync(
            new DoctorOptions(string.IsNullOrWhiteSpace(project) ? null : project, configuration), cancellationToken).ConfigureAwait(false));
    }

    [Command("inspect")]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Inspect(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p")] string project = "",
        [Option("--configuration")] string configuration = "Debug",
        [Option("--artifact")] string artifact = "manifest")
    {
        return ExecuteAsync(context, "inspect", async () => await InspectApplication.RunAsync(
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

    private static async Task<CommandOutcome<ToolCommandResult>> ExecuteAsync(CommandExecutionContext context, string command, Func<Task<int>> operation, bool stream = false)
    {
        TextWriter originalOutput = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            // Long-running development output must reach the terminal immediately.
            // JSON invocations reserve stdout for the final command envelope.
            Console.SetOut(stream ? new InvocationTextWriter(context.Console, standardError: false) : output);
            Console.SetError(stream ? new InvocationTextWriter(context.Console, standardError: true) : output);
            int exitCode = await operation().ConfigureAwait(false);
            return exitCode == Success
                ? CommandOutcome.Success(new ToolCommandResult(command, exitCode, output.ToString().TrimEnd()))
                : CommandOutcome.Failure<ToolCommandResult>(
                    CommandExitCategory.CommandFailure,
                    new CommandFault("RAPPCLI1000", $"The {command} command did not complete successfully."),
                    diagnostics: [],
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
        string? humanOutput = null)
    {
        bool containsPrivatePath =
            message.Contains('\\') ||
            message.Contains("/home/", StringComparison.Ordinal) ||
            message.Contains("/Users/", StringComparison.Ordinal) ||
            message.Contains("/root/", StringComparison.Ordinal) ||
            message.Contains("/tmp/", StringComparison.Ordinal);
        string? detail = containsPrivatePath
            ? string.Concat(humanOutput, message, "\n")
            : humanOutput;
        return CommandOutcome.Failure<ToolCommandResult>(
            category,
            new CommandFault(
                code,
                containsPrivatePath
                    ? "The command could not be completed. See the local detail above."
                    : message),
            diagnostics: [],
            detail);
    }

    private static string? BoundedHumanOutput(StringWriter output)
    {
        string text = output.ToString();
        const int maximum = 64 * 1024;
        if (text.Length == 0) return null;
        return text.Length <= maximum ? text : text[..maximum] + "\n[output truncated]\n";
    }

    private static string Version
    {
        get
        {
            string value = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
            int metadata = value.IndexOf('+');
            return metadata < 0 ? value : value[..metadata];
        }
    }

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

internal sealed class InvocationTextWriter(ICommandConsole console, bool standardError) : TextWriter
{
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    public override void Write(string? value)
    {
        if (value is null) return;
        if (standardError) console.WriteErrorAsync(value.AsMemory(), default).AsTask().GetAwaiter().GetResult();
        else console.WriteOutAsync(value.AsMemory(), default).AsTask().GetAwaiter().GetResult();
    }
    public override void Write(char value) => Write(value.ToString());
    public override void Write(ReadOnlySpan<char> value) => Write(value.ToString());
    public override void WriteLine(string? value) => Write(value + NewLine);
}
