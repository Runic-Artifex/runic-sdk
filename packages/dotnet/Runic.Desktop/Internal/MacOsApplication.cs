using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The AppKit application that DesktopEventLoop.Run owns on macOS (D045.2, D045.4). Before application code runs, Run creates NSApp, sets
// Runic's application delegate and installs the open-documents and get-URL Apple event handlers; then the main thread
// runs [NSApp run], which sends finishLaunching once, and managed work reaches it through the main dispatch queue
// (MacOsMainThread). The delegate keeps the open events and universal links that arrive before Runic.Platform.MacOS's
// open requests attach their handler (runicAttachOpenEventHandler:), so the open requests can be created at any time:
// the launch's own open event, which AppKit sends while it finishes launching, is delivered when they attach.
//
// Quitting takes one path (DesktopQuitCoordinator): terminate: (Quit, Command and Q, a status item's Quit) reaches
// applicationShouldTerminate:, which starts the quit and answers NSTerminateCancel; the quit Apple event (the Dock's
// Quit, a script, logging out, restarting) is suspended until the application's work returns, or answered with
// userCanceledErr when the application refuses. AppKit's own handling is not used for either: NSTerminateLater would run
// only the modal-panel run loop mode while the application decides, and terminate: would end the process with exit()
// before DesktopEventLoop.Run returns the application's result. Reopen raises DesktopHost.ReopenRequested; the Dock menu
// comes from MacOSDesktopOptions.DockMenu; everything else goes to MacOSDesktopOptions.ForwardingApplicationDelegate.
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
    private const uint QuitApplication = 0x71756974;                             // 'quit'
    private const uint DirectObject = 0x2D2D2D2D;                                 // '----'
    // keyAEQuitReason, which the system's quit event for log out, restart and shut down carries; keyErrorNumber.
    private const uint QuitReason = 0x7768793F, ErrorNumber = 0x6572726E;         // 'why?', 'errn'
    private const string HandleQuitSelector = "runicHandleQuitEvent:withReplyEvent:";
    private const ulong TerminateCancel = 0, TerminateNow = 1;
    // Events kept while no open requests are attached; a later one is dropped.
    internal const int MaximumBuffered = 64;
    private const ulong EventTypeApplicationDefined = 15;
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
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
    // The application delegate of the application's own (MacOSDesktopOptions.ForwardingApplicationDelegate), retained.
    private static nint s_forward;
    // Quit Apple events suspended until the application's work returns. Main thread only.
    private static readonly List<nint> s_pendingQuitReplies = [];
    // The error numbers of the quit replies Runic resumed, in order (0 for success); for the native smoke.
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<int> ResumedQuitReplies = new();
    // Set when Runic itself terminates the process, which applicationShouldTerminate: then allows.
    private static int s_terminating;

    // Whether Run created the application and its delegate: AppKit's run sends finishLaunching then.
    internal static bool IsOwner => Volatile.Read(ref s_delegate) != 0;

    // How often AppKit sent applicationWillFinishLaunching:, which finishLaunching posts; for the native smoke.
    internal static int WillFinishLaunchingCount => Volatile.Read(ref s_willFinishLaunching);

    // The number of open events and universal links kept for the open requests; for the native smoke.
    internal static int BufferedCount { get { lock (Gate) return s_buffered.Count; } }

    // Main thread. Creates NSApp and sets Runic's delegate, which installs the open event handlers.
    internal static nint Start()
    {
        // objc_getClass finds NSApplication only once AppKit is loaded. Nothing else may have loaded it yet, as in a
        // process that uses no Desktop window before the loop: [nil run] would return at once.
        if (!NativeLibrary.TryLoad(AppKitPath, out _) || Class("NSApplication") == 0)
            throw new PlatformNotSupportedException("AppKit is required for the Desktop event loop on macOS.");
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        if (application == 0) throw new InvalidOperationException("AppKit did not create the shared NSApplication.");
        if (Volatile.Read(ref s_delegate) == 0)
        {
            var type = DelegateClass.Value;
            if (type == 0) throw new InvalidOperationException("The Objective-C runtime could not create the Runic application delegate.");
            // NSApplication does not retain its delegate; this one lives for the process.
            Volatile.Write(ref s_delegate, Send(Send(type, Sel("alloc")), Sel("init")));
        }
        AdoptForwardingDelegate(FirstForwardingDelegate());
        // AppKit installs its own Apple event handlers when NSApp is created; these replace them. AppKit asks the
        // delegate which methods it implements when it is set, after the forwarding delegate is known.
        Arg(application, Sel("setDelegate:"), s_delegate);
        InstallEventHandlers();
        return application;
    }

    // Main thread. Runs [NSApp run] until the work completes; AppKit's run sends finishLaunching first.
    internal static void Run(nint application, Task work)
    {
        int generation = Interlocked.Increment(ref s_generation);
        Volatile.Write(ref s_loopEnded, 0);
        StopsSentBeforeRun = Volatile.Read(ref s_stopsSent);
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
        // The application returned after a quit Apple event: the Dock, the script or the logout learns that it quit.
        ResumeQuitReplies(refused: false);
    }

    // Stops [NSApp run] from any thread. stop: takes effect after the next event, so an event follows it; a modal
    // loop or menu tracking can take both, so the request repeats until the loop ends.
    private static void RequestStop(int generation) => MacOsMainThread.Post(() =>
    {
        if (Volatile.Read(ref s_generation) != generation || Volatile.Read(ref s_loopEnded) != 0) return;
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        Interlocked.Increment(ref s_stopsSent);
        Arg(application, Sel("stop:"), 0);
        var wake = OtherEvent(Class("NSEvent"), Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            EventTypeApplicationDefined, default, 0, 0, 0, 0, 0, 0, 0);
        if (wake != 0) PostEvent(application, Sel("postEvent:atStart:"), wake, 1);
        MacOsMainThread.PostAfter(StopRetry, () => RequestStop(generation));
    });

    // [NSApp run] returned before the work completed, because something else sent stop:. Serves the main thread with
    // public API until the work completes: the queued AppKit events, then the run loop in the default mode for up to
    // 50 ms, which runs the main dispatch queue; a run loop that returns at once is not spun. The figures are for the
    // native tests' diagnostics.
    internal static long RunReturnedEarlyAfterMilliseconds = -1;
    internal static readonly int[] FallbackRunLoopResults = new int[5];
    // Run's loops, and the stop: requests sent, in this process; the stops sent before the current loop started.
    internal static int Generation => Volatile.Read(ref s_generation);
    internal static int StopsSent => Volatile.Read(ref s_stopsSent);
    internal static int StopsSentBeforeRun { get; private set; }
    private static int s_stopsSent;

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
                // kCFRunLoopRunFinished or kCFRunLoopRunStopped return at once.
                if (result is 1 or 2) Thread.Sleep(5);
            }
            finally { Send(pool, drain); }
        }
    }

    // Main thread, without NSApp (a host that presents no window): runs the main run loop in the default mode, which runs
    // the main dispatch queue, until the work completes. An empty block queued when it completes wakes the loop.
    internal static void ServeMainQueue(Task work)
    {
        _ = work.ContinueWith(static _ => MacOsMainThread.Post(static () => { }), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var mode = DefaultMode.Value;
        while (!work.IsCompleted)
        {
            var pool = AutoreleasePoolPush();
            try
            {
                // kCFRunLoopRunFinished or kCFRunLoopRunStopped return at once.
                if (RunInMode(mode, 1, 1) is 1 or 2) Thread.Sleep(5);
            }
            finally { AutoreleasePoolPop(pool); }
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
        SetHandler(manager, install, s_delegate, Sel(HandleQuitSelector), CoreEventClass, QuitApplication);
    }

    // A host started or was disposed: the first one with a forwarding delegate supplies it.
    internal static void HostsChanged()
    {
        if (!IsOwner) return;
        var forward = FirstForwardingDelegate();
        if (forward == 0 || forward == Volatile.Read(ref s_forward)) return;
        MacOsMainThread.Post(() =>
        {
            if (!AdoptForwardingDelegate(forward)) return;
            // AppKit caches which methods the delegate implements when it is set.
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            if (Send(application, Sel("delegate")) != s_delegate) return;
            Arg(application, Sel("setDelegate:"), 0);
            Arg(application, Sel("setDelegate:"), s_delegate);
        });
    }

    private static nint FirstForwardingDelegate() =>
        DesktopHost.LiveHosts().Select(static host => host.Options.MacOS.ForwardingApplicationDelegate).FirstOrDefault(static forward => forward != 0);

    // Keeps the first forwarding delegate for the process; later ones do not replace it.
    private static bool AdoptForwardingDelegate(nint forward)
    {
        if (forward == 0 || Volatile.Read(ref s_forward) != 0) return false;
        Volatile.Write(ref s_forward, Send(forward, Sel("retain")));
        return true;
    }

    // The forwarding delegate when it implements the selector.
    private static nint Forward(nint selector)
    {
        var forward = Volatile.Read(ref s_forward);
        return forward != 0 && BoolArg(forward, Sel("respondsToSelector:"), selector) != 0 ? forward : 0;
    }

    // From the main thread: quit through the one path. The suspension is that of a quit Apple event, or 0.
    private static void StartQuit(DesktopQuitSource source, nint suspension)
    {
        if (suspension != 0) lock (s_pendingQuitReplies) s_pendingQuitReplies.Add(suspension);
        _ = DesktopQuitCoordinator.RequestAsync(source).ContinueWith(static quit =>
        {
            var outcome = quit.IsCompletedSuccessfully ? quit.Result : DesktopQuitOutcome.Refused;
            MacOsMainThread.Post(() => FinishQuit(outcome));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Main thread.
    private static void FinishQuit(DesktopQuitOutcome outcome)
    {
        switch (DesktopQuitCoordinator.ReplyFor(outcome))
        {
            case DesktopQuitReply.Refuse:
                ResumeQuitReplies(refused: true);
                break;
            case DesktopQuitReply.AfterApplication:
                // The replies wait until the application's work returns (Run).
                break;
            default:
                // Nothing would end the application (no host, or no surface without HandlesShutdownWithoutWindow, as for
                // a signal): AppKit terminates the process, as it would have without Runic.
                ResumeQuitReplies(refused: false);
                Terminate();
                break;
        }
    }

    private static void Terminate()
    {
        Volatile.Write(ref s_terminating, 1);
        Arg(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("terminate:"), 0);
    }

    // Main thread. Answers the suspended quit Apple events: success, or userCanceledErr when the application refused.
    private static void ResumeQuitReplies(bool refused)
    {
        nint[] suspensions;
        lock (s_pendingQuitReplies)
        {
            suspensions = [.. s_pendingQuitReplies];
            s_pendingQuitReplies.Clear();
        }
        if (suspensions.Length == 0) return;
        var manager = Send(Class("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        int error = refused ? DesktopQuitCoordinator.UserCanceled : 0;
        foreach (var suspension in suspensions)
        {
            try
            {
                if (refused)
                {
                    // The reply event of the suspended quit event, which the sender receives when it resumes.
                    var reply = Arg(manager, Sel("replyAppleEventForSuspensionID:"), suspension);
                    if (reply != 0)
                        SetParam(reply, Sel("setParamDescriptor:forKeyword:"),
                            Int32Descriptor(Class("NSAppleEventDescriptor"), Sel("descriptorWithInt32:"), error), ErrorNumber);
                }
                Arg(manager, Sel("resumeWithSuspensionID:"), suspension);
                ResumedQuitReplies.Enqueue(error);
            }
            catch { /* A reply that cannot be sent leaves the sender to its timeout. */ }
        }
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
        AddMethod(type, Sel(HandleQuitSelector), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&HandleQuitEvent, "v@:@@");
        // NSApplicationTerminateReply is an NSUInteger.
        AddMethod(type, Sel("applicationShouldTerminate:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nuint>)&ShouldTerminate, "Q@:@");
        AddMethod(type, Sel("applicationShouldHandleReopen:hasVisibleWindows:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte, byte>)&ShouldHandleReopen,
            boolean + "@:@" + boolean);
        AddMethod(type, Sel("applicationDockMenu:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&DockMenu, "@@:@");
        // Forwarding to the application's own delegate.
        AddMethod(type, Sel("respondsToSelector:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&RespondsToSelector, boolean + "@::");
        AddMethod(type, Sel("forwardingTargetForSelector:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&ForwardingTarget, "@@::");
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
            if (Forward(selector) is not 0 and var forward) Arg(forward, selector, notification);
        }
        catch { /* A delegate method must not throw into AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte SupportsSecureRestorableState(nint self, nint selector, nint application)
    {
        try { return Forward(selector) is not 0 and var forward ? BoolArg(forward, selector, application) : (byte)1; }
        catch { return 1; }
    }

    // terminate: from Quit, Command and Q or any other sender. Runic starts its quit and cancels AppKit's, which would
    // end the process before DesktopEventLoop.Run returns; without a Desktop host, or when Runic itself terminates, AppKit
    // goes ahead.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint ShouldTerminate(nint self, nint selector, nint application)
    {
        try
        {
            if (Volatile.Read(ref s_terminating) != 0 || DesktopHost.LiveHosts().Length == 0) return (nuint)TerminateNow;
            StartQuit(DesktopQuitSource.Menu, 0);
            return (nuint)TerminateCancel;
        }
        catch { return (nuint)TerminateCancel; /* A delegate method must not throw into AppKit. */ }
    }

    // The quit Apple event: the Dock's Quit, a script, or the system's for log out, restart and shut down, which names
    // its reason. The reply waits until the application quit or refused.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleQuitEvent(nint self, nint selector, nint appleEvent, nint reply)
    {
        try
        {
            if (DesktopHost.LiveHosts().Length == 0)
            {
                Terminate();
                return;
            }
            var source = appleEvent != 0 && Arg(appleEvent, Sel("paramDescriptorForKeyword:"), (nint)QuitReason) != 0
                ? DesktopQuitSource.System
                : DesktopQuitSource.Dock;
            var suspension = Send(Send(Class("NSAppleEventManager"), Sel("sharedAppleEventManager")), Sel("suspendCurrentAppleEvent"));
            StartQuit(source, suspension);
        }
        catch { /* An Apple event handler must not throw into AppKit. */ }
    }

    // A click on the Dock icon, or opening the running application again.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ShouldHandleReopen(nint self, nint selector, nint application, byte hasVisibleWindows)
    {
        try
        {
            foreach (var host in DesktopHost.LiveHosts()) host.RaiseReopenRequested(hasVisibleWindows != 0);
            // YES lets AppKit deminiaturize a minimized window when none is visible.
            return Forward(selector) is not 0 and var forward ? ReopenWith(forward, selector, application, hasVisibleWindows) : (byte)1;
        }
        catch { return 1; /* A delegate method must not throw into AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint DockMenu(nint self, nint selector, nint application)
    {
        try
        {
            var entries = DesktopHost.LiveHosts().Select(static host => host.Options.MacOS.DockMenu).FirstOrDefault(static menu => menu.Count > 0);
            if (entries is not null) return MacOsWkWebViewHost.CreateDockMenu(entries);
            return Forward(selector) is not 0 and var forward ? Arg(forward, selector, application) : 0;
        }
        catch { return 0; /* A delegate method must not throw into AppKit. */ }
    }

    // Runic's methods, then the forwarding delegate's.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte RespondsToSelector(nint self, nint selector, nint asked)
    {
        try { return ClassResponds(DelegateClass.Value, asked) != 0 || Forward(asked) != 0 ? (byte)1 : (byte)0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint ForwardingTarget(nint self, nint selector, nint asked)
    {
        try { return Forward(asked); }
        catch { return 0; }
    }

    // An open-documents or get-URL event: to the attached handler, else kept for it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HandleOpenEvent(nint self, nint selector, nint appleEvent, nint reply)
    {
        try { DeliverOpenEvent(appleEvent, reply); }
        catch { /* An Apple event handler must not throw into AppKit; a malformed event opens nothing. */ }
    }

    private static void DeliverOpenEvent(nint appleEvent, nint reply)
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

    // A document chosen in Runic's Open Recent menu (MacOsWkWebViewHost.Documents) reaches the open requests as the
    // open-documents event that the Dock's recent documents and the Finder send, so the application receives it through
    // the same IDesktopOpenRequests.Opened. Main thread: the file URLs become one open-documents Apple event addressed to
    // this process.
    internal static bool OpenFileUrls(nint[] urls)
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            var descriptor = Class("NSAppleEventDescriptor");
            var list = Send(descriptor, Sel("listDescriptor"));
            foreach (var url in urls)
            {
                // Index 0 appends.
                if (url != 0 && Bool(url, Sel("isFileURL")) != 0)
                    Args(list, Sel("insertDescriptor:atIndex:"), Arg(descriptor, Sel("descriptorWithFileURL:"), url), 0);
            }
            if (Send(list, Sel("numberOfItems")) == 0) return false;
            // kAutoGenerateReturnID and kAnyTransactionID.
            var appleEvent = CreateEvent(descriptor, Sel("appleEventWithEventClass:eventID:targetDescriptor:returnID:transactionID:"),
                CoreEventClass, OpenDocuments, Send(descriptor, Sel("currentProcessDescriptor")), -1, 0);
            if (appleEvent == 0) return false;
            SetParam(appleEvent, Sel("setParamDescriptor:forKeyword:"), list, DirectObject);
            DeliverOpenEvent(appleEvent, 0);
            return true;
        }
        finally { Send(pool, Sel("drain")); }
    }

    // A universal link: to the attached handler, else kept for it when it is a web-browsing activity.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Continue(nint self, nint selector, nint application, nint activity, nint restorationHandler)
    {
        try
        {
            if (activity == 0) return 0;
            bool browsing = Text(Send(activity, Sel("activityType"))) == BrowsingWeb;
            nint handler;
            lock (Gate)
            {
                handler = s_handler;
                if (handler == 0 && browsing)
                {
                    Keep(activity: true, Send(activity, Sel("retain")));
                    return 1;
                }
            }
            if (handler != 0 && ContinueWith(handler, Sel(ContinueActivity), application, activity, restorationHandler) != 0) return 1;
            // Not one of the application's universal links: the application's own delegate may take it.
            return Forward(selector) is not 0 and var forward ? ContinueWith(forward, selector, application, activity, restorationHandler) : (byte)0;
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
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte BoolArg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte ReopenWith(nint target, nint selector, nint application, byte hasVisibleWindows);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetParam(nint target, nint selector, nint descriptor, uint keyword);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Int32Descriptor(nint target, nint selector, int value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint CreateEvent(nint target, nint selector, uint eventClass, uint eventId, nint targetDescriptor, short returnId, int transactionId);
    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")] private static partial nint AutoreleasePoolPush();
    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")] private static partial void AutoreleasePoolPop(nint pool);
    [LibraryImport(ObjC, EntryPoint = "class_respondsToSelector")] private static partial byte ClassResponds(nint type, nint selector);
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
