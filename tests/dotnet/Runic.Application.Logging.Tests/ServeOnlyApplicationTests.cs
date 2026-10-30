using System.Diagnostics;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Logging.Tests;

// RunicServeOnlyApplication (#240) against this suite's real application: RunicDesktopHost.Run, a generated Bridge,
// the recommended ValidateWindow check at startup (#238) and www/index.html, started as its own process without a
// display. Each case starts its own process, so the cases may run in parallel.
internal sealed class ServeOnlyApplicationTests
{
    private const string Page = "Served without a window (RUNIC_APPLICATION_SERVE_ONLY=1).";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Test]
    public async Task StartsServesAndStopsWithoutLeavingProcesses()
    {
        var start = StartInfo();
        start.Environment[LoggingApplication.ChildProcessVariable] = "end";
        var environment = new Dictionary<string, string?>(start.Environment);
        await using var application = await RunicServeOnlyApplication.StartAsync(start, Timeout);
        Require(!start.RedirectStandardInput && start.Environment.Count == environment.Count &&
            environment.All(variable => start.Environment.TryGetValue(variable.Key, out var value) && value == variable.Value),
            "StartAsync changed the caller's start information.", application);

        Require(application.Url is { IsLoopback: true, Scheme: "http" or "https" },
            $"The application served an unexpected URL: {application.Url}.", application);
        await LoadPageAsync(application);
        var descendants = await WaitForDescendantsAsync(application);
        try
        {
            var exitCode = await application.StopAsync(Timeout);
            Require(exitCode == 0, $"The application exited with {exitCode}.", application);
            Require(await application.StopAsync(TimeSpan.FromSeconds(1)) == exitCode, "A second StopAsync returned another exit code.", application);
            Require(await AllExitedAsync(descendants),
                $"Processes outlived the application: {Describe(descendants.Where(process => !process.HasExited()))}.", application);
        }
        finally { Release(descendants); }
    }

    // #301: a Runic.CommandLine command starts the application. RunicDesktopHost.Run, handed back to Main with
    // ContinueOnCallingThread, runs on the process main thread after the command's asynchronous work, and the Window
    // is served as when Main starts the host itself.
    [Test]
    public async Task StartsFromACommandOnTheMainThread()
    {
        await using var application = await RunicServeOnlyApplication.StartAsync(
            LoggingApplication.SelfStartInfo(CommandLineApplication.Command), Timeout);
        await LoadPageAsync(application);
        var exitCode = await application.StopAsync(Timeout);
        Require(exitCode == 0, $"The application exited with {exitCode}.", application);
        Require(application.StandardOutput.ReplaceLineEndings("\n").Split('\n').Contains($"{CommandLineApplication.MainThreadLine}\tTrue"),
            "The command's continuation did not run on the process main thread.", application);
    }

    // The listing is what lets a test find a process that outlives its application.
    [Test]
    public async Task ReportsADescendantThatOutlivesTheApplication()
    {
        var start = StartInfo();
        start.Environment[LoggingApplication.ChildProcessVariable] = "leak";
        await using var application = await RunicServeOnlyApplication.StartAsync(start, Timeout);
        var descendants = await WaitForDescendantsAsync(application);
        try
        {
            Require(await application.StopAsync(Timeout) == 0, "The application did not exit cleanly.", application);
            Require(!await AllExitedAsync(descendants, TimeSpan.FromSeconds(1)),
                "The child process the application leaves running was reported as ended.", application);
        }
        finally { Release(descendants); }
    }

    [Test]
    public async Task ReportsAnApplicationThatExitsWithoutServing()
    {
        // The test application lists its cases and exits.
        var start = LoggingApplication.SelfStartInfo("--list-tests");
        try
        {
            await using var application = await RunicServeOnlyApplication.StartAsync(start, Timeout);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("before it wrote RUNIC_APPLICATION_URL=", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("StartAsync did not report an application that exited without serving.");
    }

    // The kill path: a process that never writes its URL is killed when the wait ends, and nothing is left.
    [Test]
    public async Task KillsAnApplicationThatDoesNotServeInTime()
    {
        var start = LoggingApplication.SelfStartInfo(LoggingApplication.ChildProcessArgument);
        try
        {
            await using var application = await RunicServeOnlyApplication.StartAsync(start, TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException error) when (error.Message.Contains("it was killed", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("StartAsync did not time out for an application that never serves.");
    }

    private static ProcessStartInfo StartInfo() => LoggingApplication.SelfStartInfo(LoggingApplication.Argument);

    // A real browser loads the page when the development shell provides Chromium, as the repository's other
    // browser tests do; elsewhere an HTTP request checks it.
    private static async Task LoadPageAsync(RunicServeOnlyApplication application)
    {
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH") is not { Length: > 0 } chromium)
        {
            using var client = new HttpClient();
            var html = await client.GetStringAsync(application.Url);
            Require(html.Contains(Page, StringComparison.Ordinal), $"The page was not served: {html}", application);
            return;
        }
        var profile = Directory.CreateTempSubdirectory("runic-serve-only-browser-");
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(chromium,
                ["--headless", "--no-sandbox", "--disable-gpu", "--no-first-run", "--disable-crash-reporter", "--disable-breakpad", $"--user-data-dir={profile.FullName}",
                    "--dump-dom", application.Url.ToString()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Chromium did not start.");
            using var timeout = TestTimeouts.Cancellation(Timeout);
            var errors = browser.StandardError.ReadToEndAsync(timeout.Token);
            string dom;
            try
            {
                dom = await browser.StandardOutput.ReadToEndAsync(timeout.Token);
                await browser.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                if (!browser.HasExited) browser.Kill(entireProcessTree: true);
            }
            Require(dom.Contains(Page, StringComparison.Ordinal),
                $"Chromium did not load the page. DOM:\n{dom}\nChromium:\n{await errors}", application);
        }
        finally
        {
            // Best effort: a browser process that has not released the profile yet must not fail the case.
            try { profile.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The application starts its child process before it opens its window, so the child is running by now; the
    // wait covers a slow process listing.
    private static async Task<IReadOnlyList<RunicDescendantProcess>> WaitForDescendantsAsync(RunicServeOnlyApplication application)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Timeout);
        while (true)
        {
            var descendants = application.GetDescendantProcesses();
            if (descendants.Count > 0) return descendants;
            if (DateTime.UtcNow > deadline) break;
            await Task.Delay(100);
        }
        Require(false, "The application's child process was not listed.", application);
        return [];
    }

    private static async Task<bool> AllExitedAsync(IReadOnlyList<RunicDescendantProcess> processes, TimeSpan? wait = null)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(wait ?? TimeSpan.FromSeconds(10));
        while (processes.Any(process => !process.HasExited()))
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }
        return true;
    }

    // Kills what the application left running; HasExited() first rules out a reused process id.
    private static void Release(IReadOnlyList<RunicDescendantProcess> processes)
    {
        foreach (var descendant in processes.Where(process => !process.HasExited()))
        {
            try
            {
                using var process = Process.GetProcessById(descendant.Id);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static string Describe(IEnumerable<RunicDescendantProcess> processes) => string.Join(", ", processes);

    private static void Require(bool condition, string message, RunicServeOnlyApplication application)
    {
        if (!condition)
            throw new InvalidOperationException(
                $"{message}\nStandard output:\n{application.StandardOutput}\nStandard error:\n{application.StandardError}");
    }
}
