using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The AppKit application that DesktopEventLoop.Run owns on macOS. Before application code runs, Run creates NSApp, sets
// Runic's application delegate and installs the open-documents and get-URL Apple event handlers; then the main thread
// runs [NSApp run], which sends finishLaunching once, and managed work reaches it through the main dispatch queue
// (MacOsMainThread). The delegate keeps the open events and universal links that arrive before Runic.Platform.MacOS's
// open requests attach their handler (runicAttachOpenEventHandler:), so the open requests can be created at any time:
// the launch's own open event, which AppKit sends while it finishes launching, is delivered when they attach.
internal static unsafe partial class MacOsApplication
{
    internal const string DelegateClassName = "RunicDesktopApplicationDelegate";
    // The selectors through which Runic.Platform.MacOS's open requests find the delegate (MacOpenEvents).
    internal const string AttachSelector = "runicAttachOpenEventHandler:", DetachSelector = "runicDetachOpenEventHandler:";
    private const string HandleOpenSelector = "runicHandleOpenEvent:withReplyEvent:";
    private const string ContinueActivity = "application:continueUserActivity:restorationHandler:";
    private const string BrowsingWeb = "NSUserActivityTypeBrowsingWeb";
    // AEEventClass and AEEventID four-character codes.
    private const uint CoreEventClass = 0x61657674, OpenDocuments = 0x6F646F63; // 'aevt', 'odoc'
    private const uint InternetEventClass = 0x4755524C, GetUrl = 0x4755524C;    // 'GURL', 'GURL'
    // Events kept while no open requests are attached; a later one is dropped.
    internal const int MaximumBuffered = 64;
    private const ulong EventTypeApplicationDefined = 15;
    private const nint ActivationPolicyAccessory = 1, ActivationPolicyProhibited = 2;
    private static readonly TimeSpan StopRetry = TimeSpan.FromMilliseconds(100);

    private static readonly Lock Gate = new();
    private static nint s_delegate;
    // The attached open-event handler, and one that attached but has not received the kept events yet.
    private static nint s_handler, s_pendingHandler;
    private static readonly List<(bool Activity, nint Value)> s_buffered = [];
    private static int s_willFinishLaunching;
    // Counts Run's loops, so a stop request of an earlier one does nothing.
    private static int s_generation;
    private static int s_loopEnded = 1;

    // Whether Run created the application and its delegate: AppKit's run sends finishLaunching then.
    internal static bool IsOwner => Volatile.Read(ref s_delegate) != 0;

    // How often AppKit sent applicationWillFinishLaunching:, which finishLaunching posts; for the native smoke.
    internal static int WillFinishLaunchingCount => Volatile.Read(ref s_willFinishLaunching);

    // The number of open events and universal links kept for the open requests; for the native smoke.
    internal static int BufferedCount { get { lock (Gate) return s_buffered.Count; } }

    // Main thread. Creates NSApp and sets Runic's delegate, which installs the open event handlers.
    internal static nint Start()
    {
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        if (Volatile.Read(ref s_delegate) == 0)
        {
            var type = DelegateClass.Value;
            if (type == 0) throw new InvalidOperationException("The Objective-C runtime could not create the Runic application delegate.");
            // NSApplication does not retain its delegate; this one lives for the process.
            Volatile.Write(ref s_delegate, Send(Send(type, Sel("alloc")), Sel("init")));
        }
        // An unbundled process starts with the prohibited activation policy. It launches as an accessory application,
        // without a Dock icon, as Runic.Platform.MacOS's windowless services and 0.11's first window launched it, until a
        // window makes it a regular one. Under the prohibited policy the main queue went unserved in processes that .NET
        // starts, such as test hosts and their child processes (finishLaunching is the suspect; not confirmed on hardware).
        if (Send(application, Sel("activationPolicy")) == ActivationPolicyProhibited)
            _ = Arg(application, Sel("setActivationPolicy:"), ActivationPolicyAccessory);
        // AppKit installs its own Apple event handlers when NSApp is created; these replace them.
        Arg(application, Sel("setDelegate:"), s_delegate);
        InstallEventHandlers();
        return application;
    }

    // Main thread. Runs [NSApp run] until the work completes; AppKit's run sends finishLaunching first.
    internal static void Run(nint application, Task work)
    {
        int generation = Interlocked.Increment(ref s_generation);
        Volatile.Write(ref s_loopEnded, 0);
        _ = work.ContinueWith(_ => RequestStop(generation), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var started = Environment.TickCount64;
            var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try { Send(application, Sel("run")); }
            finally { Send(pool, Sel("drain")); }
            if (!work.IsCompleted) ServeUntilCompleted(application, work, Environment.TickCount64 - started);
        }
        finally { Volatile.Write(ref s_loopEnded, 1); }
        // Work the application queued last, such as native release.
        RunLoopOnce();
    }

