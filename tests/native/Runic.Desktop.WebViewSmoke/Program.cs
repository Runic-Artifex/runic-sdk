using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;
using Runic.Platform.Windows;

SmokeSoak.Enabled = args.Contains("--soak", StringComparer.Ordinal);

// A stalled native event loop otherwise occupies a runner until the job timeout.
// Exit normally with failure rather than producing an unhandled-exception core dump.
using var watchdog = new Timer(static _ =>
{
    Console.Error.WriteLine("Native window smoke exceeded its 90-second deadline; see the last logged phase.");
    Environment.Exit(1);
}, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);

try
{
    SmokeSoak.Watchdog = watchdog;
    if (!DesktopPlatform.GetAvailability(CreateHostOptions()).Embedded.IsAvailable)
    {
        throw new PlatformNotSupportedException("The platform embedded WebView runtime is not available.");
    }

    if (args.Contains("--keyboard", StringComparer.Ordinal))
    {
        await KeyboardSmoke.RunAsync(CreateHostOptions(), "gtk3");
    }
    else if (args.Contains("--web-content", StringComparer.Ordinal))
    {
        // SEC-04 on Linux: real clicks through XTEST under Xvfb.
        await using var host = await DesktopHost.StartAsync(CreateHostOptions());
        await WebContentSmoke.RunAsync(host, new WebContentSmoke.Toolkit("gtk3",
            static opener => Runic.Desktop.Internal.LinuxWebKitGtkHost.ExternalOpenerOverride = opener));
    }
    else if (args.Contains("--menus", StringComparer.Ordinal) && OperatingSystem.IsWindows())
    {
        await WindowsMenuSmoke.RunAsync(CreateHostOptions());
    }
    else if (args.Contains("--dpi", StringComparer.Ordinal))
    {
        await WindowsDpiSmoke.RunAsync(CreateHostOptions());
    }
    else if (args.Contains("--ui-automation", StringComparer.Ordinal))
    {
        await RunWindowsUiAutomationSmokeAsync();
    }
    else if (args.Contains("--accessibility", StringComparer.Ordinal))
    {
        // DesktopEventLoop.Run must run before the first await: on macOS the AppKit loop owns the main thread.
        watchdog.Change(TimeSpan.FromMinutes(6), Timeout.InfiniteTimeSpan);
        var hostName = OperatingSystem.IsWindows() ? "webview2" : OperatingSystem.IsMacOS() ? "wkwebview" : "gtk3";
        var probe = AccessibilitySmoke.ProbeCommand(args);
        Environment.ExitCode = DesktopEventLoop.Run(CreateHostOptions(), host => AccessibilitySmoke.RunAsync(host, hostName, probe));
    }
    else if (args.Contains("--zoom", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
    {
        // Must run before the first await, as --event-loop: the AppKit loop owns the main thread.
        MacOsZoomSmoke.Run(CreateHostOptions());
    }
    else if (args.Contains("--lifecycle", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
    {
        // Must run before the first await, as --zoom.
        MacOsLifecycleSmoke.Run(CreateHostOptions());
    }
    else if (args.Contains("--documents", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
    {
        // Must run before the first await, as --zoom.
        MacOsDocumentsSmoke.Run(CreateHostOptions());
    }
    else if (args.Contains("--main-thread", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
    {
        // Must run before the first await, as --zoom.
        MacOsMainThreadSmoke.Run(CreateHostOptions());
    }
    else if (args.Contains("--web-content", StringComparer.Ordinal) && OperatingSystem.IsMacOS())
    {
        // Must run before the first await, as --zoom.
        MacOsWebContentSmoke.Run(CreateHostOptions());
    }
    else if (args.Contains("--monitors", StringComparer.Ordinal))
    {
        // B12 on Windows and macOS. Must run before the first await, as --event-loop: on macOS the AppKit loop owns the
        // main thread.
        Environment.ExitCode = DesktopEventLoop.Run(CreateHostOptions() with { StandardMenuTitles = MonitorSmoke.GermanTitles }, MonitorSmoke.RunAsync);
    }
    else if (args.Contains("--file-drop", StringComparer.Ordinal) && OperatingSystem.IsLinux())
    {
        // Only the GTK 3 file drop, drag out and frame drops (W315-006, B11), for repeated local runs.
        await using var host = await DesktopHost.StartAsync(CreateHostOptions());
        await FileDropSmoke.RunAsync(host);
    }
    else if (args.Contains("--event-loop", StringComparer.Ordinal))
    {
        // Must run before the first await: on macOS the entry point picks the main-thread AppKit loop.
        RunEventLoopSmoke();
    }
    else if (OperatingSystem.IsMacOS())
    {
        RunMacOsSmoke();
    }
    else
    {
        await RunAsyncSmoke();
    }

    Console.WriteLine("Runic Desktop embedded WebView smoke passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}

static async Task RunAsyncSmoke()
{
    await using var host = await DesktopHost.StartAsync(CreateHostOptions());
    while (SmokeSoak.NextCycle())
    {
    var closeGuard = new SmokeCloseGuard();
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(FirstPage()) }))
    await using (var window = await OpenFirstWindowAsync(surface, FirstWindowOptions() with { ConfirmCloseAsync = closeGuard.ConfirmAsync }))
    {
        await ExerciseFirstWindowAsync(surface, window);
        if (SmokeSoak.Enabled) await SmokeSoak.ExerciseAsync(surface, window, closeGuard);
        await GtkWidgetLifetime.ObserveAsync(window.NativeHandle);
        await ExerciseCloseGuardAsync(surface, window, closeGuard);
        await window.CloseAsync();
        AssertClosed(window);
    }

    await GtkWidgetLifetime.AssertReleasedAsync();

    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(RestartedPage()) }))
    await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Hidden = true,
    }))
    {
        await ExerciseRestartedWindowAsync(surface);
        await GtkWidgetLifetime.ObserveAsync(window.NativeHandle);
    }
    await GtkWidgetLifetime.AssertReleasedAsync();
    if (!SmokeSoak.Enabled)
    {
        await using var surface = await host.CreateSurfaceAsync(ContentSecurityPolicySmoke.SurfaceOptions());
        using var probe = ContentSecurityPolicySmoke.Register(surface);
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true });
        await probe.ExerciseAsync();
    }
    if (!SmokeSoak.Enabled) await RunWindowStateSmokeAsync(host);
    if (!SmokeSoak.Enabled) await FileDropSmoke.RunAsync(host);
    SmokeSoak.Completed();
    }
}

