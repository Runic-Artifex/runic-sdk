using Runic.CommandLine;
using Runic.CommandLine.Generated;

namespace Runic.Application.Logging.Tests;

// The application started by a Runic.CommandLine command (#301), as an application with a `ui` command starts its
// Window: the command does its asynchronous work, then hands RunicDesktopHost.Run back to Main with
// ContinueOnCallingThread, and CommandApp.Run runs it on the thread that called Run, the process main thread. The
// continuation writes MainThreadLine and whether it runs on that thread, which ServeOnlyApplicationTests checks.
internal static class CommandLineApplication
{
    internal const string Command = "ui";
    internal const string MainThreadLine = "MAIN-THREAD";

    private static int s_mainThread;

    // Called from Main before any await.
    public static int Run(string[] args)
    {
        s_mainThread = Environment.CurrentManagedThreadId;
        return new CommandApp(GeneratedCommandCatalog.Create()) { Name = "logging-application" }.Run(args);
    }

    [Command(Command, Description = "Open the application's Window.")]
    internal static async Task Ui(CommandExecutionContext context, CancellationToken cancellationToken = default)
    {
        // Work before the host starts, such as loading settings, runs asynchronously; the command continues on
        // another thread, where the Desktop host must not start.
        await Task.Delay(1, cancellationToken);
        context.ContinueOnCallingThread(static () =>
        {
            Console.Out.WriteLine($"{MainThreadLine}\t{Environment.CurrentManagedThreadId == s_mainThread}");
            return LoggingApplication.Run();
        });
    }
}
