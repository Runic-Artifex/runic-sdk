using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// The case replaces Console.Out and Console.In and restores them in finally blocks; the cases do not run in
// parallel (AssemblyInfo.cs), so no other case uses them meanwhile.
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging

// RUNIC_APPLICATION_SERVE_ONLY has CS-WebUI's contract on Runic Desktop: the surface serves without a window, its
// URL is written as RUNIC_APPLICATION_URL=<url>, and WaitForCloseAsync returns once a line is read from standard
// input. It takes precedence over RUNIC_APPLICATION_CLOSE_AFTER_OPEN.
internal sealed class ServeOnlyChecks
{
    [Test, NotInParallel(Order = 1)]
    public async Task ServeOnlySwitchServesWithoutAWindowUntilAnInputLine()
    {
        string? serveOnly = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY");
        string? closeAfterOpen = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN");
        var console = Console.Out;
        var input = Console.In;
        var lines = new LineReader();
        var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", "1");
            Console.SetIn(lines);
            Console.SetOut(output);

            var services = new ServiceCollection();
            services.AddScoped<OrderedCollectionModel>();
            services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
                _ => (transport, model) => new OrderedCollectionBridge(transport, model));
            await using var provider = services.BuildServiceProvider();
            var factory = new NativeDispatchFactory();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = factory,
                WaitForConnection = false,
            });
            await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<ProbeWindow>();
            var host = (DesktopBridgeWindow<OrderedCollectionModel>)window.Host;

            Check(factory.Hosts.Count == 0, "Serve-only mode opened a window.");
            Check(output.ToString().Contains($"RUNIC_APPLICATION_URL={host.Surface.Url}", StringComparison.Ordinal),
                $"Serve-only mode did not write the surface URL: '{output}'.");
            Check(!output.ToString().Contains("RUNIC_APPLICATION_OPENED", StringComparison.Ordinal),
                "RUNIC_APPLICATION_CLOSE_AFTER_OPEN took precedence over serve-only mode.");
            using (var client = new HttpClient())
            {
                using var page = await client.GetAsync(host.Surface.Url);
                Check(page.IsSuccessStatusCode, $"The serve-only surface did not serve its page: {page.StatusCode}.");
            }

            var closed = window.WaitForCloseAsync().AsTask();
            await Task.Delay(200);
            Check(!closed.IsCompleted, "WaitForCloseAsync returned before a line was read from standard input.");
            lines.Send("\n");
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lines.Send(null);
            Console.SetOut(console);
            Console.SetIn(input);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", serveOnly);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", closeAfterOpen);
        }
    }

    // #310: a line ends the Windows serving when it is read; a Window opened after it serves until the next line,
    // and has no native owner to present.
    [Test, NotInParallel(Order = 2)]
    public async Task ServeOnlyLineEndsTheServingWindowsAndAWindowOpenedLaterWaitsForTheNext()
    {
        string? serveOnly = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY");
        var console = Console.Out;
        var input = Console.In;
        var lines = new LineReader();
        try
        {
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
            Console.SetIn(lines);
            Console.SetOut(new StringWriter());

            var services = new ServiceCollection();
            services.AddScoped<OrderedCollectionModel>();
            services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
                _ => (transport, model) => new OrderedCollectionBridge(transport, model));
            await using var provider = services.BuildServiceProvider();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory(),
                WaitForConnection = false,
            });
            var host = TestWindows.EmbeddedHtml(provider, desktop);

            await using (var first = await host.OpenWindowAsync<ProbeWindow>())
            await using (var second = await host.OpenWindowAsync<ProbeWindow>())
            {
                var bridge = (DesktopBridgeWindow<OrderedCollectionModel>)first.Host;
                string? noOwner = null;
                try { _ = bridge.NativeOwner; }
                catch (InvalidOperationException error) { noOwner = error.Message; }
                Check(noOwner?.Contains("RUNIC_APPLICATION_SERVE_ONLY", StringComparison.Ordinal) == true,
                    $"NativeOwner did not report serve-only mode: '{noOwner}'.");
                Check(await PlatformServices.PresentAsync(first) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                    "Presenting a serve-only Window did not report the owner unavailable.");

                var closed = Task.WhenAll(first.WaitForCloseAsync().AsTask(), second.WaitForCloseAsync().AsTask());
                lines.Send("");
                await closed.WaitAsync(TimeSpan.FromSeconds(5));
            }

            await using var reopened = await host.OpenWindowAsync<ProbeWindow>();
            var reopenedClosed = reopened.WaitForCloseAsync().AsTask();
            await Task.Delay(200);
            Check(!reopenedClosed.IsCompleted, "A Window opened after the stop line closed without a new line.");
            lines.Send("");
            await reopenedClosed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lines.Send(null);
            Console.SetOut(console);
            Console.SetIn(input);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", serveOnly);
        }
    }

    // #238: ValidateWindow makes the serve-only decision of opening. A presentation this machine cannot open is an
    // error without serve-only, and no finding with it, where the same Window opens and serves its page; an invalid
    // configuration is an error either way. On Linux the
    // environment is the real one of a headless run: GTK 3 selected and no DISPLAY or WAYLAND_DISPLAY. Elsewhere,
    // and in addition on Linux, an embedded-window provider that reports itself unavailable stands in for it.
    [Test, NotInParallel(Order = 3)]
    public async Task ValidateWindowSkipsEnvironmentChecksInServeOnlyMode()
    {
        string?[] saved = [.. s_validationVariables.Select(Environment.GetEnvironmentVariable)];
        var console = Console.Out;
        var input = Console.In;
        var lines = new LineReader();
        try
        {
            Environment.SetEnvironmentVariable("DISPLAY", null);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", null);
            Console.SetIn(lines);
            Console.SetOut(new StringWriter());

            var services = new ServiceCollection();
            services.AddScoped<OrderedCollectionModel>();
            services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
                _ => (transport, model) => new OrderedCollectionBridge(transport, model));
            await using var provider = services.BuildServiceProvider();
            List<(string Name, DesktopHostOptions Options, string Code)> environments =
            [
                ("an unavailable embedded-window provider", new DesktopHostOptions
                {
                    WindowHostFactory = new UnavailableFactory(),
                    WaitForConnection = false,
                }, UnavailableFactory.Code),
            ];
            if (OperatingSystem.IsLinux())
                environments.Add(("GTK 3 without a graphical session", new DesktopHostOptions
                {
                    Linux = new LinuxDesktopOptions { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
                    WaitForConnection = false,
                }, "graphical-session-missing"));

            foreach (var (name, options, code) in environments)
            {
                await using var desktop = await DesktopHost.StartAsync(options);
                var host = new RunicDesktopHost(provider, desktop)
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                    WindowOptions = new DesktopWindowOptions
                    {
                        Browser = BrowserKind.Embedded,
                        PresentationPolicy = DesktopPresentationPolicy.RequestedOnly,
                    },
                };

                Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
                var presented = host.ValidateWindow<ProbeWindow>();
                Check(!presented.IsValid && presented.Errors.Any(error => error.Code == code),
                    $"With {name} and no serve-only mode, ValidateWindow did not report {code}: " +
                    $"{string.Join(", ", presented.Diagnostics.Select(diagnostic => diagnostic.Code))}.");

                Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
                var served = host.ValidateWindow<ProbeWindow>();
                Check(served.Diagnostics.Count == 0,
                    $"With {name} in serve-only mode, ValidateWindow reported presentation checks: " +
                    $"{string.Join(", ", served.Diagnostics.Select(diagnostic => $"{diagnostic.Severity} {diagnostic.Code}"))}.");
                served.ThrowIfInvalid();
                await using var window = await host.OpenWindowAsync<ProbeWindow>();
                var bridgeWindow = (DesktopBridgeWindow<OrderedCollectionModel>)window.Host;
                using var client = new HttpClient();
                using var page = await client.GetAsync(bridgeWindow.Surface.Url);
                Check(page.IsSuccessStatusCode, $"With {name}, the serve-only surface did not serve its page: {page.StatusCode}.");
            }

            // The configuration is still checked in serve-only mode: it fails on every launch that presents a window.
            List<(string Name, DesktopHostOptions Options, DesktopWindowOptions Window, string Code)> configurations =
            [
                ("an undefined browser", new DesktopHostOptions { WindowHostFactory = new UnavailableFactory(), WaitForConnection = false },
                    new DesktopWindowOptions { Browser = (BrowserKind)999 }, "window-option-invalid"),
                ("a browser Runic Desktop cannot launch", new DesktopHostOptions { WindowHostFactory = new UnavailableFactory(), WaitForConnection = false },
                    new DesktopWindowOptions { Browser = BrowserKind.Safari, PresentationPolicy = DesktopPresentationPolicy.RequestedOnly },
                    "browser-unsupported"),
            ];
            if (OperatingSystem.IsLinux())
                configurations.Add(("GTK 4 without its provider", new DesktopHostOptions
                {
                    Linux = new LinuxDesktopOptions { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
                    WaitForConnection = false,
                }, new DesktopWindowOptions { Browser = BrowserKind.Embedded, PresentationPolicy = DesktopPresentationPolicy.RequestedOnly },
                    "gtk4-provider-missing"));
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
            foreach (var (name, options, windowOptions, code) in configurations)
            {
                await using var desktop = await DesktopHost.StartAsync(options);
                var served = new RunicDesktopHost(provider, desktop) { WindowOptions = windowOptions }.ValidateWindow<ProbeWindow>();
                Check(!served.IsValid && served.Errors.Any(error => error.Code == code),
                    $"With {name} in serve-only mode, ValidateWindow did not report {code}: " +
                    $"{string.Join(", ", served.Diagnostics.Select(diagnostic => $"{diagnostic.Severity} {diagnostic.Code}"))}.");
                Check(served.Diagnostics.All(diagnostic => diagnostic.Code is not ("graphical-session-missing" or UnavailableFactory.Code)),
                    $"With {name} in serve-only mode, ValidateWindow reported the environment: " +
                    $"{string.Join(", ", served.Diagnostics.Select(diagnostic => diagnostic.Code))}.");
            }
        }
        finally
        {
            lines.Send(null);
            Console.SetOut(console);
            Console.SetIn(input);
            for (var index = 0; index < s_validationVariables.Length; index++)
                Environment.SetEnvironmentVariable(s_validationVariables[index], saved[index]);
        }
    }

    private static readonly string[] s_validationVariables =
        ["RUNIC_APPLICATION_SERVE_ONLY", "RUNIC_APPLICATION_CLOSE_AFTER_OPEN", "DISPLAY", "WAYLAND_DISPLAY"];

    // An embedded-window provider without a display, reported as Runic.Desktop.Gtk4 reports it.
    private sealed class UnavailableFactory : IDesktopWindowHostFactory
    {
        internal const string Code = "gtk4-display-unavailable";
        public bool IsSupported => false;
        public IDesktopWindowHost Create() => throw new InvalidOperationException("Serve-only mode created a window host.");
        public IReadOnlyList<DesktopDiagnostic> GetAvailabilityDiagnostics() =>
            [new(DesktopErrorCategory.Unavailable, Code, "The test window host has no display.", Retryable: false)];
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Standard input whose ReadLine blocks until the case sends a line, or null for the end of input, after which it
    // stays at the end.
    private sealed class LineReader : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string?> _lines = [];
        private volatile bool _ended;
        public void Send(string? line) { if (!_ended) _lines.Add(line?.TrimEnd('\n')); }
        public override string? ReadLine()
        {
            if (_ended) return null;
            var line = _lines.Take();
            if (line is null) _ended = true;
            return line;
        }
    }
}
