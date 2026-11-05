using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Runic.Application.Views.CsWebUi;

// The macOS main run loop, which RunicCsWebUiHost.Run's main thread blocks in while an application registers unowned file
// dialogs: it runs the main dispatch queue, where Runic.Platform.MacOS queues AppKit work without a window, and the
// blocks AppKit's modal panels need. WebUI's browser-window wait would block the main thread without running it; only its
// WKWebView windows run AppKit's own loop ([NSApp run]). The loop sleeps until work arrives: a main-queue block, a
// run-loop block, or Wake from another thread. No timer wakes it.
[SupportedOSPlatform("macos")]
internal static unsafe partial class CsWebUiMacMainQueue
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // kCFRunLoopRunFinished: the mode has nothing to wait for, so the loop returns at once.
    private const int Finished = 1;
    // As CFRunLoopRun: no timeout of its own.
    private const double Forever = 1.0e10;

    private static readonly Lazy<nint> CoreFoundationLibrary = new(static () =>
        NativeLibrary.TryLoad(CoreFoundation, out var library) ? library : 0);

    // kCFRunLoopDefaultMode, the CFString constant; 0 when CoreFoundation cannot be loaded.
    private static readonly Lazy<nint> DefaultMode = new(static () =>
        CoreFoundationLibrary.Value != 0 && NativeLibrary.TryGetExport(CoreFoundationLibrary.Value, "kCFRunLoopDefaultMode", out var export)
            ? Marshal.ReadIntPtr(export) : 0);

    // A version 0 source in the main run loop's default mode that Wake signals; its handling returns Serve. Created once,
    // from whichever thread first needs it, before the main thread first waits.
    private static readonly Lazy<nint> WakeSource = new(CreateWakeSource);

    // Main thread. Runs the main run loop until it handles a block or a source, such as Wake. Returns false when the loop
    // could not wait, so the caller waits another way instead of spinning.
    internal static bool Serve()
    {
        var mode = DefaultMode.Value;
        if (mode == 0 || WakeSource.Value == 0) return false;
        // Blocks that AppKit runs here autorelease objects; drain them per return, as an event loop does.
        var pool = AutoreleasePoolPush();
        try { return RunInMode(mode, Forever, 1) != Finished; }
        finally { AutoreleasePoolPop(pool); }
    }

    // Any thread: returns the main thread from Serve, or makes its next Serve return at once.
    internal static void Wake()
    {
        var source = WakeSource.Value;
        if (source == 0) return;
        SignalSource(source);
        WakeUp(GetMainRunLoop());
    }

    // Whether AppKit's own loop presents WebUI's windows: WebUI's WKWebView sets NSApp's delegate when it creates a window,
    // and its wait then runs [NSApp run] on the main thread. Reads NSApp without creating it.
    internal static bool WebUiRunsAppKit()
    {
        if (!NativeLibrary.TryLoad(AppKit, out var appKit) || !NativeLibrary.TryGetExport(appKit, "NSApp", out var export)) return false;
        nint application = Marshal.ReadIntPtr(export);
        return application != 0 && Send(application, RegisterName("delegate")) != 0;
    }

    private static nint CreateWakeSource()
    {
        var mode = DefaultMode.Value;
        if (mode == 0) return 0;
        var context = new SourceContext { Perform = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Perform };
        nint source = CreateSource(0, 0, &context);
        if (source != 0) AddSource(GetMainRunLoop(), source, mode);
        return source;
    }

    // Handling the source is what returns Serve.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Perform(nint info) { }

    // CFRunLoopSourceContext, version 0.
    [StructLayout(LayoutKind.Sequential)]
    private struct SourceContext
    {
        internal nint Version, Info, Retain, Release, CopyDescription, Equal, Hash, Schedule, Cancel, Perform;
    }

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static partial int RunInMode(nint mode, double seconds, byte returnAfterSourceHandled);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopGetMain")]
    private static partial nint GetMainRunLoop();

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopSourceCreate")]
    private static partial nint CreateSource(nint allocator, nint order, SourceContext* context);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopAddSource")]
    private static partial void AddSource(nint runLoop, nint source, nint mode);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopSourceSignal")]
    private static partial void SignalSource(nint source);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopWakeUp")]
    private static partial void WakeUp(nint runLoop);

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static partial nint AutoreleasePoolPush();

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static partial void AutoreleasePoolPop(nint pool);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint RegisterName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);
}
