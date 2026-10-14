using System.Diagnostics;
using System.Reflection;
using TUnit.Core;

namespace Runic.Application.Logging.Tests;

// An application that registers Serilog through AddLogging and starts with RunicDesktopHost.Run receives the
// Desktop host's messages as well as the Views' ones, and none of them goes to System.Diagnostics.Trace (#249).
// The application runs as its own process, headless in serve-only mode, as the smoke applications do, so the
// shutdown is requested with a real SIGTERM.
internal sealed class SerilogApplicationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task DesktopAndViewsMessagesReachSerilogAndNotTrace()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var application = Start();
        try { await RunAsync(application, timeout.Token); }
        finally
        {
            if (!application.HasExited) application.Kill(entireProcessTree: true);
        }
    }

    private static async Task RunAsync(Process application, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        var errors = Task.Run(() => application.StandardError.ReadToEndAsync(cancellationToken));
        string? line;
        while ((line = await application.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
        {
            output.Add(line);
            if (line.StartsWith("RUNIC_APPLICATION_URL=", StringComparison.Ordinal)) break;
        }
        if (line is null) Require(false, "The application ended before serving its window.", output, await errors);

        // The window is serving: request shutdown as a terminal or service manager does, or end serving with an
        // input line where there are no signals to send.
        if (OperatingSystem.IsWindows()) await application.StandardInput.WriteLineAsync();
        else
        {
            using var kill = Process.Start("kill", ["-TERM", application.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            await kill.WaitForExitAsync(cancellationToken);
        }
        while ((line = await application.StandardOutput.ReadLineAsync(cancellationToken)) is not null) output.Add(line);
        await application.WaitForExitAsync(cancellationToken);
        var standardError = await errors;
        Require(application.ExitCode == 0, $"The application exited with {application.ExitCode}.", output, standardError);

        var logged = output.Where(entry => entry.StartsWith("SERILOG\t", StringComparison.Ordinal))
            .Select(entry => entry.Split('\t')).ToList();
        var traced = output.Where(entry => entry.StartsWith("TRACE\t", StringComparison.Ordinal)).ToList();

        // Views: the guarded Window opened on a surface that cannot keep it open.
        RequireEvent(logged, "Runic.Application.Views", "Warning", 1053, "WindowCloseGuardUnavailable", null, output, standardError);
        // Desktop host: the configuration check at startup, through the application's logger factory.
        RequireEvent(logged, "Runic.Desktop", "Warning", 3003, "DesktopConfigurationLimited", "window-option-incomplete", output, standardError);
        // Desktop host: the shutdown that SIGTERM requested.
        if (!OperatingSystem.IsWindows())
            RequireEvent(logged, "Runic.Desktop", "Information", 3006, "DesktopShutdownRequested", null, output, standardError);

        string[] messages = ["guards its close", "window-option-incomplete", "closing Desktop windows"];
        var leaked = traced.Where(entry => messages.Any(message => entry.Contains(message, StringComparison.Ordinal))).ToList();
        Require(leaked.Count == 0, $"Runic wrote to Trace although Serilog was registered: {string.Join(" | ", leaked)}", output, standardError);
    }

    private static Process Start()
    {
        // The test executable, or dotnet and the test assembly when the suite runs through dotnet exec.
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The test process has no path.");
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(LoggingApplication.Argument);
        start.Environment["RUNIC_APPLICATION_SERVE_ONLY"] = "1";
        start.Environment.Remove("RUNIC_APPLICATION_CLOSE_AFTER_OPEN");
        return Process.Start(start) ?? throw new InvalidOperationException("The application did not start.");
    }

    private static void RequireEvent(List<string[]> logged, string category, string level, int id, string name, string? code,
        List<string> output, string standardError)
    {
        var found = logged.Any(entry => entry.Length >= 7 && entry[1] == category && entry[2] == level
            && entry[3] == id.ToString(System.Globalization.CultureInfo.InvariantCulture) && entry[4] == name
            && (code is null || entry[5] == code));
        Require(found, $"Serilog did not receive {category} {level} event {id} ({name}{(code is null ? "" : $", {code}")}).",
            output, standardError);
    }

    private static void Require(bool condition, string message, List<string> output, string standardError)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"{message}\nStandard output:\n{string.Join('\n', output)}\nStandard error:\n{standardError}");
    }
}