static async Task RunWindowStateSmokeAsync(DesktopHost host)
{
    DesktopWindowPlacement saved;
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(WindowStateSmoke.Page()) }))
    {
        var window = await surface.OpenWindowAsync(WindowStateSmoke.DisconnectedMonitorOptions());
        saved = await WindowStateSmoke.ExerciseAsync(window);
        await window.CloseAsync();
        WindowStateSmoke.CheckSavedAfterClose(window, saved);
    }
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(WindowStateSmoke.Page()) }))
    await using (var window = await surface.OpenWindowAsync(WindowStateSmoke.RestoreOptions(saved)))
    {
        await WindowStateSmoke.ExerciseRestoredAsync(window, saved);
    }
    Console.WriteLine("Window state passed: off-screen placement fitted to a monitor, change events, restore bounds, saved after close, restored maximized.");
}

static async Task RunWindowsUiAutomationSmokeAsync()
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The UI Automation smoke requires a Windows interactive desktop.");
    }

    // Keep this surface deliberately small and semantic. The accompanying Windows
    // UI Automation driver reaches the WebView2 accessibility provider through the
    // native top-level window; JavaScript calls cannot substitute for that path.
    await using var host = await DesktopHost.StartAsync(CreateHostOptions());
    await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
    {
        Content = new DesktopContent.Html(
            """
            <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
            <title>Runic Desktop UI Automation</title></head><body>
            <main><label for="display-name">Display name</label>
            <input id="display-name" aria-label="Display name" autocomplete="off">
            <button id="record" type="button">Record snapshot</button>
            <button id="pointer-target" type="button">Pointer target: 0</button>
            <button id="open-file" type="button">Open native file</button>
            <button id="save-file" type="button">Save native file</button>
            <output id="snapshot" aria-live="polite">Waiting for automation</output></main>
            <output id="keyboard-focus" aria-live="polite">Keyboard focus: none</output>
            <output id="picker-result" aria-live="polite">No file selected</output>
            <button id="finish" type="button">Finish</button>
            <script>
            document.getElementById('record').addEventListener('click', () => {
              document.getElementById('snapshot').textContent =
                'Recorded: ' + document.getElementById('display-name').value;
            });
            document.getElementById('pointer-target').addEventListener('click', event => {
              event.currentTarget.textContent = 'Pointer target: 1';
            });
            document.getElementById('open-file').addEventListener('click', () => {
              window.__runicPickerRequest = 'open';
            });
            document.getElementById('save-file').addEventListener('click', () => { window.__runicPickerRequest = 'save'; });
            for (const control of document.querySelectorAll('input, button')) {
              control.addEventListener('focus', event => {
                document.getElementById('keyboard-focus').textContent = 'Keyboard focus: ' + event.currentTarget.id;
              });
            }
            document.getElementById('finish').addEventListener('click', () => {
              if (document.getElementById('snapshot').textContent === 'Recorded: Keyboard value' &&
                  document.getElementById('pointer-target').textContent === 'Pointer target: 1' &&
                  window.__runicOpenPassed && window.__runicSavePassed && window.__runicOpenCancelled && window.__runicSaveCancelled) {
                window.__runicAutomationFinished = true;
              }
            });
            </script></body></html>
            """),
    });
    await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Width = 640,
        Height = 360,
        Centered = true,
    });

    var picker = WindowsPlatformProvider.CreateFileDialogs(new WindowPickerOwner(window));
    Console.WriteLine("Windows UI Automation surface is ready.");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
    while (true)
    {
        string finished = await surface.ExecuteJavaScriptAsync(
            "if (window.__runicPickerRequest) { const request = window.__runicPickerRequest; window.__runicPickerRequest = ''; return request; } return window.__runicAutomationFinished === true ? 'finished' : 'waiting';",
            cancellationToken: deadline.Token);
        if (finished == "finished") break;
        if (finished == "open")
        {
            // A document lease: it reads the selection twice and reopens it from its bookmark, without writing it.
            var selected = await picker.OpenDocumentAsync(new OpenFileOptions(), deadline.Token);
            string outcome;
            if (selected is PickerResult<IDocumentFileLease>.Selected file)
            {
                await using var lease = file.Value;
                string text = await ReadDocumentAsync(lease, deadline.Token);
                if (await ReadDocumentAsync(lease, deadline.Token) != text) throw new InvalidOperationException("A second document read differs.");
                await using var reopened = await ReopenDocumentAsync(lease, deadline.Token);
                if (reopened.LocalPath != lease.LocalPath || await ReadDocumentAsync(reopened, deadline.Token) != text)
                    throw new InvalidOperationException("The document's bookmark did not reopen it.");
                outcome = "Picked: " + lease.DisplayName + ": " + text;
            }
            else if (selected is PickerResult<IDocumentFileLease>.Dismissed) outcome = "Open cancelled";
            else throw new InvalidOperationException("Unexpected open result: " + selected);
            await surface.ExecuteJavaScriptAsync(selected is PickerResult<IDocumentFileLease>.Dismissed
                ? "window.__runicOpenCancelled=true; return true;" : "window.__runicOpenPassed=true; return true;", cancellationToken: deadline.Token);
            await surface.ExecuteJavaScriptAsync("document.getElementById('picker-result').textContent=" + JavaScriptString(outcome) + "; return true;", cancellationToken: deadline.Token);
        }
        if (finished == "save")
        {
            // Save As as a document, then Save twice more on the same lease, reading each save back, and reopen it from
            // its bookmark. The driver checks the last save's bytes.
            var selected = await picker.SaveDocumentAsync(new SaveFileOptions("runic-uia-saved.txt"), deadline.Token);
            string outcome;
            if (selected is PickerResult<IDocumentFileLease>.Selected file)
            {
                await using var lease = file.Value;
                string[] saves = ["Runic Windows native picker output (1 of 3).", "Runic Windows native picker output (2 of 3).", "Runic Windows native picker output."];
                foreach (string text in saves)
                {
                    var started = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace, deadline.Token);
                    if (started is not PlatformResult<IFileWriteTransaction>.Success success)
                        throw new InvalidOperationException("Could not stage native save: " + started);
                    await using (var transaction = success.Value)
                    {
                        await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), deadline.Token);
                        var committed = await transaction.CommitAsync(deadline.Token);
                        if (committed is not FileCommitResult.Committed)
                            throw new InvalidOperationException("Native save failed: " + committed);
                    }
                    if (await ReadDocumentAsync(lease, deadline.Token) != text) throw new InvalidOperationException("A saved document read back differently.");
                }
                await using (var reopened = await ReopenDocumentAsync(lease, deadline.Token))
                    if (await ReadDocumentAsync(reopened, deadline.Token) != saves[^1]) throw new InvalidOperationException("The saved document's bookmark did not reopen it.");
                outcome = "Saved: " + lease.DisplayName;
                await surface.ExecuteJavaScriptAsync("window.__runicSavePassed=true; return true;", cancellationToken: deadline.Token);
            }
            else if (selected is PickerResult<IDocumentFileLease>.Dismissed)
            {
                outcome = "Save cancelled";
                await surface.ExecuteJavaScriptAsync("window.__runicSaveCancelled=true; return true;", cancellationToken: deadline.Token);
            }
            else throw new InvalidOperationException("Unexpected save result: " + selected);
            await surface.ExecuteJavaScriptAsync("document.getElementById('picker-result').textContent=" + JavaScriptString(outcome) + "; return true;", cancellationToken: deadline.Token);
        }
        await Task.Delay(50, deadline.Token);
    }

    Console.WriteLine("Windows UI Automation WebView2 semantic interaction passed.");
}

