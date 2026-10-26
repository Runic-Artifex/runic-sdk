using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Platform;

// D046.2 on the macOS runner: DesktopEventLoop.InvokeOnMainThreadAsync for windowless native work. Another thread is
// refused before the loop runs; under the windowless Run(Func<Task>) the work runs on the process main thread, a Func<T>
// returns its value, an exception reaches the caller, cancellation before the start keeps the work from running, and
// work requested while a context menu tracks runs during the tracking. Run with --main-thread on macOS.
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal static partial class MacOsMainThreadSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);

    internal static void Run(DesktopHostOptions hostOptions)
    {
        // Before the loop: another thread has nothing to run its work.
        var refused = Task.Run(async () =>
        {
            try { await DesktopEventLoop.InvokeOnMainThreadAsync(static () => { }); return null; }
            catch (InvalidOperationException error) { return error; }
        }).GetAwaiter().GetResult();
        Expect(refused is not null && refused.Message.Contains("DesktopEventLoop.Run", StringComparison.Ordinal),
            $"another thread is refused before the loop runs: {refused?.Message ?? "it was not"}");
        Pass("before DesktopEventLoop.Run, a call from another thread throws InvalidOperationException.");

        DesktopEventLoop.Run(async () =>
        {
            Expect(PthreadMainNp() == 0, "the application's work runs off the main thread");
            var onMain = await DesktopEventLoop.InvokeOnMainThreadAsync(static () => PthreadMainNp() != 0).AsTask().WaitAsync(StepTimeout);
            Expect(onMain, "the work ran on the process main thread");
            var title = await DesktopEventLoop.InvokeOnMainThreadAsync(static () =>
                Text(Send(Send(Send(Class("NSRunningApplication"), Sel("currentApplication")), Sel("localizedName")), Sel("description")))).AsTask().WaitAsync(StepTimeout);
            Pass($"without a window, the work runs on the main thread and a Func<T> returns its value ({title}).");

            var thrown = new InvalidOperationException("thrown on the main thread");
            try
            {
                await DesktopEventLoop.InvokeOnMainThreadAsync(() => throw thrown).AsTask().WaitAsync(StepTimeout);
                Expect(false, "the exception reached the caller");
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, thrown)) { }
            Pass("an exception the work throws reaches the caller.");

            // Cancellation: before the call, and while the work waits behind busy main-thread work.
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                var ran = false;
                await ExpectCancelledAsync(() => DesktopEventLoop.InvokeOnMainThreadAsync(() => { ran = true; }, cancelled.Token));
                var busy = DesktopEventLoop.InvokeOnMainThreadAsync(static () => Thread.Sleep(1000)).AsTask();
                using var later = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                await ExpectCancelledAsync(() => DesktopEventLoop.InvokeOnMainThreadAsync(() => { ran = true; }, later.Token));
                await busy.WaitAsync(StepTimeout);
                await DesktopEventLoop.InvokeOnMainThreadAsync(static () => { }).AsTask().WaitAsync(StepTimeout);
                Expect(!ran, "cancelled work did not run");
            }
            Pass("cancellation before the work starts cancels the task, and the work never runs.");

            await ExerciseDuringContextMenuAsync(hostOptions);
        });
        Pass("DesktopEventLoop.InvokeOnMainThreadAsync behaved as documented.");
    }

    private static async Task ExpectCancelledAsync(Func<ValueTask> call)
    {
        try
        {
            await call().AsTask().WaitAsync(StepTimeout);
            Expect(false, "the cancelled call was cancelled");
        }
        catch (OperationCanceledException) { }
    }

    // A context menu tracks in AppKit's event tracking mode; the work still runs, during the tracking.
    private static async Task ExerciseDuringContextMenuAsync(DesktopHostOptions hostOptions)
    {
        await using var host = await DesktopHost.StartAsync(hostOptions);
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Html("<!doctype html><html><head><script src=\"webui.js\"></script><title>Runic main thread</title></head><body>main</body></html>"),
        });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 320 });
        using var dismiss = new CancellationTokenSource();
        ImmutableArray<DesktopMenuEntry> items = [new DesktopMenuItem("Item", "item")];
        var menu = window.ShowContextMenuAsync(items, new DesktopContextMenuOptions { X = 40, Y = 30 }, dismiss.Token).AsTask();
        var deadline = DateTime.UtcNow + StepTimeout;
        while (Runic.Desktop.Internal.MacOsWkWebViewHost.TrackingContextMenu == 0)
        {
            Expect(DateTime.UtcNow < deadline, "a context menu tracks");
            await Task.Delay(20);
        }
        await Task.Delay(300);
        var (mode, tracking) = await DesktopEventLoop.InvokeOnMainThreadAsync(static () =>
            (CurrentRunLoopMode(), Runic.Desktop.Internal.MacOsWkWebViewHost.TrackingContextMenu != 0)).AsTask().WaitAsync(StepTimeout);
        dismiss.Cancel();
        try { await menu.WaitAsync(StepTimeout); }
        catch (OperationCanceledException) { }
        Expect(tracking && mode is not (null or "kCFRunLoopDefaultMode"), $"the work ran while the menu tracked, in mode {mode ?? "(none)"}");
        Pass($"work requested while a context menu tracks runs during the tracking ({mode}).");
    }

    private static string? CurrentRunLoopMode()
    {
        var mode = CopyCurrentMode(GetCurrentRunLoop());
        if (mode == 0) return null;
        try { return Text(mode); }
        finally { CFRelease(mode); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Main thread: expected " + message + ".");
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")] private static partial int PthreadMainNp();
    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);
    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopGetCurrent")] private static partial nint GetCurrentRunLoop();
    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopCopyCurrentMode")] private static partial nint CopyCurrentMode(nint runLoop);
}
