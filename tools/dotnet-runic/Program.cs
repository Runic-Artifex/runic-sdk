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
    /// <summary>The command was cancelled, for example with Ctrl+C: 128 + SIGINT, as shells report it.</summary>
    internal const int Cancelled = 130;

    /// <summary>
    /// Selects human or JSON output, as in every Runic tool. <c>--output</c> is its former spelling, a hidden alias
    /// until 2.0 (<see cref="Runic.Tools.Shared.OutputSelectorAlias"/>).
    /// </summary>
    internal const string FormatOptionName = Runic.Tools.Shared.OutputSelectorAlias.Selector;
    internal const string FormerFormatOptionName = "--output";

    private const string ProjectDescription =
        "The project file, or a directory with one .csproj. Defaults to the current directory.";

    internal static Task<int> Main(string[] arguments) =>
        arguments is [ProcessTreeSignal.CtrlCHelperArgument, string processId]
            ? Task.FromResult(ProcessTreeSignal.RunCtrlCHelper(processId))
            : RunAsync(arguments);

    internal static async Task<int> RunAsync(string[] arguments, ICommandConsole? console = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        arguments = Runic.Tools.Shared.OutputSelectorAlias.Rewrite(arguments, FormerFormatOptionName);
        if (SupportForwarding.TryParse(arguments, out SupportForwarding? forwarding) &&
            SupportForwarding.ResolveTool(Environment.CurrentDirectory, SystemDoctorRuntime.Instance.FindExecutable, DevApplication.ResolveDotNetHost()) is { } translations)
        {
            return await forwarding!.RunAsync(translations, console ?? new SpectreCommandConsole()).ConfigureAwait(false);
        }
        using ToolSignalCancellation signals = ToolSignalCancellation.Register();
        return await new CommandApp(GeneratedCommandCatalog.Create())
        {
            ParseSettings = new ParseSettings(
                Environment.GetEnvironmentVariable(CommandOutputClassifier.EnvironmentVariableName),
                transportOutputOptionName: FormatOptionName)
            {
                GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            },
            // ToolSignalCancellation keeps Command Line's behavior but lets dev finish its cleanup.
            HandleCancelKeyPress = false,
            Name = "dotnet runic",
            CompletionExecutableName = "dotnet-runic",
            Version = Version,
            HelpPresenter = new Runic.CommandLine.Spectre.SpectreHelpPresenter(),
            Console = console ?? new SpectreCommandConsole(),
            ExitCodePolicy = ToolExitCodePolicy.Instance,
            // Command Line reports an unexpected exception only as RCLI5000; name its cause.
            ExceptionObserver = ReportUnexpectedFailure,
        }.RunAsync(arguments, signals.Token).ConfigureAwait(false);
    }

    internal static void ReportUnexpectedFailure(Exception exception) =>
        Console.Error.WriteLine($"dotnet runic failed unexpectedly: {exception}");

    [Command("dev", Description = "Run the application with development watchers.",
        Examples = ["dotnet runic dev", "dotnet runic dev --project ./MyApp.csproj -- --app-argument"])]
    [DefaultCommand]
    [CommandResult(DevCommandResult.PayloadType, typeof(DevCommandJsonContext))]
    internal static Task<CommandOutcome<DevCommandResult>> Dev(
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
        var options = new DevOptions(
            string.IsNullOrWhiteSpace(project) ? null : project,
            configuration,
            !noRestore,
            !noFrontendWatch,
            !noDotNetWatch,
            dryRun,
            applicationArguments);
        DevCommandResult? result = null;
        return ExecuteAsync(context, "dev",
            async () => await DevApplication.RunAsync(options, cancellationToken,
                evaluated => result = DevCommandResult.Create(evaluated, dryRun)).ConfigureAwait(false),
            () => result, stream: !dryRun);
    }

    [Command("size", Description = "Measure a published application and write a size report.",
        Examples = ["dotnet runic size --runtime linux-x64 --report measurements/linux.json"])]
    [CommandResult(SizeCommandResult.PayloadType, typeof(SizeCommandJsonContext))]
    internal static Task<CommandOutcome<SizeCommandResult>> Size(
        CommandExecutionContext context,
        [Option("--aot", ConflictsWith = ["no-aot"], Description = "Publish with Native AOT, the default.")] bool aot,
        [Option("--no-aot", ConflictsWith = ["aot"], Description = "Publish without Native AOT.")] bool noAot,
        [Option("--verify-argument", AllowMultipleValues = true, Description = "An argument for the --verify executable. Repeat it for several arguments.")] IReadOnlyList<string> verifyArguments,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--runtime", "--rid", "-r", Description = "The runtime identifier to publish for, for example linux-x64. Required.")] string runtime = "",
        [Option("--configuration", "-c", Description = "The build configuration.")] string configuration = "Release",
        [Option("--report", Description = "The JSON report to write. The file must not exist yet. Default: runic-size-<runtime>-<UTC time>.json in the current directory.")] string report = "",
        [Option("--verify", Description = "An executable to run after a successful publish, as a path or a command on PATH, such as node. It receives the --verify-argument values, then the publish directory and the published application's path. A nonzero exit code fails the command.")] string verify = "")
    {
        SizeCommandResult? result = null;
        return ExecuteAsync(context, "size", async () =>
        {
            SizeRun run = await SizeApplication.RunAsync(new SizeOptions(
                string.IsNullOrWhiteSpace(project) ? null : project, runtime,
                configuration, report, aot || !noAot, verify, verifyArguments), cancellationToken).ConfigureAwait(false);
            result = run.Result;
            return run.ExitCode;
        }, () => result, stream: true);
    }

    [Command("doctor", Description = "Check the project and development environment.",
        Examples = ["dotnet runic doctor --project ./MyApp.csproj", "dotnet runic doctor --runtime linux-x64 --aot", "dotnet runic doctor --format json"])]
    [CommandResult(DoctorCommandResult.PayloadType, typeof(DoctorCommandJsonContext))]
    internal static Task<CommandOutcome<DoctorCommandResult>> Doctor(
        CommandExecutionContext context,
        [Option("--aot", ConflictsWith = ["no-aot"], Description = "With --runtime, check a publish with -p:PublishAot=true.")] bool aot,
        [Option("--no-aot", ConflictsWith = ["aot"], Description = "With --runtime, check a publish with -p:PublishAot=false.")] bool noAot,
        [Option("--self-contained", Description = "With --runtime, check a publish with --self-contained true.")] bool selfContained,
        CancellationToken cancellationToken,
        [Option("--project", "-p", Description = ProjectDescription)] string project = "",
        [Option("--configuration", "-c", Description = "The build configuration to evaluate. Defaults to Debug, or Release with --runtime as dotnet publish does.")] string configuration = "",
        [Option("--fail-on", Description = "Fail when a check reaches this status: never, fail or warn. Defaults to fail in both output modes.")] string failOn = "",
        [Option("--runtime", "--rid", "-r", Description = "Also check deployment prerequisites for this runtime identifier, for example linux-x64, win-x64 or osx-arm64.")] string rid = "")
    {
        return ExecuteAsync<DoctorCommandResult>(context, stream: false, async output =>
        {
            DoctorFailOn threshold = DoctorOutcome.ParseFailOn(failOn);
            DoctorTargetRid? target = string.IsNullOrEmpty(rid) ? null : DoctorTargetRid.Parse(rid);
            if (target is null && (aot || noAot || selfContained))
            {
                throw new DevUsageException(DoctorTargetRid.UsageCode, "--aot, --no-aot and --self-contained require --runtime.");
            }
            string evaluated = configuration.Length != 0 ? configuration : target is null ? "Debug" : "Release";
            DoctorRun run = await DoctorApplication.InspectAsync(
                new DoctorOptions(string.IsNullOrWhiteSpace(project) ? null : project, evaluated, target, aot ? true : noAot ? false : null, selfContained),
                cancellationToken).ConfigureAwait(false);
            DoctorApplication.WriteReport(run.Project, run.Report, run.Target);
            return DoctorOutcome.Create(
                run, context.OutputMode, threshold, context.Path,
                output.ToString().TrimEnd(), BoundedHumanOutput(output));
        });
    }

    /// <summary>
    /// The former <c>support</c> command, hidden. <see cref="RunAsync"/> forwards it to <c>runic-translations
    /// support</c>; this command runs only when that tool is not installed.
    /// </summary>
    [Command(SupportForwarding.Command, Hidden = true, Description = "Moved to runic-translations support.")]
    internal static CommandOutcome<CommandUnit> Support(
        [Option("--mode", Hidden = true, Description = "Moved to runic-translations support.")] string mode = "",
        [Option("--editor-diagnostics", Hidden = true, Description = "Moved to runic-translations support.")] string editorDiagnostics = "",
        [Option("--destination", Hidden = true, Description = "Moved to runic-translations support.")] string destination = "")
    {
        _ = (mode, editorDiagnostics, destination);
        return Failure<CommandUnit>(CommandExitCategory.Unavailable, SupportForwarding.MissingToolCode, SupportForwarding.MissingToolMessage);
    }

    /// <summary>
    /// Runs a command that reports an exit code, and its payload once known. A failure carries the payload in the
    /// envelope's <c>fault.data</c> when the command produced one.
    /// </summary>
    private static Task<CommandOutcome<T>> ExecuteAsync<T>(
        CommandExecutionContext context, string command, Func<Task<int>> operation, Func<T?> result, bool stream)
        where T : class, IToolCommandResult<T> =>
        ExecuteAsync<T>(context, stream, async output =>
        {
            int exitCode = await operation().ConfigureAwait(false);
            T? payload = result();
            if (exitCode == Success && payload is not null)
                return CommandOutcome.Success(payload.WithHumanOutput(output.ToString().TrimEnd()));
            if (exitCode == Cancelled)
                return CommandOutcome.Failure<T>(CommandExitCategory.Cancelled, new CommandFault(CancelledCode, $"The {command} command was cancelled."));
            var fault = new CommandFault("RAPPCLI1000", $"The {command} command did not complete successfully.");
            string? human = WithHelpLink(BoundedHumanOutput(output), "RAPPCLI1000");
            return payload is null
                ? CommandOutcome.Failure<T>(CommandExitCategory.CommandFailure, fault, diagnostics: [], human)
                : CommandOutcome.FailureWithData<T>(CommandExitCategory.CommandFailure, fault,
                    CommandFailureData.Create(T.PayloadType, payload, T.JsonTypeInfo), diagnostics: [], human);
        });

    /// <summary>The fault code of a cancelled command, the one Runic.CommandLine reports.</summary>
    internal const string CancelledCode = "RCLI4000";

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
            return Failure<T>(CommandExitCategory.HostFailure, "RAPPCLI1003", "MSBuild returned invalid configuration JSON.");
        }
        catch (System.IO.IOException)
        {
            return Failure<T>(CommandExitCategory.CommandFailure, "RAPPCLI1008", "The command could not access a required local file.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure<T>(CommandExitCategory.CommandFailure, "RAPPCLI1008", "The command could not access a required local file.");
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

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
        string? detail = WithHelpLink(containsPrivatePath
            ? string.Concat(humanOutput, message, "\n")
            : humanOutput, code);
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

    /// <summary>
    /// Ends human output with the fault code's catalog entry at this release's tag.
    /// Runic.CommandLine writes it before the fault line and omits it from JSON output.
    /// </summary>
    internal static string WithHelpLink(string? humanOutput, string code) =>
        string.Concat(humanOutput, $"Help for {code}: {RunicReleaseLinks.Diagnostic(code)}\n");

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

/// <summary>A command payload that <see cref="Program"/> can also attach to a failed envelope's <c>fault.data</c>.</summary>
internal interface IToolCommandResult<TSelf> where TSelf : class, IToolCommandResult<TSelf>
{
    static abstract string PayloadType { get; }
    static abstract JsonTypeInfo<TSelf> JsonTypeInfo { get; }
    TSelf WithHumanOutput(string humanOutput);
}

/// <summary>
/// The exit codes every Runic tool shares: 0 success, 1 the command failed or what it needs is unavailable,
/// 2 usage or validation, 3 internal or host failure, 130 cancelled.
/// </summary>
internal sealed class ToolExitCodePolicy : IExitCodePolicy
{
    internal static ToolExitCodePolicy Instance { get; } = new();
    public int GetExitCode(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Success => Program.Success,
        CommandExitCategory.Usage or CommandExitCategory.Validation => Program.UsageFailure,
        CommandExitCategory.CommandFailure or CommandExitCategory.Unavailable => Program.DevelopmentFailure,
        CommandExitCategory.Cancelled => Program.Cancelled,
        _ => Program.InternalFailure,
    };
}

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