static async Task<string> ReadDocumentAsync(IDocumentFileLease document, CancellationToken cancellationToken)
{
    var read = await document.OpenReadAsync(cancellationToken);
    if (read is not PlatformResult<Stream>.Success(var stream)) throw new InvalidOperationException("Document read failed: " + read);
    await using (stream)
    using (var reader = new StreamReader(stream))
        return await reader.ReadToEndAsync(cancellationToken);
}

// Windows bookmarks are paths, as DesktopPlatformServices.CreateFileBookmarks returns them.
static async Task<IDocumentFileLease> ReopenDocumentAsync(IDocumentFileLease document, CancellationToken cancellationToken)
{
    var bookmark = await document.CreateBookmarkAsync(cancellationToken);
    if (bookmark is not PlatformResult<System.Collections.Immutable.ImmutableArray<byte>>.Success(var bytes))
        throw new InvalidOperationException("Document bookmark failed: " + bookmark);
    var reopened = await new PathFileBookmarks().OpenDocumentAsync(bytes, cancellationToken);
    return reopened is PlatformResult<IDocumentFileLease>.Success(var lease) ? lease : throw new InvalidOperationException("Document reopen failed: " + reopened);
}

static string JavaScriptString(string value) => "'" + value
    .Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("'", "\\'", StringComparison.Ordinal)
    .Replace("\r", "\\r", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal)
    .Replace("<", "\\u003c", StringComparison.Ordinal)
    + "'";

