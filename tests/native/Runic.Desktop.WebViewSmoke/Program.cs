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
    if (!DesktopPlatform.GetAvailability(null, CreateHostOptions().Linux).Presentations.Any(static presentation => presentation.Browser == BrowserKind.Embedded && presentation.IsAvailable))
    {
        throw new PlatformNotSupportedException("The platform embedded WebView runtime is not available.");
    }

    if (args.Contains("--ui-automation", StringComparer.Ordinal))
    {
        await RunWindowsUiAutomationSmokeAsync();
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
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = FirstPage() }))
    await using (var window = await surface.OpenWindowAsync(FirstWindowOptions() with { ConfirmCloseAsync = closeGuard.ConfirmAsync }))
    {
        await ExerciseFirstWindowAsync(surface, window);
        if (SmokeSoak.Enabled) await SmokeSoak.ExerciseAsync(surface, window, closeGuard);
        await GtkWidgetLifetime.ObserveAsync(window.NativeHandle);
        await ExerciseCloseGuardAsync(surface, window, closeGuard);
        await window.CloseAsync();
        AssertClosed(window);
    }

    await GtkWidgetLifetime.AssertReleasedAsync();

    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = RestartedPage() }))
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
    SmokeSoak.Completed();
    }
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
        Content =
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
            <output id="ime-result" aria-live="off" style="display:block;max-height:2em;overflow:hidden;overflow-wrap:anywhere">IME: []</output>
            <output id="keyboard-focus" aria-live="polite">Keyboard focus: none</output>
            <output id="picker-result" aria-live="polite">No file selected</output>
            <button id="finish" type="button">Finish</button>
            <script>
            const imeEvents = [];
            for (const type of ['compositionstart', 'compositionupdate', 'compositionend', 'input']) {
              document.getElementById('display-name').addEventListener(type, event => {
                imeEvents.push({ type, data: event.data, value: event.target.value, trusted: event.isTrusted, composing: event.isComposing });
                if (type === 'compositionend') {
                  document.getElementById('ime-result').textContent = 'IME: ' + JSON.stringify(imeEvents);
                }
              });
            }
            document.getElementById('display-name').addEventListener('blur', () => {
              document.getElementById('ime-result').textContent = 'IME: ' + JSON.stringify(imeEvents);
            });
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
            """,
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
            var selected = await picker.OpenFileAsync(new OpenFileOptions(), deadline.Token);
            string outcome;
            if (selected is PickerResult<IReadFileLease>.Selected file)
            {
                await using var lease = file.Value;
                await using var stream = await lease.OpenReadAsync(deadline.Token);
                using var reader = new StreamReader(stream);
                outcome = "Picked: " + lease.DisplayName + ": " + await reader.ReadToEndAsync(deadline.Token);
            }
            else if (selected is PickerResult<IReadFileLease>.Dismissed) outcome = "Open cancelled";
            else throw new InvalidOperationException("Unexpected open result: " + selected);
            await surface.ExecuteJavaScriptAsync(selected is PickerResult<IReadFileLease>.Dismissed
                ? "window.__runicOpenCancelled=true; return true;" : "window.__runicOpenPassed=true; return true;", cancellationToken: deadline.Token);
            await surface.ExecuteJavaScriptAsync("document.getElementById('picker-result').textContent=" + JavaScriptString(outcome) + "; return true;", cancellationToken: deadline.Token);
        }
        if (finished == "save")
        {
            var selected = await picker.SaveFileAsync(new SaveFileOptions("runic-uia-saved.txt"), deadline.Token);
            string outcome;
            if (selected is PickerResult<ISaveFileLease>.Selected file)
            {
                await using var lease = file.Value;
                var started = await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace, deadline.Token);
                if (started is not PlatformResult<IFileWriteTransaction>.Success success)
                    throw new InvalidOperationException("Could not stage native save: " + started);
                await using var transaction = success.Value;
                await transaction.Content.WriteAsync(System.Text.Encoding.UTF8.GetBytes("Runic Windows native picker output."), deadline.Token);
                var committed = await transaction.CommitAsync(deadline.Token);
                if (committed is not FileCommitResult.Committed)
                    throw new InvalidOperationException("Native save failed: " + committed);
                outcome = "Saved: " + lease.DisplayName;
                await surface.ExecuteJavaScriptAsync("window.__runicSavePassed=true; return true;", cancellationToken: deadline.Token);
            }
            else if (selected is PickerResult<ISaveFileLease>.Dismissed)
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

static string JavaScriptString(string value) => "'" + value
    .Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("'", "\\'", StringComparison.Ordinal)
    .Replace("\r", "\\r", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal)
    .Replace("<", "\\u003c", StringComparison.Ordinal)
    + "'";

static void RunMacOsSmoke()
{
    Console.WriteLine("macOS: starting presentation host on the main thread.");
    var host = DesktopHost.StartAsync(CreateHostOptions()).AsTask().GetAwaiter().GetResult();
    try
    {
        while (SmokeSoak.NextCycle())
        {
        var closeGuard = new SmokeCloseGuard();
        RunMacOsWindow(host, FirstPage(), FirstWindowOptions() with { ConfirmCloseAsync = closeGuard.ConfirmAsync },
            async (surface, window) =>
            {
                await ExerciseFirstWindowAsync(surface, window);
                if (SmokeSoak.Enabled) await SmokeSoak.ExerciseAsync(surface, window, closeGuard);
                await ExerciseCloseGuardAsync(surface, window, closeGuard);
            });
        RunMacOsWindow(
            host,
            RestartedPage(),
            new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true },
            static (surface, _) => ExerciseRestartedWindowAsync(surface));
        SmokeSoak.Completed();
        }
    }
    finally
    {
        Console.WriteLine("macOS: disposing presentation host.");
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void RunMacOsWindow(
    DesktopHost host,
    string content,
    DesktopWindowOptions options,
    Func<DesktopSurface, DesktopWindow, Task> exercise)
{
    var surface = host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = content })
        .AsTask().GetAwaiter().GetResult();
    DesktopWindow? window = null;
    try
    {
        Console.WriteLine("macOS: opening embedded window and authenticating bridge.");
        window = surface.OpenWindowAsync(options).AsTask().GetAwaiter().GetResult();
        Console.WriteLine("macOS: exercising window while pumping native events.");
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
        activeWindow.WaitForClose();
        Console.WriteLine("macOS: event pump returned; awaiting exercise completion.");
        work.GetAwaiter().GetResult();
        AssertClosed(activeWindow);
    }
    finally
    {
        Console.WriteLine("macOS: disposing window and surface.");
        window?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        surface.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    await window.ResizeAsync(700, 500);
    await window.MoveAsync(20, 30);
    await window.FocusAsync();
    await window.MinimizeAsync();
    await window.ToggleMaximizedAsync();
}

static async Task ExerciseCloseGuardAsync(DesktopSurface surface, DesktopWindow window, SmokeCloseGuard guard)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.CloseConfirmation))
        throw new InvalidOperationException("The native host did not advertise close confirmation.");
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
    Console.WriteLine("Native close interception passed: deferred decision, responsive bridge, veto, retry, approval.");
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

static DesktopHostOptions CreateHostOptions() => new()
{
    Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 },
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

internal sealed class WindowPickerOwner(DesktopWindow window) : INativePickerOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => window.IsOpen && window.NativeHandle != 0 && window.SupportsNativeDispatch;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
        window.DispatchNativeAsync(handle =>
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
            Action? pending = null;
            var dispatcher = new PresentationDispatcher(lifetime, () => false, action => pending = action);
            using var cancel = new CancellationTokenSource();
            var invoked = false;
            var work = dispatcher.InvokeAsync(() => invoked = true, cancel.Token).AsTask();
            cancel.Cancel();
            try { await work; throw new InvalidOperationException("Queued operation ignored cancellation."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            pending!();
            if (invoked) throw new InvalidOperationException("Cancelled operation executed.");
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