    // Stops [NSApp run] from any thread. stop: takes effect after the next event, so an event follows it; a modal
    // loop or menu tracking can take both, so the request repeats until the loop ends.
    private static void RequestStop(int generation) => MacOsMainThread.Post(() =>
    {
        if (Volatile.Read(ref s_generation) != generation || Volatile.Read(ref s_loopEnded) != 0) return;
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        Arg(application, Sel("stop:"), 0);
        var wake = OtherEvent(Class("NSEvent"), Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            EventTypeApplicationDefined, default, 0, 0, 0, 0, 0, 0, 0);
        if (wake != 0) PostEvent(application, Sel("postEvent:atStart:"), wake, 1);
        MacOsMainThread.PostAfter(StopRetry, () => RequestStop(generation));
    });

    // [NSApp run] returned before the work completed: another stop: ended it, or AppKit's loop did not run at all, as on
    // the macOS runner for unbundled processes that .NET starts (the sampled main thread never showed AppKit's loop, and
    // its next events returned at once without serving the main queue). Serves the main thread as 0.11's loop did: the
    // queued AppKit events, then the run loop for up to 50 ms, which runs the main dispatch queue; a run loop that has
    // nothing to wait for does not spin. The figures are for the native tests' diagnostics.
    internal static long RunReturnedEarlyAfterMilliseconds = -1;
    internal static readonly int[] FallbackRunLoopResults = new int[5];
    internal static int FallbackDirectDrains;