// The single entry point picks the event loop for this platform and host: AppKit on macOS, a plain wait on
// Windows and Linux GTK 3. The GTK 4 smoke covers the Gtk4Application branch.
static void RunEventLoopSmoke()
{
    Console.WriteLine("Event loop: opening an embedded window through DesktopEventLoop.Run(options, application).");
    DesktopHost? started = null;
    int exitCode = DesktopEventLoop.Run(CreateHostOptions(), async host =>
    {
        started = host;
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html(
                "<!doctype html><html><head><script src=\"webui.js\"></script><title>Runic event loop</title></head><body>loop</body></html>"),
        });
        await using var window = await surface.OpenWindowAsync(FirstWindowOptions());
        string? title = await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(30));
        if (title != "Runic event loop")
        {
            throw new InvalidOperationException($"The event-loop window reported title '{title}'.");
        }
        await window.CloseAsync();
        if (OperatingSystem.IsMacOS())
        {
            // A second window does not launch the application again.
            await using var second = await surface.OpenWindowAsync(FirstWindowOptions());
            await second.CloseAsync();
            ExpectLaunchedOnce("the event loop's two windows");
        }
        return 42;
    });
    if (exitCode != 42)
    {
        throw new InvalidOperationException($"DesktopEventLoop.Run returned {exitCode} instead of the application's result.");
    }
    if (started is null || !IsDisposed(started))
    {
        throw new InvalidOperationException("DesktopEventLoop.Run did not dispose the host it started.");
    }
    Console.WriteLine("Event loop: the application result was returned and the host was disposed.");

    static bool IsDisposed(DesktopHost host)
    {
        try
        {
            host.Validate();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}

// The process main thread runs the AppKit loop, which services native window events while the smoke awaits.
static void RunMacOsSmoke() => DesktopEventLoop.Run(async () =>
{
    Console.WriteLine("macOS: starting presentation host under the AppKit event loop.");
    var host = await DesktopHost.StartAsync(CreateHostOptions());
    try
    {
        while (SmokeSoak.NextCycle())
        {
        var closeGuard = new SmokeCloseGuard();
        await RunMacOsWindowAsync(host, FirstPage(), FirstWindowOptions() with { ConfirmCloseAsync = closeGuard.ConfirmAsync },
            async (surface, window) =>
            {
                await ExerciseFirstWindowAsync(surface, window);
                if (SmokeSoak.Enabled) await SmokeSoak.ExerciseAsync(surface, window, closeGuard);
                await ExerciseCloseGuardAsync(surface, window, closeGuard);
            });
        await RunMacOsWindowAsync(
            host,
            RestartedPage(),
            new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true },
            static (surface, _) => ExerciseRestartedWindowAsync(surface));
        if (!SmokeSoak.Enabled)
        {
            ContentSecurityPolicySmoke.Probe? probe = null;
            try
            {
                await RunMacOsSurfaceWindowAsync(
                    host,
                    ContentSecurityPolicySmoke.SurfaceOptions(),
                    new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true },
                    (_, _) => probe!.ExerciseAsync(),
                    surface => probe = ContentSecurityPolicySmoke.Register(surface));
            }
            finally { probe?.Dispose(); }
            DesktopWindowPlacement? saved = null;
            DesktopWindow? exercised = null;
            await RunMacOsWindowAsync(host, WindowStateSmoke.Page(), WindowStateSmoke.DisconnectedMonitorOptions(),
                async (_, window) => { exercised = window; saved = await WindowStateSmoke.ExerciseAsync(window); });
            WindowStateSmoke.CheckSavedAfterClose(exercised!, saved!);
            await RunMacOsWindowAsync(host, WindowStateSmoke.Page(), WindowStateSmoke.RestoreOptions(saved!),
                (_, window) => WindowStateSmoke.ExerciseRestoredAsync(window, saved!));
            Console.WriteLine("Window state passed: off-screen placement fitted to a monitor, change events, restore bounds, saved after close, restored maximized.");
            await RunMacOsWindowAsync(host, WindowStateSmoke.Page(), new DesktopWindowOptions { Browser = BrowserKind.Embedded },
                async (_, window) =>
                {
                    await MacOsMenuSmoke.ExerciseAsync(host, window);
                    await MacOsDragOutSmoke.ExerciseAsync(window);
                });
        }
        SmokeSoak.Completed();
        }
        ExpectLaunchedOnce("the macOS smoke's windows");
    }
    finally
    {
        Console.WriteLine("macOS: disposing presentation host.");
        await host.DisposeAsync();
    }
});

