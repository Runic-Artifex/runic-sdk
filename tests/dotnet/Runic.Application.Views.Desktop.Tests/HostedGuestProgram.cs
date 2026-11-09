using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

// This suite's executable as a guest application in hosted mode (runic-host/1), for the host's cases
// (HostedApplicationTests): a real guest process with its pipes, its channel listener and its surfaces. The Windows'
// pages are not loaded, since the host's windows are headless. Each mode is one behaviour of the guest application.
internal static class HostedGuestProgram
{
    internal const string Argument = "--hosted-guest";
    internal const string PuppetArgument = "--puppet-guest";
    internal const string PuppetPortVariable = "RUNIC_TEST_PUPPET_PORT";
    internal const string ApplicationId = "dev.runic.tests.hosted-guest";
    internal const int StartFailedExitCode = 3;
    internal const int RefusedExitCode = 5;
    internal const int CrashExitCode = 7;

    // Starts this test executable, or dotnet and the test assembly when the suite runs through dotnet exec.
    internal static RunicGuestStartInfo StartInfo(params string[] arguments)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("The test process has no path.");
        var all = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            all.Add(typeof(HostedGuestProgram).Assembly.Location);
        all.AddRange(arguments);
        return new RunicGuestStartInfo(path, all) { WorkingDirectory = AppContext.BaseDirectory };
    }

    internal static RunicGuestStartInfo Guest(string mode) => StartInfo(Argument, mode);

    public static int Run(string mode)
    {
        if (mode == "exit") return StartFailedExitCode;
        if (mode == "sleep")
        {
            // Reads no offer and writes no line.
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        using var provider = services.BuildServiceProvider();
        // The guarded Window refuses every close its host asks for; a forced close skips it.
        ConformanceGuardedWindow.Decide(mode != "guarded");
        try
        {
            return RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
                new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                    Guest = RunicGuestOptions.FromStandardInput() with
                    {
                        ApplicationName = "Hosted guest",
                        ApplicationVersion = "1.0.0",
                        ApplicationId = ApplicationId,
                    },
                },
                host => BodyAsync(host, mode));
        }
        catch (RunicHostedApplicationException error)
        {
            Console.Error.WriteLine($"Hosted mode did not start ({error.Code}): {error.Message}");
            return StartFailedExitCode;
        }
    }

    private static async Task<int> BodyAsync(RunicDesktopHost host, string mode)
    {
        switch (mode)
        {
            case "main":
            {
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                await window.WaitForCloseAsync();
                return 0;
            }
            case "guarded":
            {
                await using var window = await host.OpenWindowAsync<ConformanceGuardedWindow>();
                await window.WaitForCloseAsync();
                return 0;
            }
            case "crash":
            {
                _ = await host.OpenWindowAsync<ConformanceMainWindow>();
                // Long enough for the host to see the window; then the process ends without closing it.
                await Task.Delay(TimeSpan.FromSeconds(1));
                Console.Error.WriteLine("The guest crashes.");
                Environment.Exit(CrashExitCode);
                return 0;
            }
            case "owned":
            {
                await using var main = await host.OpenWindowAsync<ConformanceMainWindow>();
                await using var owned = await host.OpenWindowAsync<ConformanceMainWindow>(
                    new RunicWindowOptions { Owner = main, Modality = RunicWindowModality.Window }, null);
                await main.WaitForCloseAsync();
                return 0;
            }
            case "refused":
            {
                try
                {
                    await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                    return 0;
                }
                catch (RunicWindowConfigurationException error)
                {
                    Console.Error.WriteLine("OPEN " + string.Join(",", error.Diagnostics.Select(static diagnostic => diagnostic.Code)));
                    return RefusedExitCode;
                }
            }
            case "present":
            {
                await using var window = await host.OpenWindowAsync<ConformanceMainWindow>();
                var presentation = ((IRunicWindow)window).Presentation;
                Report("present", await presentation.PresentAsync());
                Report("minimize", await presentation.MinimizeAsync());
                Report("visible", await presentation.SetVisibleAsync(true));
                Report("full-screen", await presentation.SetFullScreenAsync(true));
                Console.Error.WriteLine("PRESENTED");
                await window.WaitForCloseAsync();
                return 0;
            }
            default:
                throw new ArgumentException($"Unknown guest mode '{mode}'.", nameof(mode));
        }
    }

    // OUTCOME <request> success | unavailable <reason> | failed <code>, on standard error, which the host logs.
    private static void Report(string request, PlatformResult<PlatformUnit> result) => Console.Error.WriteLine("OUTCOME " + request + " " + result switch
    {
        PlatformResult<PlatformUnit>.Success => "success",
        PlatformResult<PlatformUnit>.Unavailable unavailable => "unavailable " + unavailable.Reason,
        PlatformResult<PlatformUnit>.Failed failed => "failed " + failed.Code,
        _ => "unknown",
    });

    // The host conformance runner's guest (HostRoleConformanceTests): a relay of standard input and output to the runner,
    // which plays the guest's side of the protocol over a loopback connection. A line from the runner is written to
    // standard output, except "exit <code>", which ends the process with that code.
    public static int RunPuppet()
    {
        var port = int.Parse(Environment.GetEnvironmentVariable(PuppetPortVariable)
            ?? throw new InvalidOperationException($"{PuppetPortVariable} is not set."), System.Globalization.CultureInfo.InvariantCulture);
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var input = Console.OpenStandardInput();
        new Thread(() =>
        {
            try { input.CopyTo(stream); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }) { IsBackground = true }.Start();
        var output = Console.OpenStandardOutput();
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        while (reader.ReadLine() is { } command)
        {
            if (command.StartsWith("exit ", StringComparison.Ordinal))
            {
                output.Flush();
                Environment.Exit(int.Parse(command[5..], System.Globalization.CultureInfo.InvariantCulture));
            }
            output.Write(Encoding.UTF8.GetBytes(command + "\n"));
            output.Flush();
        }
        return 0;
    }
}
