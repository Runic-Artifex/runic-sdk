using Microsoft.Testing.Platform.Builder;
using Runic.Application.Logging.Tests;

/// <summary>
/// The entry point is hand-written because <see cref="SerilogApplicationTests"/> starts this executable as the
/// application it checks: <c>--run-logging-application</c> runs <see cref="LoggingApplication"/> instead of the tests,
/// and <c>ui</c> runs it through <see cref="CommandLineApplication"/>'s command.
/// Every other invocation is the TUnit test application.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The application mode needs its own Main; TUnit's generated entry point is disabled.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        // Before any await: RunicDesktopHost.Run needs the process main thread on macOS.
        if (args is [LoggingApplication.Argument]) return LoggingApplication.Run();
        // The same application started by a Runic.CommandLine command, which hands the host back to this thread.
        if (args is [CommandLineApplication.Command, ..]) return CommandLineApplication.Run(args);
        // Hosted mode, reached through the parameter this application defines for it, and a guest without the nonce.
        if (args is [HostedApplication.Argument]) return HostedApplication.Run();
        if (args is [FakeGuest.Argument]) return FakeGuest.Run();
        if (args is [FakeGuest.MismatchArgument]) return FakeGuest.Run(mismatch: true);
        // The application's child process: it runs until it is killed.
        if (args is [LoggingApplication.ChildProcessArgument])
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
