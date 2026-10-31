using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Runtime;
using System.Runtime.Versioning;

#pragma warning disable CA1416 // The executable checks Linux before entering the annotated smoke path.

if (args.Contains("--usability", StringComparer.Ordinal)) return UsabilitySmoke.Run();

if (args.Contains("--keyboard", StringComparer.Ordinal))
{
    return Gtk4Application.Run(async () =>
    {
        await KeyboardSmoke.RunAsync(new DesktopHostOptions
        {
            Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
            WindowHostFactory = new Gtk4WindowHostFactory(),
        }, "gtk4");
        return 0;
    });
}

// SEC-04: the window's DesktopWebContentPolicy, with real clicks through XTEST under Xvfb.
if (args.Contains("--web-content", StringComparer.Ordinal))
{
    return Gtk4Application.Run(async () =>
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions().WithGtk4() with { ConnectionTimeout = TestTimeouts.Scale(TimeSpan.FromSeconds(45)) });
        await WebContentSmoke.RunAsync(host, new WebContentSmoke.Toolkit("gtk4", static opener => Gtk4WindowHost.ExternalOpenerOverride = opener));
        Console.WriteLine("PASS GTK 4 web content policy.");
        return 0;
    });
}

if (Array.IndexOf(args, "--announcement") is var announcement and >= 0)
    return AnnouncementSmoke.Run(announcement + 1 < args.Length ? args[announcement + 1] : "gtk4");

// --accessibility gtk3|gtk4 --probe <command>: the page read and changed through AT-SPI by the accessibility probe.
if (Array.IndexOf(args, "--accessibility") is var accessibility and >= 0)
{
    var toolkit = accessibility + 1 < args.Length ? args[accessibility + 1] : "gtk4";
    var options = toolkit switch
    {
        "gtk3" => new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
        "gtk4" => new DesktopHostOptions().WithGtk4(),
        _ => throw new ArgumentException($"Unknown toolkit '{toolkit}'; use gtk3 or gtk4."),
    };
    var probe = AccessibilitySmoke.ProbeCommand(args);
    return DesktopEventLoop.Run(options, host => AccessibilitySmoke.RunAsync(host, toolkit, probe));
}

// --menus gtk3|gtk4 [--rtl] (B10): the in-window menu bar, its shortcuts and context menus.
if (Array.IndexOf(args, "--menus") is var menus and >= 0)
    return MenuSmoke.Run(menus + 1 < args.Length ? args[menus + 1] : "gtk4", args.Contains("--rtl", StringComparer.Ordinal));

// --global-menu gtk3|gtk4 (B10): the bar in the desktop's global menu, with a fake registrar on a private session bus.
if (Array.IndexOf(args, "--global-menu") is var globalMenu and >= 0)
    return GlobalMenuSmoke.Run(globalMenu + 1 < args.Length ? args[globalMenu + 1] : "gtk4");

// --global-menu-session gtk3|gtk4 <log> (B10): a window with a bar on a real desktop session, for nixos/global-menu.
if (Array.IndexOf(args, "--global-menu-session") is var globalMenuSession and >= 0 && globalMenuSession + 2 < args.Length)
    return GlobalMenuSessionSmoke.Run(args[globalMenuSession + 1], args[globalMenuSession + 2]);

if (args.Contains("--rtl", StringComparer.Ordinal)) return RightToLeftSmoke.Run();

if (Array.IndexOf(args, "--monitors") is var monitors and >= 0)
    return MonitorSmoke.Run(monitors + 1 < args.Length ? args[monitors + 1] : "gtk4");

// --placement [--window-manager] (#663): a restored placement's position on X11, also under a window manager.
if (args.Contains("--placement", StringComparer.Ordinal)) return PlacementSmoke.Run(args.Contains("--window-manager", StringComparer.Ordinal));

// --spelling gtk3|gtk4 (#662): WebKitGTK's spell checker with the application's languages, and off in a window.
if (Array.IndexOf(args, "--spelling") is var spelling and >= 0)
    return SpellCheckingSmoke.Run(spelling + 1 < args.Length ? args[spelling + 1] : "gtk4");

if (args.Contains("--message-boxes", StringComparer.Ordinal)) return MessageBoxSmoke.Run();

