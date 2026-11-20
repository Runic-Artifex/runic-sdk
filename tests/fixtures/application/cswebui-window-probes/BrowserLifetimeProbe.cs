using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace CsWebUiWindowProbes;

// sdk#1263, --probe-browser-lifetime: the browser a Development CS-WebUI window started with the run's own profile ends with
// the application, also when the application is killed (TerminateProcess, SIGKILL; a debugger's Stop), and a browser the
// user started is left alone. The probe runs itself twice as the application (--child):
// 1. The first child opens a window with a DevTools Protocol endpoint and is killed. Its browser must end, its endpoint's
//    port must be free, and on Linux and macOS its profile must be deleted. A browser the probe started meanwhile with a
//    profile of its own (the user's) must still run.
// 2. The second child opens a window and exits normally: its browser must end and its profile be deleted, and the first
//    child's profile, if Windows left it, must have been deleted by this run.
// Needs an untrimmed build (the endpoint is off in a trimmed one) and a Chromium-based browser: CSWEBUI_BROWSER_PATH's or
// WebUI's choice; otherwise it reports itself skipped.
internal static class BrowserLifetimeProbe
{
    private const string Marker = "PROBE_BROWSER ";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);

    internal static int Run(string[] args)
    {
        if (args.Contains("--child")) return RunChild(args[^1], exitNormally: args.Contains("--exit"));
        if (AppContext.TryGetSwitch("Runic.Desktop.WebUiRemoteDebugging", out var enabled) && !enabled)
        {
            Console.WriteLine("CSWEBUI_BROWSER_LIFETIME_SKIPPED: a trimmed build opens no DevTools Protocol endpoint");
            return 0;
        }
        new Thread(static () =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(4));
            Console.Error.WriteLine("The browser lifetime probe did not finish within 4 minutes.");
            Environment.Exit(3);
        })
        { IsBackground = true }.Start();

        // The windows' content, which a killed child cannot delete.
        var root = Directory.CreateTempSubdirectory("runic-cswebui-lifetime-").FullName;
        File.WriteAllText(Path.Combine(root, "index.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Probe</title><script src=\"/webui.js\"></script></head><body>Probe</body></html>");
        try { return RunParent(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static int RunParent(string root)
    {
        // 1. Killed.
        using var first = StartChild(root, exit: false);
        var report = ReadReport(first);
        if (report is null)
        {
            first.Kill();
            Console.WriteLine("CSWEBUI_BROWSER_LIFETIME_SKIPPED: the browser is not Chromium-based");
            return 0;
        }
        using var browser = Process.GetProcessById(report.Pid);
        if (browser.HasExited) throw new InvalidOperationException($"The window's browser {report.Pid} is not running.");
        var userProfile = Directory.CreateTempSubdirectory("runic-probe-user-browser-").FullName;
        using var userBrowser = StartUserBrowser(report.Executable, userProfile);
        try
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            if (userBrowser.HasExited) throw new InvalidOperationException($"The user's browser ({report.Executable}) exited at once with {userBrowser.ExitCode}.");
            first.Kill();
            first.WaitForExit();
            if (!browser.WaitForExit(Limit))
                throw new InvalidOperationException($"The killed application's browser {report.Pid} still runs after {Limit.TotalSeconds} seconds.");
            WaitUntil(() => !Listening(report.Port), $"port {report.Port} is still listening after the browser ended");
            if (!OperatingSystem.IsWindows())
                WaitUntil(() => !Directory.Exists(report.Profile), $"the killed application's profile {report.Profile} was not deleted");
            if (userBrowser.HasExited) throw new InvalidOperationException("The user's browser ended with the application.");
            Console.WriteLine($"killed: browser {report.Pid} ended, port {report.Port} free, profile {(Directory.Exists(report.Profile) ? "left for the next run" : "deleted")}, the user's browser {userBrowser.Id} runs");

            // 2. Exits normally.
            using var second = StartChild(root, exit: true);
            var secondReport = ReadReport(second) ?? throw new InvalidOperationException("The second child reported no browser.");
            using var secondBrowser = Process.GetProcessById(secondReport.Pid);
            if (!second.WaitForExit(Limit)) throw new InvalidOperationException("The second child did not exit.");
            if (second.ExitCode != 0) throw new InvalidOperationException($"The second child exited with {second.ExitCode}.");
            if (!secondBrowser.WaitForExit(Limit))
                throw new InvalidOperationException($"The browser {secondReport.Pid} of the application that exited still runs.");
            WaitUntil(() => !Directory.Exists(secondReport.Profile), $"the profile {secondReport.Profile} of the application that exited was not deleted");
            WaitUntil(() => !Directory.Exists(report.Profile), $"the killed application's profile {report.Profile} was not deleted by the next run");
            if (userBrowser.HasExited) throw new InvalidOperationException("The user's browser ended with the second application.");
            Console.WriteLine($"exited: browser {secondReport.Pid} ended, both profiles deleted, the user's browser {userBrowser.Id} runs");
        }
        finally
        {
            if (!userBrowser.HasExited) userBrowser.Kill(entireProcessTree: true);
            userBrowser.WaitForExit();
            for (var attempt = 0; attempt < 20 && Directory.Exists(userProfile); attempt++)
            {
                try { Directory.Delete(userProfile, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
            }
        }
        Console.WriteLine("CSWEBUI_BROWSER_LIFETIME_OK");
        return 0;
    }

    private sealed record Report(int Pid, int Port, string Profile, string Executable);

    private static Process StartChild(string root, bool exit)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, UseShellExecute = false };
        // A JIT build runs as dotnet <probe>.dll.
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CsWebUiWindowProbes.dll"));
        start.ArgumentList.Add("--probe-browser-lifetime");
        start.ArgumentList.Add("--child");
        if (exit) start.ArgumentList.Add("--exit");
        start.ArgumentList.Add(root);
        return Process.Start(start) ?? throw new InvalidOperationException("The child did not start.");
    }

    // The child's report, or null when its browser is not Chromium-based.
    private static Report? ReadReport(Process child)
    {
        var line = Task.Run(() =>
        {
            while (child.StandardOutput.ReadLine() is { } output)
            {
                Console.WriteLine($"  child: {output}");
                if (output.StartsWith(Marker, StringComparison.Ordinal) || output.StartsWith("CSWEBUI_BROWSER_LIFETIME_SKIPPED", StringComparison.Ordinal))
                    return output;
            }
            return null;
        });
        if (!line.Wait(Limit + Limit) || line.Result is null)
            throw new InvalidOperationException("The child reported no browser.");
        // Keep draining its output.
        _ = Task.Run(() => { while (child.StandardOutput.ReadLine() is { } output) Console.WriteLine($"  child: {output}"); });
        if (!line.Result.StartsWith(Marker, StringComparison.Ordinal)) return null;
        var fields = line.Result[Marker.Length..].Split('\t');
        return new Report(int.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture), fields[2], fields[3]);
    }

    // A browser the user started: the same browser, with a profile of its own.
    private static Process StartUserBrowser(string executable, string profile)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[] { "--headless=new", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check", "about:blank" })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("The user's browser did not start.");
    }

    private static bool Listening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"After {Limit.TotalSeconds} seconds, {failure}.");
            Thread.Sleep(200);
        }
    }

    // The application: a Development window with a DevTools Protocol endpoint, reported as PROBE_BROWSER <pid> <port>
    // <profile> <browser executable>; then it waits to be killed, or with --exit closes the window and exits.
    private static int RunChild(string root, bool exitNormally)
    {
        Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("RUNIC_WEBUI_REMOTE_DEBUGGING_PORT", "0");
        var startedAt = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        var output = new DebugEndpointProbe.LineCapture(Console.Out);
        Console.SetOut(output);
        var services = new ServiceCollection();
        services.AddScoped<ProbeViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return RunicCsWebUiHost.Run(provider, async host =>
        {
            var window = await host.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { ContentRoot = root, Width = 640, Height = 480 });
            var native = CsWebUiPlatformServices.GetWebUiWindow(window) ?? throw new InvalidOperationException("The Window has no WebUI window.");
            if (Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable) is null
                && native.BestBrowser is not (WebUiBrowser.Chrome or WebUiBrowser.Edge or WebUiBrowser.Chromium or WebUiBrowser.Brave
                    or WebUiBrowser.Vivaldi or WebUiBrowser.Epic or WebUiBrowser.Yandex))
            {
                output.Inner.WriteLine("CSWEBUI_BROWSER_LIFETIME_SKIPPED");
                await window.DisposeAsync();
                return 0;
            }
            string? endpoint;
            var deadline = DateTime.UtcNow + Limit;
            while ((endpoint = output.Find("RUNIC_APPLICATION_DEBUG_ENDPOINT=")) is null)
            {
                if (DateTime.UtcNow > deadline) throw new InvalidOperationException("No RUNIC_APPLICATION_DEBUG_ENDPOINT line. Output:\n" + output);
                await Task.Delay(100);
            }
            var port = new Uri(endpoint["RUNIC_APPLICATION_DEBUG_ENDPOINT=chromium cdp ".Length..].Trim()).Port;
            var profile = new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories("runic-cswebui-devtools-*")
                .Where(directory => directory.CreationTimeUtc >= startedAt && File.Exists(Path.Combine(directory.FullName, "Local State")))
                .OrderByDescending(static directory => directory.CreationTimeUtc).FirstOrDefault()?.FullName
                ?? throw new InvalidOperationException("The browser runs in no runic-cswebui-devtools-* profile of this run.");
            var pid = (int)native.BrowserProcessId;
            if (pid == 0) throw new InvalidOperationException("WebUI reports no browser process for the window.");
            var executable = Environment.GetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable) is { Length: > 0 } configured
                ? configured
                : Process.GetProcessById(pid).MainModule?.FileName ?? throw new InvalidOperationException("The browser's executable is unknown.");
            output.Inner.WriteLine($"{Marker}{pid}\t{port}\t{profile}\t{executable}");
            output.Inner.Flush();
            if (!exitNormally) await Task.Delay(Timeout.Infinite);
            await window.DisposeAsync();
            return 0;
        });
    }
}