// DesktopEventLoop.Run's AppKit loop sends finishLaunching once, not once per window.
static void ExpectLaunchedOnce(string after)
{
    var launches = Runic.Desktop.Internal.MacOsApplication.WillFinishLaunchingCount;
    if (launches != 1) throw new InvalidOperationException($"The application finished launching {launches} times after {after}; expected once.");
    Console.WriteLine($"PASS finishLaunching was sent once after {after}.");
}

static Task RunMacOsWindowAsync(
    DesktopHost host,
    string content,
    DesktopWindowOptions options,
    Func<DesktopSurface, DesktopWindow, Task> exercise) =>
    RunMacOsSurfaceWindowAsync(host, new DesktopSurfaceOptions { Content = new DesktopContent.Html(content) }, options, exercise);

static async Task RunMacOsSurfaceWindowAsync(
    DesktopHost host,
    DesktopSurfaceOptions surfaceOptions,
    DesktopWindowOptions options,
    Func<DesktopSurface, DesktopWindow, Task> exercise,
    Action<DesktopSurface>? beforeOpen = null)
{
    var surface = await host.CreateSurfaceAsync(surfaceOptions);
    DesktopWindow? window = null;
    try
    {
        beforeOpen?.Invoke(surface);
        Console.WriteLine("macOS: opening embedded window and authenticating bridge.");
        window = await surface.OpenWindowAsync(options);
        Console.WriteLine("macOS: exercising window while the event loop services native events.");
        var activeWindow = window;
        var work = Task.Run(async () =>
        {
            try
            {
                await exercise(surface, activeWindow);
            }
            finally
            {
                Console.WriteLine("macOS: closing exercised window.");
                await activeWindow.CloseAsync();
                Console.WriteLine("macOS: exercised window cleanup completed.");
            }
        });
        await activeWindow.WaitForCloseAsync();
        Console.WriteLine("macOS: window closed; awaiting exercise completion.");
        await work;
        AssertClosed(activeWindow);
    }
    finally
    {
        Console.WriteLine("macOS: disposing window and surface.");
        if (window is not null) await window.DisposeAsync();
        await surface.DisposeAsync();
    }
}

static async Task ExerciseFirstWindowAsync(DesktopSurface surface, DesktopWindow window)
{
    if (window.NativeHandle == 0)
    {
        throw new InvalidOperationException("The embedded platform window did not open.");
    }
    if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) !=
        "Runic Desktop M6 smoke")
    {
        throw new InvalidOperationException("The embedded WebView bridge did not execute JavaScript.");
    }
    // The native host hands the session credential to the document; fetchable scripts no longer carry it.
    if (await surface.ExecuteJavaScriptAsync("return typeof globalThis[Symbol.for('runic-desktop.bootstrap')];") != "object")
    {
        throw new InvalidOperationException("The embedded WebView did not receive its document-start bootstrap script.");
    }

    await window.ResizeAsync(700, 500);
    await window.MoveAsync(20, 30);
    // The platform's accessibility API accepts the announcement; the GTK 4 smoke's --announcement checks what
    // reaches AT-SPI.
    if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.Announcements))
        throw new InvalidOperationException("The embedded window did not advertise announcements.");
    await window.AnnounceAsync("Runic Desktop smoke announcement");
    await window.AnnounceAsync("Runic Desktop smoke alert", DesktopAnnouncementPriority.Assertive);
    // I-024: a hidden window stays open and its page keeps answering; showing it brings it back.
    if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.Visibility))
        throw new InvalidOperationException("The embedded window did not advertise visibility.");
    await window.HideAsync();
    if (!window.IsOpen || await surface.ExecuteJavaScriptAsync("return 41 + 1;", TimeSpan.FromSeconds(10)) != "42")
        throw new InvalidOperationException("Hiding the embedded window closed it or stopped its page.");
    await window.SetVisibleAsync(true);
    await window.FocusAsync();
    await window.MinimizeAsync();
    await window.ToggleMaximizedAsync();
}