if (args.Any(arg => arg.StartsWith("--notification-", StringComparison.Ordinal)))
    return NotificationActivationSmoke.Run(args);

// The drag-out and frame-drop cases (B11) drive several pointer drags under XTEST.
using var watchdog = new Timer(static _ =>
{
    Console.Error.WriteLine("GTK 4 smoke exceeded its 120-second deadline.");
    Environment.Exit(1);
}, null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
SmokeMode.PortalParentOnly = args.Contains("--portal-parent-only", StringComparer.Ordinal);
SmokeMode.DirectoryOnly = args.Contains("--directory", StringComparer.Ordinal);

try
{
    if (!OperatingSystem.IsLinux())
    {
        throw new PlatformNotSupportedException("The GTK 4 native smoke runs on Linux only.");
    }
    if (args.Contains("--event-loop", StringComparer.Ordinal))
    {
        // The GTK runner has one lifetime per process, so the entry-point check runs in its own process.
        RunEventLoopSmoke();
        Console.WriteLine("PASS GTK 4 DesktopEventLoop.Run entry point.");
        return 0;
    }
    if (args.Contains("--signal", StringComparer.Ordinal))
    {
        RunSignalSmoke();
        Console.WriteLine("PASS GTK 4 SIGTERM closes the window, ends the GTK loop and disposes the host.");
        return 0;
    }
    if (args.Contains("--no-display", StringComparer.Ordinal))
    {
        // Run with no reachable display: gtk_init would end the process with exit(1).
        RunNoDisplaySmoke();
        Console.WriteLine("PASS GTK 4 DesktopEventLoop.Run without a display falls back and reports why.");
        return 0;
    }
    using var directoryPortal = SmokeMode.DirectoryOnly ? DirectorySmoke.StartPortal() : null;
    AssertRunnerContract();
    Environment.ExitCode = RunSmoke();
    Console.WriteLine("PASS GTK 4 / WebKitGTK 6 native window lifecycle and capability contract.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
return Environment.ExitCode;

[SupportedOSPlatform("linux")]
static void AssertRunnerContract()
{
    var factory = new Gtk4WindowHostFactory();
    try
    {
        _ = factory.Create();
        throw new InvalidOperationException("GTK4 factory accepted host creation outside the GTK 4 application lifetime.");
    }
    // The message names the public entry point, not the internal runner.
    catch (InvalidOperationException error) when (error.Message.Contains("DesktopEventLoop.Run(options.WithGtk4(), application)", StringComparison.Ordinal))
    {
    }

    var rejectedWorker = Task.Run(() =>
    {
        try
        {
            _ = Gtk4Application.Run(static () => Task.FromResult(0));
            return false;
        }
        catch (InvalidOperationException error)
        {
            return error.Message.Contains("process main thread", StringComparison.Ordinal);
        }
    }).GetAwaiter().GetResult();
    if (!rejectedWorker)
    {
        throw new InvalidOperationException("GTK4 runner accepted a worker-thread initialization.");
    }
}

// Without a display DesktopEventLoop.Run must not start GTK: it reports gtk4-display-unavailable, runs the plain
// loop, and the GTK 4 factory is unsupported from then on, so a browser fallback can take over.
[SupportedOSPlatform("linux")]
static void RunNoDisplaySmoke()
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
    {
        throw new InvalidOperationException("Run --no-display with DISPLAY and WAYLAND_DISPLAY unset and GDK_BACKEND=x11.");
    }
    List<DesktopDiagnostic> reported = [];
    var options = new DesktopHostOptions { DiagnosticSink = reported.Add }.WithGtk4("dev.runic.desktop.gtk4.NoDisplaySmoke");
    var factory = (Gtk4WindowHostFactory)options.WindowHostFactory!;
    int result = DesktopEventLoop.Run(options, host =>
    {
        if (host.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded }).IsValid)
        {
            throw new InvalidOperationException("The embedded GTK 4 presentation validated without a display.");
        }
        return Task.FromResult(7);
    });
    if (result != 7)
    {
        throw new InvalidOperationException($"DesktopEventLoop.Run returned {result} instead of the application's result.");
    }
    var codes = reported.Select(static diagnostic => diagnostic.Code).ToArray();
    if (!codes.Contains("event-loop-unavailable") || !codes.Contains("gtk4-display-unavailable"))
    {
        throw new InvalidOperationException($"The no-display fallback reported [{string.Join(", ", codes)}].");
    }
    if (factory.IsSupported || !factory.GetAvailabilityDiagnostics().Any(static d => d.Code == "gtk4-display-unavailable"))
    {
        throw new InvalidOperationException("The GTK 4 factory stayed supported after no display could be opened.");
    }
    // The application continues on the plain loop, so every forwarded reason is a warning.
    if (reported.Any(static diagnostic => diagnostic.Severity != DesktopDiagnosticSeverity.Warning))
    {
        throw new InvalidOperationException("The no-display fallback reported an error although the application continued.");
    }

    // A direct Gtk4Application.Run without a display throws instead of letting gtk_init end the process.
    try
    {
        Gtk4Application.Run(static () => Task.FromResult(0), "dev.runic.desktop.gtk4.NoDisplaySmoke");
        throw new InvalidOperationException("Gtk4Application.Run started without a display.");
    }
    catch (PlatformNotSupportedException)
    {
    }
}

