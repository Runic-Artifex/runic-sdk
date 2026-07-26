using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.CommandLine.Processes;

namespace WebUIToolkit.CommandLine.Samples.Kernel;

internal static class Program
{
    private const string ChildProbeArgument = "--sample-child-probe";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], ChildProbeArgument, StringComparison.Ordinal))
        {
            Console.Out.WriteLine("kernel-child-ok");
            return 0;
        }

        CommandCatalog catalog = CreateCatalog();
        ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            args,
            new ParseSettings(Environment.GetEnvironmentVariable(
                CommandOutputClassifier.EnvironmentVariableName)));

        if (parse.Kind == ParseOutcomeKind.Help)
        {
            await Console.Out.WriteAsync(UsageText).ConfigureAwait(false);
            return CommandExitCodes.Success;
        }

        if (parse.Kind == ParseOutcomeKind.Version)
        {
            await Console.Out.WriteLineAsync(CliProtocol.Identity).ConfigureAwait(false);
            return CommandExitCodes.Success;
        }

        if (parse.Kind != ParseOutcomeKind.Invocation || parse.Invocation is null)
        {
            foreach (CommandDiagnostic diagnostic in parse.Diagnostics)
            {
                await Console.Error.WriteLineAsync(
                    string.Create(CultureInfo.InvariantCulture, $"{diagnostic.Code}: {diagnostic.Message}"))
                    .ConfigureAwait(false);
            }

            await Console.Error.WriteAsync(UsageText).ConfigureAwait(false);
            return CommandExitCodes.Usage;
        }

        var console = new SystemCommandConsole();
        var request = new CommandExecutionRequest(
            parse.Invocation,
            console,
            CultureInfo.CurrentCulture,
            string.Create(CultureInfo.InvariantCulture, $"sample-{Environment.ProcessId}"));
        var executor = new CommandExecutor(SampleScopeFactory.Instance);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            CommandExecutionResult result = await executor.ExecuteAsync(
                request,
                new CommandOutputDispatcher(),
                cancellation.Token).ConfigureAwait(false);
            return result.ExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static CommandCatalog CreateCatalog() => new CommandCatalogBuilder()
        .Command<ProbeOptions, ProbeHandler, ProbeResult>(
            "probe",
            command => command
                .Describe("sample.probe.description")
                .Option(
                    "label",
                    "--label",
                    CommandArity.ExactlyOne,
                    descriptionKey: "sample.probe.label",
                    aliases: "-l")
                .BindWith(ProbeOptionsBinder.Instance)
                .CreateHandlerWith(ProbeHandlerFactory.Instance)
                .Produces(ProbeResultCodec.Instance))
        .Build();

    private const string UsageText = """
        Usage: CommandLine.Kernel probe [--label <text>] [--output human|json]
               CommandLine.Kernel --help
               CommandLine.Kernel --version

        WEBUITOOLKIT_CLI_OUTPUT may select human or json when --output is absent.
        """;
}

internal sealed record ProbeOptions(string Label);

internal sealed record ProbeResult(
    string Label,
    string State,
    int? ChildExitCode,
    string StandardOutput,
    bool StandardOutputTruncated);

internal sealed class ProbeOptionsBinder : ICommandOptionsBinder<ProbeOptions>
{
    internal static ProbeOptionsBinder Instance { get; } = new();

    private ProbeOptionsBinder()
    {
    }

    public ValueTask<CommandOutcome<ProbeOptions>> BindAsync(
        ParsedInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string label = "kernel";
        foreach (CommandValueBinding option in invocation.Options)
        {
            if (string.Equals(option.Id, "label", StringComparison.Ordinal))
            {
                label = option.Values[0];
            }
        }

        if (label.Length > 64)
        {
            return ValueTask.FromResult(CommandOutcome.Failure<ProbeOptions>(
                CommandExitCategory.Validation,
                new CommandFault("SAMPLE_LABEL_TOO_LONG", "The label cannot exceed 64 characters.")));
        }

        return ValueTask.FromResult(CommandOutcome.Success(new ProbeOptions(label)));
    }
}

internal sealed class ProbeHandlerFactory : ICommandHandlerFactory<ProbeHandler>
{
    internal static ProbeHandlerFactory Instance { get; } = new();

    private ProbeHandlerFactory()
    {
    }

