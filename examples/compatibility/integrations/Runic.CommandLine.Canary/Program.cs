using System;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Processes;

if (!string.Equals(CliProtocol.Identity, "runic.commandline/1", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected protocol identity: {CliProtocol.Identity}");
}

CommandCatalog catalog = new CommandCatalogBuilder().Build();
var adapter = new CommandLineHostingAdapter(
    catalog,
    new CommandExecutor(EmptyScopeFactory.Instance));
HostedCommandLineDecision decision = adapter.Classify(new HostedCommandLineLaunchInput(
    Array.Empty<string>(),
    emptyInputFallback: EmptyInputFallback.UserInterface));

if (decision.Kind != HostedCommandLineDecisionKind.UserInterface)
{
    throw new InvalidOperationException($"Unexpected host decision: {decision.Kind}");
}

string executable;
string[] arguments;
if (OperatingSystem.IsWindows())
{
    executable = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    arguments = ["/d", "/c", "exit", "0"];
}
else
{
    executable = "/bin/sh";
    arguments = ["-c", "exit 0"];
}

var runner = new ProcessRunner(new LocalExecutablePolicy());
ProcessResult process = await runner.RunAsync(new ProcessRequest(
    executable,
    arguments,
    options: new ProcessExecutionOptions(
        timeout: TimeSpan.FromSeconds(10),
        standardOutputLimitBytes: 4096,
        standardErrorLimitBytes: 4096)));

if (process.State != ProcessState.Exited || process.ExitCode != 0)
{
    throw new InvalidOperationException(
        $"Unexpected child-process result: {process.State}, exit {process.ExitCode}");
}

Console.WriteLine(
    $"{CliProtocol.Identity}: package-only managed and NativeAOT canary passed.");

internal sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
{
    internal static EmptyScopeFactory Instance { get; } = new();

    private EmptyScopeFactory()
    {
    }

    public ICommandExecutionScope CreateScope() => new EmptyScope();

    private sealed class EmptyScope : ICommandExecutionScope, IServiceProvider
    {
        public IServiceProvider Services => this;

        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