// DesktopEventLoop.Run must hand WithGtk4() options to Gtk4Application.Run: otherwise opening the window
// fails because the GTK 4 factory only creates hosts inside the runner.
[SupportedOSPlatform("linux")]
static void RunEventLoopSmoke()
{
    var options = new DesktopHostOptions().WithGtk4("dev.runic.desktop.gtk4.EventLoopSmoke");
    if (options.WindowHostFactory is not Gtk4WindowHostFactory { ApplicationId: "dev.runic.desktop.gtk4.EventLoopSmoke" })
    {
        throw new InvalidOperationException("WithGtk4(applicationId) did not configure the factory's application ID.");
    }
    DesktopHost? started = null;
    int result = DesktopEventLoop.Run(options, async host =>
    {
        started = host;
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html><title>GTK4 event loop</title><script src=\"webui.js\"></script>"),
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true });
        if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 event loop")
        {
            throw new InvalidOperationException("The GTK 4 event-loop window did not load its page.");
        }
        await window.CloseAsync();
        return 42;
    });
    if (result != 42)
    {
        throw new InvalidOperationException($"DesktopEventLoop.Run returned {result} instead of the application's result.");
    }
    try
    {
        started?.Validate();
        throw new InvalidOperationException("DesktopEventLoop.Run did not dispose the host it started.");
    }
    catch (ObjectDisposedException)
    {
    }
}

// SIGTERM must run the shutdown of a closed last window: the GTK window closes on the GTK thread, WaitForCloseAsync
// returns, the application returns and DesktopEventLoop.Run disposes the host before GTK quits.
[SupportedOSPlatform("linux")]
static void RunSignalSmoke()
{
    DesktopHost? started = null;
    var closed = false;
    int result = DesktopEventLoop.Run(new DesktopHostOptions().WithGtk4("dev.runic.desktop.gtk4.SignalSmoke"), async host =>
    {
        started = host;
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html><title>GTK4 signal</title><script src=\"webui.js\"></script>"),
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true });
        if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 signal")
        {
            throw new InvalidOperationException("The GTK 4 signal window did not load its page.");
        }
        if (SignalNative.Kill(Environment.ProcessId, SignalNative.SigTerm) != 0)
        {
            throw new InvalidOperationException("The smoke could not send SIGTERM to itself.");
        }
        await window.WaitForCloseAsync();
        closed = host.ShutdownRequested.IsCancellationRequested;
        return 43;
    });
    if (result != 43 || !closed)
    {
        throw new InvalidOperationException($"SIGTERM returned {result} (shutdown requested: {closed}) instead of closing the window.");
    }
    try
    {
        started?.Validate();
        throw new InvalidOperationException("DesktopEventLoop.Run did not dispose the host after SIGTERM.");
    }
    catch (ObjectDisposedException)
    {
    }
}

[SupportedOSPlatform("linux")]
static int RunSmoke() => Gtk4Application.Run(async () =>
{
    await RunAsync();
    return 0;
});

