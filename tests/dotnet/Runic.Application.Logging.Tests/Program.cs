using Microsoft.Testing.Platform.Builder;
using Runic.Application.Logging.Tests;

/// <summary>
/// The entry point is hand-written because <see cref="SerilogApplicationTests"/> starts this executable as the
/// application it checks: <c>--run-logging-application</c> runs <see cref="LoggingApplication"/> instead of the tests.
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