    public ProbeHandler Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new ProbeHandler((IProcessRunner)(services.GetService(typeof(IProcessRunner)) ??
            throw new InvalidOperationException("The invocation scope has no process runner.")));
    }
}

internal sealed class ProbeHandler : ICommandHandler<ProbeOptions, ProbeResult>
{
    private readonly IProcessRunner processRunner;

    internal ProbeHandler(IProcessRunner processRunner)
    {
        this.processRunner = processRunner;
    }

    public async ValueTask<CommandOutcome<ProbeResult>> ExecuteAsync(
        ProbeOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        ProcessResult child = await processRunner.RunAsync(
            SelfProbe.CreateRequest(),
            cancellationToken).ConfigureAwait(false);
        if (child.State != ProcessState.Exited || child.ExitCode != 0)
        {
            return CommandOutcome.Failure<ProbeResult>(
                CommandExitCategory.CommandFailure,
                child.Fault ?? new CommandFault(
                    "SAMPLE_CHILD_FAILED",
                    "The bounded child probe did not complete successfully."));
        }

        return CommandOutcome.Success(new ProbeResult(
            options.Label,
            child.State.ToString(),
            child.ExitCode,
            child.StandardOutput.Text.TrimEnd(),
            child.StandardOutput.IsTruncated));
    }
}

internal static class SelfProbe
{
    private static readonly string Executable = Environment.ProcessPath ??
        throw new InvalidOperationException("The current executable path is unavailable.");

    internal static ProcessRequest CreateRequest()
    {
        IReadOnlyList<string> arguments = IsDotnetHost(Executable)
            ? [Path.Combine(AppContext.BaseDirectory, "CommandLine.Kernel.dll"), "--sample-child-probe"]
            : ["--sample-child-probe"];
        return new ProcessRequest(
            Executable,
            arguments,
            options: new ProcessExecutionOptions(
                timeout: TimeSpan.FromSeconds(5),
                standardOutputLimitBytes: 4096,
                standardErrorLimitBytes: 4096,
                drainGracePeriod: TimeSpan.FromSeconds(2)));
    }

    internal static IExecutablePolicy CreatePolicy() => new ExactExecutablePolicy(Executable);

    private static bool IsDotnetHost(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
}

internal sealed class ExactExecutablePolicy : IExecutablePolicy
{
    private readonly string executable;

    internal ExactExecutablePolicy(string executable)
    {
        this.executable = Path.GetFullPath(executable);
    }

    public ExecutablePolicyDecision Evaluate(ProcessRequest request)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(request.FileName), executable, comparison)
            ? ExecutablePolicyDecision.Allow()
            : ExecutablePolicyDecision.Reject(
                "SAMPLE_EXECUTABLE_REJECTED",
                "Only this sample's own process image may be started.");
    }
}

internal sealed class ProbeResultCodec : ICommandResultCodec<ProbeResult>
{
    internal static ProbeResultCodec Instance { get; } = new();

    private ProbeResultCodec()
    {
    }

    public string PayloadType => "webuitoolkit.cli.sample.probe/1";

    public JsonTypeInfo<ProbeResult> TypeInfo => SampleJsonContext.Default.ProbeResult;

    public ValueTask WriteHumanAsync(
        ProbeResult value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken) => console.WriteOutAsync(
            string.Create(
                culture,
                $"{value.Label}: child {value.State} ({value.ChildExitCode}); {value.StandardOutput}\n").AsMemory(),
            cancellationToken);
}

internal sealed class SampleScopeFactory : ICommandExecutionScopeFactory
{
    internal static SampleScopeFactory Instance { get; } = new();

    private SampleScopeFactory()
    {
    }

    public ICommandExecutionScope CreateScope() => new SampleScope();

    private sealed class SampleScope : ICommandExecutionScope, IServiceProvider
    {
        private readonly IProcessRunner processRunner = new ProcessRunner(SelfProbe.CreatePolicy());

        public IServiceProvider Services => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IProcessRunner) ? processRunner : null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class SystemCommandConsole : ICommandConsole
{
    public bool IsInteractive =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;

    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public bool IsErrorRedirected => Console.IsErrorRedirected;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        Console.In.ReadLineAsync(cancellationToken);

    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        new(Console.Out.WriteAsync(value, cancellationToken));

    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        Console.OpenStandardOutput().WriteAsync(value, cancellationToken);

    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        new(Console.Error.WriteAsync(value, cancellationToken));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProbeResult))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;