[SupportedOSPlatform("linux")]
static async Task RunAsync()
{
    var factory = new Gtk4WindowHostFactory();
    if (!factory.IsSupported)
    {
        throw new PlatformNotSupportedException("GTK 4 and WebKitGTK 6 are not available for the native smoke.");
    }
    // GTK 4 positions windows only on X11 (I-146); on Wayland the compositor places them.
    var x11 = Gtk4X11Placement.IsActive;
    if (factory.Capabilities.HasFlag(DesktopWindowCapabilities.Move) != x11)
    {
        throw new InvalidOperationException($"GTK 4 advertised window movement {factory.Capabilities.HasFlag(DesktopWindowCapabilities.Move)} with X11 {x11}.");
    }

    await using var host = factory.Create();
    Console.WriteLine("GTK4 smoke: opening WebKit window.");
    await host.OpenAsync(new Uri("about:blank"), new DesktopWindowHostOptions
    {
        Width = 640,
        Height = 480,
        Hidden = false,
    });
    if (!host.IsOpen || host.NativeHandle == 0)
    {
        throw new InvalidOperationException("GTK 4 host did not create a native window.");
    }
    if (host.Capabilities.HasFlag(DesktopWindowCapabilities.Move) != x11)
    {
        throw new InvalidOperationException("GTK 4 host advertised window movement on a display that has no window positions.");
    }

    var owner = new HostOwner((IDesktopNativeDispatchWindowHost)host);
    await using (var portalParent = await Gtk4PlatformProvider.CreatePortalWindowOwner(owner).ExportParentAsync())
    {
        Console.WriteLine($"GTK4 smoke: exported {portalParent.Identifier.Split(':')[0]} portal parent.");
        if (!portalParent.Identifier.StartsWith("x11:", StringComparison.Ordinal) &&
            !portalParent.Identifier.StartsWith("wayland:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GTK 4 did not export an X11 or Wayland portal parent.");
        }
    }
    if (SmokeMode.PortalParentOnly)
    {
        await host.CloseAsync();
        return;
    }
    if (SmokeMode.DirectoryOnly)
    {
        await DirectorySmoke.RunAsync(owner, host);
        return;
    }
    var clipboard = Gtk4PlatformProvider.CreateTextClipboard(owner);
    // Wayland clipboard access requires the compositor to have focused the
    // window. Present queues that request; it does not synchronously grant it.
    bool focused = false;
    for (int attempt = 0; attempt < 50 && !focused; attempt++)
    {
        await owner.InvokeAsync(window => focused = NotificationActivationSmoke.IsActive(window) != 0);
        if (!focused) await Task.Delay(100);
    }
    if (!focused) throw new InvalidOperationException("The compositor did not focus the clipboard test window.");
    try
    {
        if (await clipboard.WriteTextAsync("") is not PlatformResult<PlatformUnit>.Success ||
            await clipboard.ReadTextAsync(0) is not PlatformResult<string?>.Success { Value: "" })
        {
            throw new InvalidOperationException("GTK 4 empty clipboard text was not preserved.");
        }
        var write = await clipboard.WriteTextAsync("Runic GTK4 clipboard");
        var read = await clipboard.ReadTextAsync(64);
        if (write is not PlatformResult<PlatformUnit>.Success ||
            read is not PlatformResult<string?>.Success { Value: "Runic GTK4 clipboard" })
        {
            throw new InvalidOperationException($"GTK 4 clipboard round-trip failed: write={write}; read={read}.");
        }
        if (await clipboard.ReadTextAsync(3) is not PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge })
        {
            throw new InvalidOperationException("GTK 4 clipboard character bound was not enforced.");
        }
        await ClipboardFormatsSmoke.RunAsync(clipboard);
        var successor = Gtk4PlatformProvider.CreateTextClipboard(owner);
        try
        {
            _ = await successor.WriteTextAsync("successor");
            await ((IAsyncDisposable)clipboard).DisposeAsync();
            if (await successor.ReadTextAsync(64) is not PlatformResult<string?>.Success { Value: "successor" })
            {
                throw new InvalidOperationException("GTK 4 disposal cleared another writer's clipboard content.");
            }
        }
        finally { await ((IAsyncDisposable)successor).DisposeAsync(); }
    }
    finally
    {
        if (clipboard is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    Console.WriteLine("GTK4 smoke: exercising lifecycle operations.");
    await host.ResizeAsync(700, 500);
    await host.FocusAsync();
    await host.ToggleMaximizedAsync();
    await host.SetVisibleAsync(false);
    await host.NavigateAsync(new Uri("about:blank#reloaded"));
    await ExerciseCancelledQueuedOpenAsync(factory, (IDesktopNativeDispatchWindowHost)host);
    Console.WriteLine("GTK4 smoke: closing window.");
    await host.CloseAsync();
    if (host.IsOpen || host.NativeHandle != 0)
    {
        throw new InvalidOperationException("GTK 4 host did not release its native window.");
    }

    await ExerciseDesktopHostAsync(factory);
}

[SupportedOSPlatform("linux")]
static async Task ExerciseCancelledQueuedOpenAsync(Gtk4WindowHostFactory factory, IDesktopNativeDispatchWindowHost owner)
{
    Console.WriteLine("GTK4 smoke: cancelling a queued window creation.");
    using var release = new ManualResetEventSlim();
    var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var busy = owner.InvokeNativeAsync(() =>
    {
        blocked.SetResult();
        release.Wait();
    }, CancellationToken.None).AsTask();
    await blocked.Task;
    try
    {
        await using var queued = factory.Create();
        using var cancellation = new CancellationTokenSource();
        var open = queued.OpenAsync(new Uri("about:blank"), new DesktopWindowHostOptions(), cancellation.Token).AsTask();
        cancellation.Cancel();
        try
        {
            await open.WaitScaledAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("A cancelled GTK 4 window creation opened a window.");
        }
        catch (OperationCanceledException)
        {
        }
        if (queued.IsOpen)
        {
            throw new InvalidOperationException("A cancelled GTK 4 window creation left a window open.");
        }
    }
    finally
    {
        release.Set();
        await busy;
    }
}

[SupportedOSPlatform("linux")]
static async Task ExerciseDesktopHostAsync(Gtk4WindowHostFactory factory)
{
    var x11 = Gtk4X11Placement.IsActive;
    Console.WriteLine("GTK4 smoke: exercising explicit DesktopHost integration and bridge.");
    var profile = new DesktopHostOptions().WithGtk4();
    if (profile.WindowHostFactory is not Gtk4WindowHostFactory || profile.Linux.EmbeddedBackend != LinuxEmbeddedBackend.Gtk4WebKit6)
    {
        throw new InvalidOperationException("WithGtk4 did not select the GTK 4 backend and factory together.");
    }
    await using (var profiled = await DesktopHost.StartAsync(profile))
    {
        var validation = profiled.Validate(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"The GTK 4 profile reported missing pieces: {string.Join(", ", validation.Errors.Select(static item => item.Code))}.");
        }
    }
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
    {
        Linux = new LinuxDesktopOptions { EmbeddedBackend = LinuxEmbeddedBackend.Gtk4WebKit6 },
        WindowHostFactory = factory,
        WaitForConnection = true,
    });
    await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = new DesktopContent.Html("<!doctype html><title>GTK4 bridge</title><script src=\"webui.js\"></script><main>GTK4</main>"),
    });

    var decisions = new Queue<bool>([false, true]);
    await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Hidden = true,
        ConfirmCloseAsync = _ => ValueTask.FromResult(decisions.Dequeue()),
    });
    if (window.Capabilities.HasFlag(DesktopWindowCapabilities.Move) != x11 ||
        !window.Capabilities.HasFlag(DesktopWindowCapabilities.Visibility) ||
        await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 bridge")
    {
        throw new InvalidOperationException("GTK 4 DesktopHost bridge or capability contract failed.");
    }
    if (await window.RequestCloseAsync() || !window.IsOpen)
    {
        throw new InvalidOperationException("GTK 4 close-veto did not keep the bridge window open.");
    }
    if (!await window.RequestCloseAsync() || window.IsOpen)
    {
        throw new InvalidOperationException("GTK 4 close retry did not close the bridge window.");
    }

    await using var restartedSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = new DesktopContent.Html("<!doctype html><title>GTK4 restarted</title><script src=\"webui.js\"></script>"),
    });
    await using var restartedWindow = await restartedSurface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Hidden = true,
    });
    if (await restartedSurface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 restarted")
    {
        throw new InvalidOperationException("GTK 4 did not reopen a DesktopHost window on the same application runner.");
    }
    await restartedWindow.CloseAsync();
    await ExerciseVisibilityAndClosedAsync(host);
    await ExerciseWindowStateAsync(host, x11);
    if (x11) await ExerciseX11PositionAsync(host);
    await FileDropGtk4Smoke.RunAsync(host);
}

