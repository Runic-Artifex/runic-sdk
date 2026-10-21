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
                Check(await DesktopPlatformServices.PresentAsync(first) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                    "Presenting a serve-only Window did not report the owner unavailable.");
                Check(await DesktopPlatformServices.AnnounceAsync(first, "Saved") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                    "An announcement for a serve-only Window did not report the owner unavailable.");
                Check(DesktopPlatformServices.GetWindowState(first) is PlatformResult<DesktopWindowState>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                    "The state of a serve-only Window did not report the owner unavailable.");

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
                var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                    WindowOptions = new DesktopWindowOptions
                    {
                        Browser = BrowserKind.Embedded,
                        PresentationPolicy = DesktopPresentationPolicy.RequestedOnly,
                    },
                });

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

            // The default options fall back to an installed browser. Without one, presenting fails on the embedded
            // WebView's prerequisites, such as linux-embedded-backend-not-selected; serve-only needs neither (L2).
            string noBrowsers = Directory.CreateTempSubdirectory("runic-no-browsers-").FullName;
            try
            {
                await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
                {
                    BrowserFolder = noBrowsers,
                    WindowHostFactory = OperatingSystem.IsLinux() ? null : new UnavailableFactory(),
                    WaitForConnection = false,
                });
                var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                });
                Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
                var presented = host.ValidateWindow<ProbeWindow>();
                Check(!presented.IsValid && presented.Errors.Any(error => error.Code == "presentation-unavailable"),
                    "Without a browser and no serve-only mode, ValidateWindow did not report presentation-unavailable: " +
                    $"{string.Join(", ", presented.Diagnostics.Select(diagnostic => diagnostic.Code))}.");
                if (OperatingSystem.IsLinux())
                    Check(presented.Errors.Any(error => error.Code == "linux-embedded-backend-not-selected"),
                        "Without a browser or a Linux backend, ValidateWindow did not report linux-embedded-backend-not-selected: " +
                        $"{string.Join(", ", presented.Diagnostics.Select(diagnostic => diagnostic.Code))}.");

                Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
                var served = host.ValidateWindow<ProbeWindow>();
                Check(served.Diagnostics.Count == 0,
                    "Without a browser in serve-only mode, ValidateWindow reported presentation checks: " +
                    $"{string.Join(", ", served.Diagnostics.Select(diagnostic => $"{diagnostic.Severity} {diagnostic.Code}"))}.");
                await using var window = await host.OpenWindowAsync<ProbeWindow>();
                using var client = new HttpClient();
                using var page = await client.GetAsync(((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface.Url);
                Check(page.IsSuccessStatusCode, $"Without a browser, the serve-only surface did not serve its page: {page.StatusCode}.");
            }
            finally { Directory.Delete(noBrowsers, recursive: true); }

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
                configurations.Add(("the embedded WebView without a Linux backend or a browser fallback", new DesktopHostOptions { WaitForConnection = false },
                    new DesktopWindowOptions { Browser = BrowserKind.Embedded, PresentationPolicy = DesktopPresentationPolicy.RequestedOnly },
                    "linux-embedded-backend-not-selected"));
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
                var served = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions { WindowOptions = windowOptions })
                    .ValidateWindow<ProbeWindow>();
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

    // #313, #317: a Window serves the resources RunicDesktopHost.Resources chose for it, shown and served alike, and
    // RunicDesktopHost.GetSurface tells when the last browser tab of a served Window disconnected.
    [Test, NotInParallel(Order = 4)]
    public async Task WindowsServeTheirResourcesShownAndServedAndCountTheirConnections()
    {
        string? serveOnly = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY");
        var console = Console.Out;
        var input = Console.In;
        var lines = new LineReader();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
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
            var chosen = new List<IRunicWindow>();
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
                Resources = window =>
                {
                    chosen.Add(window);
                    var model = ((RunicWindow<OrderedCollectionModel>)window).ViewModel;
                    return window is ProbeWindow
                        ? (request, _) => ValueTask.FromResult<ContentResponse?>(request.Path == "/icons/app.svg"
                            ? ContentResponse.Text($"<svg>{model.GetHashCode()}</svg>", "image/svg+xml")
                            : null)
                        : null;
                },
            });
            using var client = new HttpClient();

            async Task CheckResourcesAsync(IRunicWindow window, string mode)
            {
                var surface = RunicDesktopHost.GetSurface(window)
                    ?? throw new InvalidOperationException($"GetSurface returned no surface ({mode}).");
                Check(ReferenceEquals(surface, ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Surface),
                    $"GetSurface returned another surface than the Window's ({mode}).");
                using var icon = await client.GetAsync(new Uri(surface.Url, "icons/app.svg"), deadline.Token);
                Check(icon.IsSuccessStatusCode && icon.Content.Headers.ContentType?.MediaType == "image/svg+xml",
                    $"The Window did not serve its resource ({mode}): {icon.StatusCode}.");
                var expected = $"<svg>{((RunicWindow<OrderedCollectionModel>)window).ViewModel.GetHashCode()}</svg>";
                Check(await icon.Content.ReadAsStringAsync(deadline.Token) == expected,
                    $"The resource was not the one chosen for this Window ({mode}).");
                using var missing = await client.GetAsync(new Uri(surface.Url, "icons/other.svg"), deadline.Token);
                Check(missing.StatusCode == System.Net.HttpStatusCode.NotFound,
                    $"A resource the handler does not have was not 404 ({mode}): {missing.StatusCode}.");
                Check(await client.GetStringAsync(surface.Url, deadline.Token) == "<!doctype html>",
                    $"The resources replaced the Window's content ({mode}).");
            }

            await using (var shown = await host.OpenWindowAsync<ProbeWindow>(cancellationToken: deadline.Token))
            await using (var other = await host.OpenWindowAsync<OtherProbeWindow>(cancellationToken: deadline.Token))
            {
                Check(chosen.SequenceEqual<IRunicWindow>([shown, other]), "Resources was not called once for each Window.");
                await CheckResourcesAsync(shown, "shown");
                var otherSurface = RunicDesktopHost.GetSurface(other)!;
                using var none = await client.GetAsync(new Uri(otherSurface.Url, "icons/app.svg"), deadline.Token);
                Check(none.StatusCode == System.Net.HttpStatusCode.NotFound,
                    $"A Window without resources served another Window's: {none.StatusCode}.");
            }

            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
            await using var served = await host.OpenWindowAsync<ProbeWindow>(cancellationToken: deadline.Token);
            await CheckResourcesAsync(served, "served");

            var servedSurface = RunicDesktopHost.GetSurface(served)!;
            Check(servedSurface.ConnectionCount == 0, $"A served Window without a tab counted {servedSurface.ConnectionCount}.");
            var counts = new System.Collections.Concurrent.ConcurrentQueue<(DesktopConnectionEventKind, int)>();
            var lastTabClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var connections = servedSurface.SubscribeConnectionEvents(invocation =>
            {
                counts.Enqueue((invocation.Kind, invocation.Surface.ConnectionCount));
                if (invocation.Kind == DesktopConnectionEventKind.Disconnected && invocation.Surface.ConnectionCount == 0)
                    lastTabClosed.TrySetResult();
            });
            using (var tab = await DesktopWireClient.ConnectAsync(servedSurface, deadline.Token))
            {
                while (servedSurface.ConnectionCount != 1) await Task.Delay(10, deadline.Token);
            }
            await lastTabClosed.Task.WaitAsync(deadline.Token);
            Check(counts.SequenceEqual([(DesktopConnectionEventKind.Connected, 1), (DesktopConnectionEventKind.Disconnected, 0)]),
                $"The connection events saw the counts {string.Join(", ", counts)}.");
            lines.Send("");
        }
        finally
        {
            lines.Send(null);
            Console.SetOut(console);
            Console.SetIn(input);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", serveOnly);
        }
    }

    private sealed class OtherProbeWindow : RunicWindow<OrderedCollectionModel>;

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
