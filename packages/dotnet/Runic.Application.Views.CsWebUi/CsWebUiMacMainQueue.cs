using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Runic.Application.Views.CsWebUi;

// The macOS main dispatch queue, served from RunicCsWebUiHost.Run's main-thread loop. AppKit work without a window, such
// as Runic.Platform.MacOS's unowned file panels, is queued there; WebUI's browser-window wait blocks the main thread
// without running it, and only its WKWebView windows run AppKit's own loop ([NSApp run]). Serving the main run loop in the
// default mode runs the queue, as Runic Desktop does for a host without NSApp (MacOsApplication.ServeMainQueue).
[SupportedOSPlatform("macos")]
internal static class CsWebUiMacMainQueue
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // CFRunLoopRunInMode results that return at once: kCFRunLoopRunFinished and kCFRunLoopRunStopped.
    private const int Finished = 1, Stopped = 2;

    // kCFRunLoopDefaultMode, the CFString constant; 0 when CoreFoundation cannot be loaded.
    private static readonly Lazy<nint> DefaultMode = new(static () =>
        NativeLibrary.TryLoad(CoreFoundation, out var library) && NativeLibrary.TryGetExport(library, "kCFRunLoopDefaultMode", out var export)
            ? Marshal.ReadIntPtr(export) : 0);

    // Main thread. Runs the main run loop for up to `seconds`, returning after the first block or source it handles.
    // Returns false when it could not wait, so the caller waits another way instead of spinning.
    internal static bool Serve(double seconds)
    {
        var mode = DefaultMode.Value;
        if (mode == 0) return false;
        // Blocks that AppKit runs here autorelease objects; drain them per slice, as an event loop does.
        var pool = AutoreleasePoolPush();
        try { return RunInMode(mode, seconds, 1) is not (Finished or Stopped); }
        finally { AutoreleasePoolPop(pool); }
    }

    [DllImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static extern int RunInMode(nint mode, double seconds, byte returnAfterSourceHandled);

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern nint AutoreleasePoolPush();

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void AutoreleasePoolPop(nint pool);
}