// I-024: a window hides and shows without closing, and Closed is raised once when it closes.
static async Task ExerciseVisibilityAndClosedAsync(DesktopHost host)
{
    await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = new DesktopContent.Html("<!doctype html><title>GTK4 visibility</title><script src=\"webui.js\"></script>"),
    });
    var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var raised = 0;
    window.Closed += (_, _) =>
    {
        Interlocked.Increment(ref raised);
        closed.TrySetResult();
    };
    async Task<bool> MappedAsync()
    {
        var mapped = false;
        await window.InvokeNativeAsync(handle => mapped = Gtk4Native.WidgetGetMapped(handle));
        return mapped;
    }
    await window.HideAsync();
    for (var waited = 0; await MappedAsync() && waited < 5000; waited += 20) await Task.Delay(20);
    if (await MappedAsync() || !window.IsOpen)
        throw new InvalidOperationException("HideAsync did not unmap the GTK 4 window or closed it.");
    if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "GTK4 visibility")
        throw new InvalidOperationException("The hidden GTK 4 window's page stopped answering.");
    await window.SetVisibleAsync(true);
    for (var waited = 0; !await MappedAsync() && waited < 5000; waited += 20) await Task.Delay(20);
    if (!await MappedAsync())
        throw new InvalidOperationException("SetVisibleAsync(true) did not map the GTK 4 window again.");
    // A permission request reaches the window's permission-request handler, which denies it without a grant; the
    // handler must not keep the view from being released when the window closes.
    await surface.ExecuteJavaScriptAsync("Notification.requestPermission().then(result => window.__permission = result); return 'asked';");
    var permission = "undefined";
    for (var waited = 0; permission == "undefined" && waited < 5000; waited += 50)
    {
        await Task.Delay(50);
        permission = await surface.ExecuteJavaScriptAsync("return String(window.__permission);");
    }
    if (permission != "denied")
        throw new InvalidOperationException($"The GTK 4 window answered a notification permission request with '{permission}'.");
    // Closes as the title bar's close button does, through gtk_window_close and the window's close request (#509).
    await window.InvokeNativeAsync(Gtk4Native.WindowClose);
    await closed.Task.WaitScaledAsync(TimeSpan.FromSeconds(15));
    // Completes once the window and its web view were released, then the surface is disposed.
    await window.WaitForCloseAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(15));
    await Task.Delay(200);
    if (window.IsOpen || Volatile.Read(ref raised) != 1)
        throw new InvalidOperationException($"DesktopWindow.Closed was raised {raised} times for one user close.");
    Console.WriteLine("GTK4 smoke: hide, show, a denied permission request, the native close and the Closed event passed.");
}

