using System;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Testing;

/// <summary>Runs the real application pipeline with isolated, captured IO.</summary>
public sealed class CommandAppTester
{
    private readonly Func<TestCommandConsole, CommandApp> _createApp;
    /// <summary>Initializes an application factory. Set deterministic ParseSettings in the factory.</summary>
    public CommandAppTester(Func<TestCommandConsole, CommandApp> createApp) => _createApp = createApp ?? throw new ArgumentNullException(nameof(createApp));
    /// <summary>Runs one invocation with a fresh console.</summary>
    public async Task<CommandAppTestResult> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var console = new TestCommandConsole();
        int exitCode = await _createApp(console).RunAsync(args, cancellationToken).ConfigureAwait(false);
        return new CommandAppTestResult(exitCode, console.StandardOutput, console.StandardError);
    }
}

/// <summary>Captured process-facing application behavior.</summary>
public sealed record CommandAppTestResult(int ExitCode, string StandardOutput, string StandardError);