    private static readonly Lazy<nint> DrainMainQueue = new(static () =>
        NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out var library)
        && NativeLibrary.TryGetExport(library, "_dispatch_main_queue_callback_4CF", out var export) ? export : 0);

    private static void ServeUntilCompleted(nint application, Task work, long afterMilliseconds)
    {
        Volatile.Write(ref RunReturnedEarlyAfterMilliseconds, afterMilliseconds);
        var date = Send(Class("NSDate"), Sel("distantPast"));
        var mode = DefaultMode.Value;
        nint next = Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"), send = Sel("sendEvent:");
        nint poolClass = Class("NSAutoreleasePool"), alloc = Sel("alloc"), init = Sel("init"), drain = Sel("drain");
        while (!work.IsCompleted)
        {
            var pool = Send(Send(poolClass, alloc), init);
            try
            {
                for (var events = 0; events < 64; events++)
                {
                    var queued = NextEvent(application, next, ulong.MaxValue, date, mode, 1);
                    if (queued == 0) break;
                    Arg(application, send, queued);
                }
                int result = RunInMode(mode, 0.05, 1);
                Interlocked.Increment(ref FallbackRunLoopResults[Math.Clamp(result, 0, 4)]);
                // kCFRunLoopRunFinished or kCFRunLoopRunStopped return at once, without the main queue: run it directly,
                // as the run loop does (libdispatch's entry for CoreFoundation), and do not spin.
                if (result is 1 or 2)
                {
                    if (DrainMainQueue.Value is not 0 and var drainQueue)
                    {
                        ((delegate* unmanaged[Cdecl]<nint, void>)drainQueue)(0);
                        Interlocked.Increment(ref FallbackDirectDrains);
                    }
                    Thread.Sleep(5);
                }
            }
            finally { Send(pool, drain); }
        }
    }

    // kCFRunLoopDefaultMode, the CFString constant.
    private static readonly Lazy<nint> DefaultMode = new(static () =>
        NativeLibrary.TryLoad("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", out var library)
        && NativeLibrary.TryGetExport(library, "kCFRunLoopDefaultMode", out var export) ? *(nint*)export : 0);

    private static void RunLoopOnce()
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            _ = RunInMode(DefaultMode.Value, 0, 0);
        }
        finally { Send(pool, Sel("drain")); }
    }

    // Outside Run, as for the first window shown from the main thread without the event loop: finishLaunching once.
    private static int s_launchedWithoutOwner;

    internal static void EnsureLaunched(nint application)
    {
        if (IsOwner || Bool(application, Sel("isRunning")) != 0) return;
        if (Interlocked.Exchange(ref s_launchedWithoutOwner, 1) == 0) Send(application, Sel("finishLaunching"));
    }

    private static void InstallEventHandlers()
    {
        var manager = Send(Class("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        var handle = Sel(HandleOpenSelector);
        var install = Sel("setEventHandler:andSelector:forEventClass:andEventID:");
        SetHandler(manager, install, s_delegate, handle, CoreEventClass, OpenDocuments);
        SetHandler(manager, install, s_delegate, handle, InternetEventClass, GetUrl);
    }

    private static readonly Lazy<nint> DelegateClass = new(CreateDelegateClass);

    private static nint CreateDelegateClass()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        var existing = Class(DelegateClassName);
        if (existing != 0) return existing;
        var type = AllocateClass(Class("NSObject"), DelegateClassName, 0);
        if (type == 0) return 0;
        string boolean = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
        AddMethod(type, Sel("applicationWillFinishLaunching:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WillFinishLaunching, "v@:@");
        AddMethod(type, Sel(HandleOpenSelector), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&HandleOpenEvent, "v@:@@");
        AddMethod(type, Sel(ContinueActivity), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, byte>)&Continue, boolean + "@:@@@?");
        // Without an answer AppKit warns on macOS 14 and later; restorable state is secure-coded either way.
        AddMethod(type, Sel("applicationSupportsSecureRestorableState:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&SupportsSecureRestorableState, boolean + "@:@");
        AddMethod(type, Sel(AttachSelector), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&AttachHandler, "v@:@");
        AddMethod(type, Sel(DetachSelector), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&DetachHandler, "v@:@");
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillFinishLaunching(nint self, nint selector, nint notification)
    {
        try
        {
            Interlocked.Increment(ref s_willFinishLaunching);
            // finishLaunching installs AppKit's open-documents handler again before it sends the launch's open event.
            InstallEventHandlers();
        }
        catch { /* A delegate method must not throw into AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte SupportsSecureRestorableState(nint self, nint selector, nint application) => 1;

    // An open-documents or get-URL event: to the attached handler, else kept for it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleOpenEvent(nint self, nint selector, nint appleEvent, nint reply)
    {
        try
        {
            if (appleEvent == 0) return;
            nint handler;
            lock (Gate)
            {
                handler = s_handler;
                // A copy owns its descriptor, which AppKit releases after the handler returns.
                if (handler == 0) Keep(activity: false, Send(appleEvent, Sel("copy")));
            }
            if (handler != 0) Args(handler, Sel("handleEvent:withReplyEvent:"), appleEvent, reply);
        }
        catch { /* An Apple event handler must not throw into AppKit; a malformed event opens nothing. */ }
    }

    // A universal link: to the attached handler, else kept for it when it is a web-browsing activity.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Continue(nint self, nint selector, nint application, nint activity, nint restorationHandler)
    {
        try
        {
            if (activity == 0) return 0;
            nint handler;
            lock (Gate)
            {
                handler = s_handler;
                if (handler == 0)
                {
                    if (Text(Send(activity, Sel("activityType"))) != BrowsingWeb) return 0;
                    Keep(activity: true, Send(activity, Sel("retain")));
                    return 1;
                }
            }
            return ContinueWith(handler, Sel(ContinueActivity), application, activity, restorationHandler);
        }
        catch { return 0; /* A delegate method must not throw into AppKit. */ }
    }

    // Inside Gate. Takes ownership of the retained value.
    private static void Keep(bool activity, nint value)
    {
        if (value == 0) return;
        if (s_buffered.Count >= MaximumBuffered) { Send(value, Sel("release")); return; }
        s_buffered.Add((activity, value));
    }

    // From any thread: the open requests attach their handler. The kept events reach it on the main thread, in order,
    // before any later one.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AttachHandler(nint self, nint selector, nint handler)
    {
        try
        {
            if (handler == 0) return;
            lock (Gate)
            {
                if (s_pendingHandler != 0) Send(s_pendingHandler, Sel("release"));
                s_pendingHandler = Send(handler, Sel("retain"));
            }
            MacOsMainThread.Post(DeliverKept);
        }
        catch { /* A method called through the Objective-C runtime must not throw. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DetachHandler(nint self, nint selector, nint handler)
    {
        try
        {
            lock (Gate)
            {
                if (handler != 0 && s_pendingHandler == handler) { Send(s_pendingHandler, Sel("release")); s_pendingHandler = 0; }
                if (handler != 0 && s_handler == handler) { Send(s_handler, Sel("release")); s_handler = 0; }
            }
        }
        catch { /* A method called through the Objective-C runtime must not throw. */ }
    }

    // Main thread. Makes the pending handler the attached one and gives it the kept events.
    private static void DeliverKept()
    {
        nint handler;
        (bool Activity, nint Value)[] kept;
        lock (Gate)
        {
            if (s_pendingHandler == 0) return;
            if (s_handler != 0) Send(s_handler, Sel("release"));
            handler = s_handler = s_pendingHandler;
            s_pendingHandler = 0;
            kept = [.. s_buffered];
            s_buffered.Clear();
            // Retained for the deliveries below, which a detach on another thread must not outlive.
            Send(handler, Sel("retain"));
        }
        try
        {
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            foreach (var (activity, value) in kept)
            {
                var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
                try
                {
                    if (activity) _ = ContinueWith(handler, Sel(ContinueActivity), application, value, 0);
                    else Args(handler, Sel("handleEvent:withReplyEvent:"), value, 0);
                }
                catch { /* One malformed event does not stop the others. */ }
                finally
                {
                    Send(pool, Sel("drain"));
                    Send(value, Sel("release"));
                }
            }
        }
        finally { Send(handler, Sel("release")); }
    }

    private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Point(double X, double Y);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Args(nint target, nint selector, nint first, nint second);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte ContinueWith(nint target, nint selector, nint application, nint activity, nint restorationHandler);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetHandler(nint target, nint selector, nint handler, nint action, uint eventClass, uint eventId);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint NextEvent(nint target, nint selector, ulong mask, nint date, nint mode, byte dequeue);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void PostEvent(nint target, nint selector, nint appEvent, byte atStart);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint OtherEvent(nint target, nint selector, ulong type, Point location, ulong modifierFlags, double timestamp,
        nint windowNumber, nint context, short subtype, nint data1, nint data2);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRunLoopRunInMode")]
    private static partial int RunInMode(nint mode, double seconds, byte returnAfterSourceHandled);
}