// I-146: on X11 a window opens at its requested position, reports it in its state and moves.
static async Task ExerciseX11PositionAsync(DesktopHost host)
{
    const string page = "<!doctype html><title>GTK4 position</title><script src=\"webui.js\"></script>";
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(page) }))
    {
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, X = 120, Y = 90, Width = 400, Height = 300 });
        var opened = await WaitForStateAsync(window, static state => state is { X: 120, Y: 90 }, "the position 120,90");
        if (opened.Placement is not { X: 120, Y: 90 })
            throw new InvalidOperationException($"The GTK 4 placement did not keep the position: {opened}.");
        await window.MoveAsync(200, 150);
        var moved = await WaitForStateAsync(window, static state => state is { X: 200, Y: 150, Placement: { X: 200, Y: 150 } }, "the position 200,150");
        Console.WriteLine($"GTK4 smoke: X11 window opened at {opened.X},{opened.Y} and moved to {moved.X},{moved.Y}.");
        await window.CloseAsync();
    }
    // A hidden window takes its position when it is first shown.
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(page) }))
    {
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true, X = 60, Y = 40 });
        await window.SetVisibleAsync(true);
        await WaitForStateAsync(window, static state => state is { X: 60, Y: 40 }, "the position 60,40 of a window shown later");
        await window.CloseAsync();
    }
    // A saved placement is restored with its position, kept on a monitor.
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(page) }))
    {
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Placement = new DesktopWindowPlacement { X = 30, Y = 20, Width = 320, Height = 240 },
        });
        await WaitForStateAsync(window, static state => state is { X: 30, Y: 20 }, "the restored position 30,20");
        await window.CloseAsync();
    }
    Console.WriteLine("GTK4 smoke: X11 position, move, deferred position and placement restore passed.");
}

