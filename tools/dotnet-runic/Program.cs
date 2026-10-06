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

internal static partial class Program
{

    internal const int Success = 0;
    internal const int DevelopmentFailure = 1;
    internal const int UsageFailure = 2;
    internal const int InternalFailure = 3;

    private const string ProjectDescription =
        "The project file, or a directory with one .csproj. Defaults to the current directory.";

    internal static Task<int> Main(string[] arguments) => RunAsync(arguments);

    internal static async Task<int> RunAsync(string[] arguments, ICommandConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return await new CommandApp(GeneratedCommandCatalog.Create())
        {
            Name = "dotnet runic",
            CompletionExecutableName = "dotnet-runic",
            Version = Version,
            HelpPresenter = new Runic.CommandLine.Spectre.SpectreHelpPresenter(),
            Console = console ?? new SpectreCommandConsole(),
            ExitCodePolicy = ToolExitCodePolicy.Instance,
        }.RunAsync(arguments).ConfigureAwait(false);
    }

    [Command("dev", Description = "Run the application with development watchers.",
        Examples = ["dotnet runic dev", "dotnet runic dev --project ./MyApp.csproj -- --app-argument"])]
    [DefaultCommand]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Dev(
        CommandExecutionContext context,
        [Option("--no-restore", Description = "Skip the .NET restore and the JavaScript package install. The build does not install missing frontend packages either.")] bool noRestore,
        [Option("--no-frontend-watch", Description = "Build the frontend once instead of starting its development server or watcher.")] bool noFrontendWatch,
        [Option("--no-dotnet-watch", Description = "Run the application once instead of restarting it under dotnet watch after C# edits.")] bool noDotNetWatch,
        [Option("--dry-run", Description = "Print the evaluated project configuration and exit without changing files or starting processes.")] bool dryRun,
        [Argument(AllowMultipleValues = true, Description = "Arguments after -- are passed to the application.")] IReadOnlyList<string> applicationArguments,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = "The build configuration.")] string configuration = "Debug")
    {
        return ExecuteAsync(context, "dev", async () =>
        {
            var options = new DevOptions(
                string.IsNullOrWhiteSpace(project) ? null : project,
                configuration,
                !noRestore,
                !noFrontendWatch,
                !noDotNetWatch,
                dryRun,
                applicationArguments);
            return await DevApplication.RunAsync(options, cancellationToken).ConfigureAwait(false);
        }, stream: !dryRun);
    }

    [Command("size", Description = "Measure a published application and write a size report.",
        Examples = ["dotnet runic size --runtime linux-x64 --report measurements/linux.json"])]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static Task<CommandOutcome<ToolCommandResult>> Size(
        CommandExecutionContext context,
        [Option("--no-aot", Description = "Publish without Native AOT.")] bool noAot,
        [Option("--verify-argument", AllowMultipleValues = true, Description = "An argument for the --verify executable. Repeat it for several arguments.")] IReadOnlyList<string> verifyArguments,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--runtime", "-r", Description = "The runtime identifier to publish for, for example linux-x64. Required.")] string runtime = "",
        [Option("--configuration", "-c", Description = "The build configuration.")] string configuration = "Release",
        [Option("--report", Description = "The JSON report to write. The file must not exist yet.")] string report = "runic-size.json",
        [Option("--verify", Description = "A published file, relative to the publish directory, to run as an executable check.")] string verify = "") =>
        ExecuteAsync(context, "size", () => SizeApplication.RunAsync(new SizeOptions(
            string.IsNullOrWhiteSpace(project) ? null : project, runtime,
            configuration, report, !noAot, verify, verifyArguments), cancellationToken), stream: true);

    [Command("doctor", Description = "Check the project and development environment.",
        Examples = ["dotnet runic doctor --project ./MyApp.csproj", "dotnet runic doctor --rid win-x64", "dotnet runic doctor --output json"])]
    [CommandResult(DoctorCommandResult.PayloadType, typeof(DoctorCommandJsonContext))]
    internal static Task<CommandOutcome<DoctorCommandResult>> Doctor(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = "The build configuration to evaluate.")] string configuration = "Debug",
        [Option("--fail-on", Description = "Fail when a check reaches this status: never, fail or warn. Defaults to fail for human output and never for JSON output.")] string failOn = "",
        [Option("--rid", "--runtime", "-r", Description = "Also check deployment prerequisites for this runtime identifier, for example linux-x64, win-x64 or osx-arm64.")] string rid = "")
    {
        return ExecuteAsync<DoctorCommandResult>(context, stream: false, async output =>
        {
            DoctorFailOn threshold = DoctorOutcome.ParseFailOn(failOn, context.OutputMode);
            DoctorTargetRid? target = string.IsNullOrEmpty(rid) ? null : DoctorTargetRid.Parse(rid);
            DoctorRun run = await DoctorApplication.InspectAsync(
                new DoctorOptions(string.IsNullOrWhiteSpace(project) ? null : project, configuration, target),
                cancellationToken).ConfigureAwait(false);
            DoctorApplication.WriteReport(run.Project, run.Report, run.Target);
            return DoctorOutcome.Create(
                run, context.OutputMode, threshold, context.Path,
                output.ToString().TrimEnd(), BoundedHumanOutput(output));
        });
    }

    [Command("support", Description = "Preview, collect or remove a local support envelope built from an Editor diagnostic ZIP.",
        Examples = ["dotnet runic support --mode preview --editor-diagnostics ./editor-diagnostics.zip"])]
    [CommandResult("runic.application.tool/1", typeof(ToolCommandJsonContext))]
    internal static async Task<CommandOutcome<ToolCommandResult>> Support(
        CancellationToken cancellationToken,
        [Option("--mode", Description = "preview lists what would be collected, collect writes the envelope, and remove verifies and deletes it.")] string mode = "preview",
        [Option("--editor-diagnostics", Description = "The Editor diagnostic ZIP to read. Required by preview and collect.")] string editorDiagnostics = "",
        [Option("--destination", Description = "The support envelope JSON file. Required by collect and remove.")] string destination = "")
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

    private static Task<CommandOutcome<ToolCommandResult>> ExecuteAsync(CommandExecutionContext context, string command, Func<Task<int>> operation, bool stream = false) =>
        ExecuteAsync<ToolCommandResult>(context, stream, async output =>
        {
            int exitCode = await operation().ConfigureAwait(false);
            return exitCode == Success
                ? CommandOutcome.Success(new ToolCommandResult(command, exitCode, output.ToString().TrimEnd()))
                : CommandOutcome.Failure<ToolCommandResult>(
                    CommandExitCategory.CommandFailure,
                    new CommandFault("RAPPCLI1000", $"The {command} command did not complete successfully."),
                    diagnostics: [],
                    humanOutput: BoundedHumanOutput(output));
        });

    private static async Task<CommandOutcome<T>> ExecuteAsync<T>(
        CommandExecutionContext context,
        bool stream,
        Func<StringWriter, Task<CommandOutcome<T>>> operation)
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
            return await operation(output).ConfigureAwait(false);
        }
        catch (DevUsageException exception)
        {
            return Failure<T>(CommandExitCategory.Usage, exception.Code, exception.Message, WithDetail(BoundedHumanOutput(output), exception.LocalDetail));
        }
        catch (DevDevelopmentException exception)
        {
            return Failure<T>(CommandExitCategory.CommandFailure, exception.Code, exception.Message, WithDetail(BoundedHumanOutput(output), exception.LocalDetail));
        }
        catch (System.Text.Json.JsonException)
        {
            return CommandOutcome.Failure<T>(CommandExitCategory.HostFailure, new CommandFault("RAPPCLI1003", "MSBuild returned invalid configuration JSON."));
        }
        catch (System.IO.IOException)
        {
            return CommandOutcome.Failure<T>(CommandExitCategory.CommandFailure, new CommandFault("RAPPCLI1008", "The command could not access a required local file."));
        }
        catch (UnauthorizedAccessException)
        {
            return CommandOutcome.Failure<T>(CommandExitCategory.CommandFailure, new CommandFault("RAPPCLI1008", "The command could not access a required local file."));
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
        string? humanOutput = null) => Failure<ToolCommandResult>(category, code, message, humanOutput);

    /// <summary>
    /// Backstop for fault messages: finds any rooted path token (a Unix path
    /// after a boundary, a drive letter path, a UNC path, or any backslash).
    /// </summary>
    internal static bool ContainsRootedPath(string message) =>
        message.Contains('\\') || RootedPath().IsMatch(message);

    [System.Text.RegularExpressions.GeneratedRegex("""(?:^|[\s'"(=:,])(?:/[^\s/]|[A-Za-z]:[\\/])""")]
    private static partial System.Text.RegularExpressions.Regex RootedPath();

    internal static CommandOutcome<T> Failure<T>(
        CommandExitCategory category,
        string code,
        string message,
        string? humanOutput = null)
    {
        bool containsPrivatePath = ContainsRootedPath(message);
        string? detail = containsPrivatePath
            ? string.Concat(humanOutput, message, "\n")
            : humanOutput;
        return CommandOutcome.Failure<T>(
            category,
            new CommandFault(
                code,
                containsPrivatePath
                    ? "The command could not be completed. See the local detail above."
                    : message),
            diagnostics: [],
            detail);
    }

    private static string? WithDetail(string? humanOutput, string? localDetail) =>
        string.IsNullOrEmpty(localDetail) ? humanOutput : string.Concat(humanOutput, localDetail);

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