static async Task ExerciseCloseGuardAsync(DesktopSurface surface, DesktopWindow window, SmokeCloseGuard guard)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation))
        throw new InvalidOperationException("The native host did not advertise close confirmation.");
    var closedEvents = 0;
    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    window.Closed += (_, _) =>
    {
        Interlocked.Increment(ref closedEvents);
        closed.TrySetResult();
    };
    await NativeCloseRequest.SendAsync(window.NativeHandle).WaitAsync(timeout.Token);
    await guard.Entered.Task.WaitAsync(timeout.Token);
    // The native loop and bridge must remain responsive while the decision is outstanding.
    if (await surface.ExecuteJavaScriptAsync("return 42;", cancellationToken: timeout.Token) != "42")
        throw new InvalidOperationException("The close request blocked the presentation.");
    var denied = window.RequestCloseAsync(timeout.Token).AsTask();
    guard.Decision.SetResult(false);
    if (await denied || !window.IsOpen)
        throw new InvalidOperationException("A denied native close request closed the window.");
    guard.Reset();
    await NativeCloseRequest.SendAsync(window.NativeHandle).WaitAsync(timeout.Token);
    await guard.Entered.Task.WaitAsync(timeout.Token);
    var allowed = window.RequestCloseAsync(timeout.Token).AsTask();
    guard.Decision.SetResult(true);
    if (!await allowed) throw new InvalidOperationException("An approved native close request was denied.");
    AssertClosed(window);
    // I-024: the vetoed requests raised nothing; the approved close raises Closed once.
    await closed.Task.WaitAsync(timeout.Token);
    await Task.Delay(200, timeout.Token);
    if (Volatile.Read(ref closedEvents) != 1)
        throw new InvalidOperationException($"DesktopWindow.Closed was raised {closedEvents} times.");
    Console.WriteLine("Native close interception passed: deferred decision, responsive bridge, veto, retry, approval, Closed.");
}

static async Task ExerciseRestartedWindowAsync(DesktopSurface surface)
{
    if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "restarted")
    {
        throw new InvalidOperationException("The embedded WebView did not restart.");
    }
}

static void AssertClosed(DesktopWindow window)
{
    if (window.IsOpen || window.NativeHandle != 0)
    {
        throw new InvalidOperationException("The embedded platform window did not close.");
    }
}

// The job's first WebView2 starts a browser process on a fresh runner. Rarely that process never registers, and
// CreateCoreWebView2Controller fails with CO_E_SERVER_EXEC_FAILURE once COM's two-minute server start timeout ends
// (#307). The runtime, not Runic, failed to start, and a rerun of the job passes, so the smoke records what was
// running and opens the window once more, in a new profile, within its three-minute deadline.
static async Task<DesktopWindow> OpenFirstWindowAsync(DesktopSurface surface, DesktopWindowOptions options)
{
    try
    {
        return await surface.OpenWindowAsync(options);
    }
    catch (DesktopException exception) when (OperatingSystem.IsWindows() && exception.Retryable && IsWebView2ServerExecFailure(exception))
    {
        var browsers = System.Diagnostics.Process.GetProcessesByName("msedgewebview2");
        Console.Error.WriteLine(
            $"WebView2's browser process did not start (CO_E_SERVER_EXEC_FAILURE, #307); {browsers.Length} msedgewebview2 processes are running. Retrying once.");
        foreach (var browser in browsers) browser.Dispose();
        return await surface.OpenWindowAsync(options);
    }
}

static bool IsWebView2ServerExecFailure(Exception exception)
{
    const int CoEServerExecFailure = unchecked((int)0x80080005);
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is COMException { ErrorCode: CoEServerExecFailure }) return true;
    }
    return false;
}

static DesktopHostOptions CreateHostOptions() => new()
{
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
    ConnectionTimeout = TimeSpan.FromSeconds(45),
    DiagnosticSink = diagnostic => Console.Error.WriteLine(
        $"Desktop diagnostic: {diagnostic.Category}/{diagnostic.Code}: {diagnostic.Message}"),
};