// W315-001: GTK 4 reports the size and maximized state, keeps the restored size in the placement, and restores a
// placement fitted to a monitor; on Wayland it ignores the position, on X11 it keeps the window on a monitor.
static async Task ExerciseWindowStateAsync(DesktopHost host, bool x11)
{
    const string page = "<!doctype html><title>GTK4 state</title><script src=\"webui.js\"></script>";
    DesktopWindowPlacement saved;
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(page) }))
    {
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Placement = new DesktopWindowPlacement { X = -100_000, Y = -100_000, Width = 20_000, Height = 15_000 },
        });
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.State))
            throw new InvalidOperationException("The GTK 4 window did not advertise its state.");
        var opened = await WaitForStateAsync(window, static _ => true, "an initial state");
        Console.WriteLine($"GTK4 smoke: window state opened as {opened}.");
        if (opened.Width >= 20_000 || opened.Placement.Width >= 20_000
            || (x11 ? opened.X is not >= 0 || opened.Y is not >= 0 : opened.X is not null || opened.Placement.X is not null))
            throw new InvalidOperationException($"The GTK 4 placement was not fitted to a monitor (X11 {x11}): {opened}.");
        var changed = 0;
        window.StateChanged += (_, _) => Interlocked.Increment(ref changed);
        await window.ResizeAsync(420, 300);
        var resized = await WaitForStateAsync(window, static state => state.Placement is { Width: 420, Height: 300 }, "the size 420x300");
        // DesktopWindow.State changes before StateChanged runs on the thread pool: wait for the event too.
        for (var waited = 0; Volatile.Read(ref changed) == 0 && waited < 5000; waited += 20) await Task.Delay(20);
        if (Volatile.Read(ref changed) == 0)
            throw new InvalidOperationException("Resizing the GTK 4 window raised no StateChanged event.");
        saved = resized.Placement with { Maximized = true };
        await window.CloseAsync();
        if (window.State?.Placement is not { Width: 420, Height: 300 })
            throw new InvalidOperationException($"The closed GTK 4 window lost its last state: {window.State}.");
    }
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(page) }))
    await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Placement = saved }))
    {
        var restored = await WaitForStateAsync(window, static state => state.Placement is { Width: 420, Height: 300 }, "the restored size");
        Console.WriteLine($"GTK4 smoke: window state restored {saved} as {restored}.");
    }
    Console.WriteLine("GTK4 smoke: window state, change events, saved-after-close and restore passed.");
}

static async Task<DesktopWindowState> WaitForStateAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected)
{
    var wait = TestTimeouts.Start(TimeSpan.FromSeconds(15));
    while (!wait.IsExpired)
    {
        if (window.State is { } state && condition(state)) { wait.Succeeded(); return state; }
        await Task.Delay(20);
    }
    throw new TimeoutException($"The GTK 4 window did not report {expected}; last state: {window.State?.ToString() ?? "<none>"}.");
}

internal sealed class HostOwner(IDesktopNativeDispatchWindowHost host) : INativeWindowOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => host.IsOpen && host.NativeHandle != 0;

    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
        host.InvokeNativeAsync(() =>
        {
            if (!IsAvailable)
            {
                throw new OwnerClosedException();
            }
            action(host.NativeHandle);
        }, cancellationToken);
}

[SupportedOSPlatform("linux")]
internal static partial class Gtk4Native
{
    [System.Runtime.InteropServices.LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_mapped")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I4)]
    internal static partial bool WidgetGetMapped(nint widget);

    [System.Runtime.InteropServices.LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_close")]
    internal static partial void WindowClose(nint window);
}

internal static class SmokeMode
{
    internal static bool PortalParentOnly { get; set; }
    internal static bool DirectoryOnly { get; set; }
}
#pragma warning restore CA1416

internal static partial class SignalNative
{
    internal const int SigTerm = 15;

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "kill")]
    internal static partial int Kill(int processId, int signal);
}
