using System.Runtime.InteropServices;
using Runic.Desktop;

// sdk#900 W4b, --close-drain: DesktopWindowOptions.CloseDrainAsync in a real native window, and the shutdown's drain.
// Linked into Runic.Desktop.Gtk4.Smoke for GTK 4.
// 1. The platform's own close (WM_CLOSE, performClose:, gtk_window_close) of a window whose drain waits for durable work:
//    the window stays open, visible and connected while the drain runs, a second native close is ignored, and the window
//    closes once the work has ended.
// 2. A termination signal (SIGTERM on Linux and macOS, Ctrl+C in a console of the smoke's own on Windows): ShutdownStarting
//    is cancelled, the window stays open while it drains (DesktopCloseReason.Shutdown), and ShutdownRequested is cancelled
//    only after the drain; the process then exits 0.
internal static partial class CloseDrainSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1);

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic close drain</title>
        </head><body>draining</body></html>
        """;

    internal static int Run(DesktopHostOptions hostOptions, bool gtk4 = false) =>
        DesktopEventLoop.Run(hostOptions with { HandleShutdownSignals = true, ShutdownDrainTimeout = TimeSpan.FromSeconds(60) },
            host => RunAsync(host, gtk4));

    private static async Task<int> RunAsync(DesktopHost host, bool gtk4)
    {
        try
        {
            Console.WriteLine("Close drain: phase 1, the native close.");
            await RunUserCloseAsync(host, gtk4);
            Console.WriteLine("Close drain: phase 2, the shutdown.");
            return await RunShutdownAsync(host);
        }
        catch (Exception error)
        {
            // Written to standard output too: on Windows a failure must not vanish into a console of the smoke's own.
            Console.WriteLine($"FAIL close drain: {error}");
            throw;
        }
    }

    private static async Task RunUserCloseAsync(DesktopHost host, bool gtk4)
    {
        var drain = new Drain();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 520, Height = 380, CloseDrainAsync = drain.RunAsync,
        });
        Require(window.DrainsVisibly, "the window to refuse the native close while it drains");
        await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);

        await SendNativeCloseAsync(window, gtk4);
        var reason = await drain.Started.Task.WaitAsync(Deadline);
        Require(reason == DesktopCloseReason.User, $"the native close to start a user drain, not {reason}");
        await Task.Delay(Hold);
        RequireVisible(window, "while its durable work runs");
        Require(await surface.ExecuteJavaScriptAsync("return document.title;", Deadline) == "Runic close drain",
            "the page to stay connected while the window drains");
        Console.WriteLine($"Close drain: the window stays open after the native close{Activity(window)}.");

        // D064.16: the user's second close is consumed.
        await SendNativeCloseAsync(window, gtk4);
        await Task.Delay(Hold);
        RequireVisible(window, "after a second close during the drain");
        Require(drain.Runs == 1, $"one drain, not {drain.Runs}");
        Require(await window.RequestCloseAsync().AsTask().WaitAsync(Deadline), "RequestCloseAsync during the drain to return true");

        drain.Release();
        await window.WaitForCloseAsync().AsTask().WaitAsync(Deadline);
        Require(!window.IsOpen, "the window to close once its work had drained");
        Console.WriteLine("PASS close drain: the native close waits for the durable work, a second close is ignored, and the window closes after it.");
    }

    private static async Task<int> RunShutdownAsync(DesktopHost host)
    {
        var drain = new Drain();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 520, Height = 380, CloseDrainAsync = drain.RunAsync,
        });
        await surface.ExecuteJavaScriptAsync("return document.readyState;", Deadline);

        Console.WriteLine("Close drain: sending a termination signal.");
        var (signal, unavailable) = SendTerminationSignal();
        if (signal is null)
        {
            // The console signal itself is unverified on this runner: the shutdown starts through the entry point the
            // signal handler calls (ShutdownSignals → DesktopHost.RequestShutdownForSignal).
            Console.WriteLine($"Close drain: Ctrl+C cannot be sent here ({unavailable}); requesting the shutdown as the signal handler does.");
            signal = "the signal handler's shutdown request";
            Require(host.RequestShutdownForSignal("SIGINT", static () => { }), "the shutdown request to start a shutdown");
        }
        Console.WriteLine($"Close drain: {signal} sent; waiting for the drain.");
        var reason = await drain.Started.Task.WaitAsync(Deadline);
        Require(reason == DesktopCloseReason.Shutdown, $"{signal} to start a shutdown drain, not {reason}");
        Require(host.ShutdownStarting.IsCancellationRequested, "ShutdownStarting to be cancelled before the drain");
        await Task.Delay(Hold);
        RequireVisible(window, $"while {signal} drains it");
        Require(!host.ShutdownRequested.IsCancellationRequested, "ShutdownRequested to wait for the drain");

        Console.WriteLine("Close drain: the window stays open while the shutdown drains; releasing the work.");
        drain.Release();
        await window.WaitForCloseAsync().AsTask().WaitAsync(Deadline);
        await Task.Delay(Timeout.Infinite, host.ShutdownRequested).ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(Deadline);
        Console.WriteLine($"PASS close drain shutdown: {signal} cancels ShutdownStarting, keeps the window open while it drains, then ShutdownRequested.");
        return 0;
    }

    private static void RequireVisible(DesktopWindow window, string when)
    {
        Require(window.IsOpen, $"the window to stay open {when}");
        Require(window.Activity is not { IsVisible: false }, $"the window to stay visible {when}{Activity(window)}");
    }

    private static string Activity(DesktopWindow window) => window.Activity is { } activity
        ? $" (visible {activity.IsVisible}, minimized {activity.IsMinimized}, active {activity.IsActive})"
        : "";

    private static void Require(bool condition, string expected)
    {
        if (!condition) throw new InvalidOperationException($"Close drain: expected {expected}.");
    }

    // The platform's own close of the window, as its title bar's close button sends it.
    private static async Task SendNativeCloseAsync(DesktopWindow window, bool gtk4)
    {
        if (OperatingSystem.IsWindows())
        {
            if (PostMessage(window.NativeHandle, 0x0010, 0, 0) == 0) throw new InvalidOperationException("WM_CLOSE could not be posted.");
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            // performClose: consults windowShouldClose:, as the close button does.
            await window.InvokeNativeAsync(static handle => SendObject(handle, Selector("performClose:"), 0));
            return;
        }
        await window.InvokeNativeAsync(gtk4 ? static handle => Gtk4WindowClose(handle) : static handle => Gtk3WindowClose(handle));
    }

    // SIGTERM on Linux and macOS; on Windows Ctrl+C in a console of the smoke's own, which reaches no other process, such
    // as the shell that runs the smoke. AllocConsole replaces the standard handles, so the job's pipes are put back.
    private static (string? Signal, string? Unavailable) SendTerminationSignal()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (Kill(Environment.ProcessId, 15) != 0) throw new InvalidOperationException("The smoke could not send SIGTERM to itself.");
            return ("SIGTERM", null);
        }
        const int StdInput = -10, StdOutput = -11, StdError = -12;
        var handles = new[] { StdInput, StdOutput, StdError }.Select(static kind => (kind, GetStdHandle(kind))).ToArray();
        _ = FreeConsole();
        var allocated = AllocConsole() != 0;
        var allocError = Marshal.GetLastPInvokeError();
        foreach (var (kind, handle) in handles) _ = SetStdHandle(kind, handle);
        if (!allocated) return (null, $"AllocConsole failed with Win32 error {allocError}");
        // A process started in a new process group ignores Ctrl+C until it says otherwise.
        _ = SetConsoleCtrlHandler(0, 0);
        if (GenerateConsoleCtrlEvent(0, 0) == 0) return (null, $"GenerateConsoleCtrlEvent failed with Win32 error {Marshal.GetLastPInvokeError()}");
        return ("Ctrl+C", null);
    }

    private sealed class Drain
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        internal TaskCompletionSource<DesktopCloseReason> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Runs => Volatile.Read(ref _runs);

        internal void Release() => _release.TrySetResult();

        internal async ValueTask RunAsync(DesktopCloseDrainContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            Started.TrySetResult(context.Reason);
            Console.WriteLine($"Close drain: draining for {context.Reason}.");
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int kind);

    [LibraryImport("kernel32.dll")]
    private static partial int SetStdHandle(int kind, nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int SetConsoleCtrlHandler(nint handler, int add);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int Kill(int processId, int signal);

    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_close")]
    private static partial void Gtk3WindowClose(nint window);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_close")]
    private static partial void Gtk4WindowClose(nint window);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void SendObject(nint receiver, nint selector, nint argument);
}