static DesktopWindowOptions FirstWindowOptions() => new()
{
    Browser = BrowserKind.Embedded,
    Width = 640,
    Height = 480,
    MinimumWidth = 320,
    MinimumHeight = 240,
    Hidden = true,
};

static string FirstPage() =>
    """
    <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
    <script>window.__runicSoakDocumentId = Date.now().toString(36) + Math.random().toString(36);</script>
    <title>Runic Desktop M6 smoke</title></head><body>embedded</body></html>
    """;

static string RestartedPage() =>
    """
    <!doctype html><html><head><script src="webui.js"></script>
    <title>restarted</title></head><body></body></html>
    """;

internal sealed class WindowPickerOwner(DesktopWindow window) : INativeWindowOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => window.IsOpen && window.NativeHandle != 0 && window.SupportsNativeDispatch;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
        window.InvokeNativeAsync(handle =>
        {
            if (!IsAvailable) throw new OwnerClosedException();
            action(handle);
        }, cancellationToken);
}

internal sealed class SmokeCloseGuard
{
    internal TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> Decision { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ValueTask<bool> ConfirmAsync(CancellationToken cancellationToken)
    {
        var decision = Decision;
        Entered.TrySetResult();
        return new(decision.Task.WaitAsync(cancellationToken));
    }

    internal void Reset()
    {
        Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// --soak keeps the host alive across actual native window/reload/cancellation cycles.
// File grants below are controlled test inputs, never native selected-file evidence.
internal static class SmokeSoak
{
    internal static bool Enabled;
    internal static Timer? Watchdog;
    private static int _cycles;
    internal static bool NextCycle()
    {
        if (!Enabled) return _cycles++ == 0;
        Console.WriteLine("RUNIC_SOAK_READY");
        Console.Out.Flush();
        Watchdog?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var command = Console.ReadLine();
        if (command == "stop") return false;
        if (command != "cycle") throw new InvalidOperationException("Expected cycle or stop.");
        Watchdog?.Change(TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        return true;
    }

    internal static async Task ExerciseAsync(DesktopSurface surface, DesktopWindow window, SmokeCloseGuard closeGuard)
    {
        foreach (var hostNavigation in new[] { true, false })
        {
        var marker = Guid.NewGuid().ToString("N");
        var initial = await surface.ExecuteJavaScriptAsync("return window.__runicSoakDocumentId + '\\n' + location.href;");
        var original = initial.Split('\n', 2);
        if (original.Length != 2 || string.IsNullOrWhiteSpace(original[0]) || original[0] == "undefined")
            throw new InvalidOperationException($"Initial document identity unavailable: {initial}");
        await surface.ExecuteJavaScriptAsync($"sessionStorage.setItem('soak', '{marker}'); return 'stored';");
        // Navigate the existing presentation to its current URL through the host
        // command, which closes the old bridge before replacing the document.
        if (!hostNavigation)
        {
            try
            {
                var request = await surface.ExecuteJavaScriptAsync("try { location.reload(); return 'reload requested'; } catch (error) { return 'reload failed: ' + error.stack; }");
                Console.WriteLine($"Soak script reload outcome: {request}");
            }
            catch (IOException error)
            {
                // WebView2 may unload the old document before its reply is sent.
                // The bounded check below must still prove a new complete document,
                // the unchanged URL and retained session marker before proceeding.
                Console.WriteLine($"Soak script reload disconnected before acknowledgement: {error.Message}");
            }
        }
        else await surface.NavigateAsync(original[1]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? lastValue = null;
        Exception? lastError = null;
        while (true)
        {
            try
            {
                lastValue = await surface.ExecuteJavaScriptAsync("return sessionStorage.getItem('soak') + '\\n' + window.__runicSoakDocumentId + '\\n' + document.readyState + '\\n' + location.href;", TimeSpan.FromSeconds(1), cancellationToken: deadline.Token);
                var observed = lastValue.Split('\n', 4);
                if (observed.Length == 4 && observed[0] == marker &&
                    !string.IsNullOrWhiteSpace(observed[1]) && observed[1] != "undefined" && observed[1] != original[0] &&
                    observed[2] == "complete" && observed[3] == original[1])
                {
                    Console.WriteLine($"Soak reconnect verified: document {original[0]} -> {observed[1]}, URL {observed[3]}, retained session marker.");
                    break;
                }
            }
            catch (Exception error)
            {
                // Reload deliberately drops the old bridge session. Retain the exact
                // last error so a failed reconnect is diagnosable instead of hidden.
                lastError = error;
            }
            if (deadline.IsCancellationRequested)
                throw new TimeoutException($"Native reload did not reconnect from document {original[0]} to a new complete document at {original[1]}. Close guard entered: {closeGuard.Entered.Task.IsCompleted}; decision complete: {closeGuard.Decision.Task.IsCompleted}. Last response: {lastValue ?? "<none>"}. Last error: {lastError?.ToString() ?? "<none>"}", lastError);
            await Task.Delay(50);
        }
        }
        var lifetime = new PresentationLifetime(() => window.IsOpen);
        var path = Path.Combine(Path.GetTempPath(), "runic-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var readStream = new MemoryStream([1, 2, 3]);
            var savePath = Path.Combine(path, "document.txt");
            await File.WriteAllTextAsync(savePath, "original");
            var backend = new SoakPicker(new ReadFileLease("input", readStream), await SaveFileLease.CreateAsync(savePath, new LocalAtomicFileReplacement()));
            var files = new PresentationFiles(lifetime, backend);
            var read = (PickerResult<IReadFileLease>.Selected)await files.OpenFileAsync(new OpenFileOptions());
            await read.Value.OpenReadAsync();
            var save = (PickerResult<ISaveFileLease>.Selected)await files.SaveFileAsync(new SaveFileOptions("document.txt"));
            var transaction = (PlatformResult<IFileWriteTransaction>.Success)await save.Value.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace);
            var content = transaction.Value.Content;
            await content.WriteAsync(new byte[] { 9 });
            await lifetime.DisposeAsync(); // Deliberately forgotten leases and transaction must drain.
            if (readStream.CanRead || content.CanWrite || Directory.GetFiles(path).Length != 1 || await File.ReadAllTextAsync(savePath) != "original")
                throw new InvalidOperationException("Scope teardown leaked stream/staging data or modified target.");
            var resources = lifetime.GetResourceSnapshot();
            if (resources.OwnedLeaseCount != 0 || resources.PendingOperationCount != 0)
                throw new InvalidOperationException("Scope teardown retained resources.");
        }
        finally { await lifetime.DisposeAsync(); Directory.Delete(path, true); }
    }

    internal static void Completed()
    {
        if (!Enabled) return;
        Console.WriteLine("RUNIC_SOAK_CYCLE {\"operations\":{\"window\":true,\"reconnect\":true,\"cancellation\":true},\"resources\":{\"leases\":0,\"transactions\":0,\"presentations\":0,\"pendingOperations\":0}}");
        Console.Out.Flush();
    }
    private sealed class SoakPicker(IReadFileLease read, ISaveFileLease save) : IPickerBackend
    {
        public bool IsAvailable => true;
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) => ValueTask.FromResult<PickerResult<IReadFileLease>>(new PickerResult<IReadFileLease>.Selected(read));
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) => ValueTask.FromResult<PickerResult<ISaveFileLease>>(new PickerResult<ISaveFileLease>.Selected(save));
    }
}

// Weak native references verify real GTK destruction, including WebKit's hidden
// key-binding child. Managed disposal alone cannot detect these native leaks.
internal static partial class GtkWidgetLifetime
{
    private static int _observed;
    private static int _finalized;

    internal static async Task ObserveAsync(nint window)
    {
        if (!OperatingSystem.IsLinux()) return;
        var request = new Observation(window);
        var handle = GCHandle.Alloc(request);
        unsafe
        {
            if (AddIdle((nint)(delegate* unmanaged[Cdecl]<nint, int>)&ObserveOnGtk, GCHandle.ToIntPtr(handle)) == 0)
            {
                handle.Free();
                throw new InvalidOperationException("GTK lifetime observation could not be scheduled.");
            }
        }
        await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    internal static async Task AssertReleasedAsync()
    {
        if (!OperatingSystem.IsLinux()) return;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _finalized) != Volatile.Read(ref _observed) && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        if (_observed == 0 || _observed != _finalized)
            throw new InvalidOperationException($"GTK widgets survived window disposal: observed {_observed}, finalized {_finalized}.");
    }

    private sealed record Observation(nint Window)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ObserveOnGtk(nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        var request = (Observation)handle.Target!;
        try
        {
            ForAll(request.Window, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ObserveWebView, 0);
            request.Completion.SetResult();
        }
        catch (Exception error) { request.Completion.SetException(error); }
        finally { handle.Free(); }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ObserveWebView(nint widget, nint data)
    {
        Observe(widget);
        ForAll(widget, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ObserveChild, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ObserveChild(nint widget, nint data) => Observe(widget);

    private static unsafe void Observe(nint widget)
    {
        Interlocked.Increment(ref _observed);
        WeakRef(widget, (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&Finalized, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Finalized(nint data, nint widget) => Interlocked.Increment(ref _finalized);

    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_idle_add")]
    private static partial uint AddIdle(nint callback, nint data);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_container_forall")]
    private static partial void ForAll(nint container, nint callback, nint data);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_weak_ref")]
    private static partial void WeakRef(nint instance, nint callback, nint data);
}
