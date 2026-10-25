using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// The open-documents and get-URL Apple events that LaunchServices sends when the user opens a file or URL registered
// in the bundle's Info.plist, before and after launch, and universal links: NSUserActivity web-browsing activities,
// which AppKit hands only to the application delegate.
//
// Under Runic.Desktop's DesktopEventLoop.Run, Runic Desktop's application delegate owns the Apple event handlers from
// before the application's code runs and keeps the events and links until a handler attaches
// (runicAttachOpenEventHandler:); the handler here then receives them in order, so the open requests can be created
// at any time, on any thread.
//
// Without it, the handlers here replace AppKit's, which would look for an NSApplication delegate that Runic does not
// set. finishLaunching installs AppKit's handlers and then dispatches the launch's own open event, so they are
// installed again when it posts NSApplicationWillFinishLaunchingNotification, and creating them after launch is
// refused. With web link hosts, a Runic application delegate (or, when the application set its own, a method added to
// its class) receives universal links, before or after launch.
internal static partial class MacOpenEvents
{
    // The selectors of Runic Desktop's application delegate (Runic.Desktop's MacOsApplication).
    private const string AttachSelector = "runicAttachOpenEventHandler:", DetachSelector = "runicDetachOpenEventHandler:";
    // AEEventClass and AEEventID four-character codes.
    internal const uint CoreEventClass = 0x61657674, OpenDocuments = 0x6F646F63; // 'aevt', 'odoc'
    internal const uint InternetEventClass = 0x4755524C, GetUrl = 0x4755524C;    // 'GURL', 'GURL'
    private const uint DirectObject = 0x2D2D2D2D, FileUrlType = 0x6675726C;      // '----', 'furl'

    private static readonly Lock Gate = new();
    private const string ContinueActivity = "application:continueUserActivity:restorationHandler:";
    private const string BrowsingWeb = "NSUserActivityTypeBrowsingWeb";
    private static nint s_handler, s_delegate, s_observing, s_owner;
    private static OpenRequestQueue? s_queue;
    private static DesktopOpenOptions? s_options;

    internal static void Install(OpenRequestQueue queue, DesktopOpenOptions options)
    {
        // Runic Desktop's event loop owns the application: its delegate keeps the events until this handler attaches.
        if (LifecycleOwner() is not 0 and var owner)
        {
            lock (Gate)
            {
                if (s_queue is not null) throw new InvalidOperationException("The application already has open requests; dispose them first.");
                if (s_handler == 0) s_handler = Send(Send(HandlerClass.Value, Sel("alloc")), Sel("init"));
                s_queue = queue; s_options = options; s_owner = owner;
            }
            Arg(owner, Sel(AttachSelector), s_handler);
            return;
        }
        if (Bool(Class("NSThread"), Sel("isMainThread")) == 0)
            throw new InvalidOperationException("Create the open requests on the main thread, before the first window opens.");
        lock (Gate)
        {
            if (s_queue is not null) throw new InvalidOperationException("The application already has open requests; dispose them first.");
            // AppKit installs its own handlers when NSApp is created; create it first so these replace them.
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            if (Bool(Send(Class("NSRunningApplication"), Sel("currentApplication")), Sel("isFinishedLaunching")) != 0
                || Bool(application, Sel("isRunning")) != 0)
                throw new InvalidOperationException(
                    "Create the open requests before the first window or status icon: the system sends the launch's open event as the application finishes launching.");
            if (s_handler == 0) s_handler = Send(Send(HandlerClass.Value, Sel("alloc")), Sel("init"));
            if (s_observing == 0)
            {
                s_observing = s_handler;
                // finishLaunching installs AppKit's own open-documents handler, then posts this notification before it
                // dispatches the launch's open event: install the handlers again there, as an application delegate
                // would in applicationWillFinishLaunching:.
                var center = Send(Class("NSNotificationCenter"), Sel("defaultCenter"));
                Args4(center, Sel("addObserver:selector:name:object:"), s_handler, Sel("willFinishLaunching:"),
                    String("NSApplicationWillFinishLaunchingNotification"), 0);
            }
            SetHandlers();
            s_queue = queue; s_options = options;
            if (!options.WebLinkHosts.IsEmpty) SetDelegate();
        }
    }

    // Universal links reach only the application delegate: Runic's, or the method added to the application's own.
    internal static unsafe void SetDelegate()
    {
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        nint current = Send(application, Sel("delegate"));
        if (current == 0)
        {
            // NSApplication does not retain its delegate; this one lives for the process.
            if (s_delegate == 0) s_delegate = Send(Send(DelegateClass.Value, Sel("alloc")), Sel("init"));
            Arg(application, Sel("setDelegate:"), s_delegate);
        }
        else if (current != s_delegate && BoolArg(current, Sel("respondsToSelector:"), Sel(ContinueActivity)) == 0)
            AddMethod(ObjectClass(current), Sel(ContinueActivity), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, byte>)&Continue, ContinueTypes);
    }

    // BOOL is bool on arm64 and signed char on x86_64.
    private static string ContinueTypes => (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c") + "@:@@@?";

    private static readonly Lazy<nint> DelegateClass = new(CreateDelegateClass);

    private static unsafe nint CreateDelegateClass()
    {
        var type = AllocateClass(Class("NSObject"), "RunicApplicationDelegate", 0);
        if (type == 0) throw new InvalidOperationException("The application delegate class already exists.");
        string boolean = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
        AddMethod(type, Sel(ContinueActivity), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, byte>)&Continue, ContinueTypes);
        // Without an answer AppKit warns on macOS 14 and later; restorable state is secure-coded either way.
        AddMethod(type, Sel("applicationSupportsSecureRestorableState:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&SupportsSecureRestorableState, boolean + "@:@");
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte SupportsSecureRestorableState(nint self, nint selector, nint application) => 1;

    // A web-browsing activity whose page is on one of the web link hosts is an open request; AppKit handles every other.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Continue(nint self, nint selector, nint application, nint activity, nint restorationHandler)
    {
        try
        {
            using var pool = new Pool();
            OpenRequestQueue? queue; DesktopOpenOptions? options;
            lock (Gate) { queue = s_queue; options = s_options; }
            if (queue is null || options is null || options.WebLinkHosts.IsEmpty || activity == 0) return 0;
            if (Text(Send(activity, Sel("activityType"))) != BrowsingWeb) return 0;
            nint url = Send(activity, Sel("webpageURL"));
            if (url == 0) return 0;
            // Only the web link rules: a custom scheme or a file never arrives as a web-browsing activity.
            var request = DesktopOpenRequests.FromLaunch(new ApplicationLaunch([Text(Send(url, Sel("absoluteString")))], "/"),
                new DesktopOpenOptions([]) { WebLinkHosts = options.WebLinkHosts });
            if (request is null) return 0;
            queue.Post(request);
            return 1;
        }
        catch { return 0; /* A delegate method must not throw into AppKit; a malformed activity opens nothing. */ }
    }

    private static void SetHandlers()
    {
        var manager = Send(Class("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        var selector = Sel("handleEvent:withReplyEvent:");
        Args4(manager, Sel("setEventHandler:andSelector:forEventClass:andEventID:"), s_handler, selector, (nint)CoreEventClass, (nint)OpenDocuments);
        Args4(manager, Sel("setEventHandler:andSelector:forEventClass:andEventID:"), s_handler, selector, (nint)InternetEventClass, (nint)GetUrl);
    }

    internal static void Uninstall(OpenRequestQueue queue)
    {
        nint owner;
        lock (Gate)
        {
            if (!ReferenceEquals(s_queue, queue)) return;
            s_queue = null; s_options = null;
            owner = s_owner; s_owner = 0;
        }
        // Runic Desktop's delegate keeps later events for the next open requests.
        if (owner != 0) Arg(owner, Sel(DetachSelector), s_handler);
        // Events that arrive later find no queue and are ignored; the handlers stay installed so AppKit's do not
        // look for a delegate.
    }

    private static readonly Lazy<nint> HandlerClass = new(CreateHandlerClass);

    private static unsafe nint CreateHandlerClass()
    {
        var type = AllocateClass(Class("NSObject"), "RunicOpenEventHandler", 0);
        if (type == 0) throw new InvalidOperationException("The open event handler class already exists.");
        AddMethod(type, Sel("handleEvent:withReplyEvent:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&Handle, "v@:@@");
        AddMethod(type, Sel("willFinishLaunching:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WillFinishLaunching, "v@:@");
        // The universal links that Runic Desktop's application delegate hands on.
        AddMethod(type, Sel(ContinueActivity), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, byte>)&Continue, ContinueTypes);
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillFinishLaunching(nint self, nint selector, nint notification)
    {
        try
        {
            SetHandlers();
            DesktopOpenOptions? options;
            lock (Gate) options = s_options;
            // The application may have set its delegate since the open requests were created.
            if (options is { WebLinkHosts.IsEmpty: false }) SetDelegate();
        }
        catch { /* A notification observer must not throw into AppKit. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Handle(nint self, nint selector, nint appleEvent, nint reply)
    {
        try
        {
            using var pool = new Pool();
            OpenRequestQueue? queue; DesktopOpenOptions? options;
            lock (Gate) { queue = s_queue; options = s_options; }
            if (queue is null || options is null || appleEvent == 0) return;
            uint eventClass = (uint)Send(appleEvent, Sel("eventClass")), eventId = (uint)Send(appleEvent, Sel("eventID"));
            var direct = Arg(appleEvent, Sel("paramDescriptorForKeyword:"), (nint)DirectObject);
            if (direct == 0) return;
            var items = ImmutableArray.CreateBuilder<Uri>();
            if (eventClass == CoreEventClass && eventId == OpenDocuments && options.IncludeFiles)
            {
                // A list of file references; a single file may arrive as one descriptor rather than a list of one.
                long count = Send(direct, Sel("numberOfItems"));
                for (long index = 1; index <= Math.Max(count, 1) && items.Count < 1024; index++)
                {
                    var descriptor = count <= 0 ? direct : Arg(direct, Sel("descriptorAtIndex:"), (nint)index);
                    var file = Arg(descriptor, Sel("coerceToDescriptorType:"), (nint)FileUrlType);
                    var url = file == 0 ? 0 : Send(file, Sel("fileURLValue"));
                    if (url != 0 && Uri.TryCreate(Text(Send(url, Sel("absoluteString"))), UriKind.Absolute, out var uri) && uri.IsFile) items.Add(uri);
                }
            }
            else if (eventClass == InternetEventClass && eventId == GetUrl)
            {
                string text = Text(Send(direct, Sel("stringValue")));
                if (text.Length <= 32 * 1024 && Uri.TryCreate(text, UriKind.Absolute, out var uri) && !uri.IsFile
                    && options.UrlSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
                    items.Add(uri);
            }
            if (items.Count > 0) queue.Post(new DesktopOpenRequest(items.ToImmutable()));
        }
        catch { /* An Apple event handler must not throw into AppKit; a malformed event opens nothing. */ }
    }

    // NSApp's delegate when it is Runic Desktop's, which exists while DesktopEventLoop.Run runs. Reads NSApp without
    // creating it, so any thread may ask.
    internal static unsafe nint LifecycleOwner()
    {
        if (!NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out var appKit)
            || !NativeLibrary.TryGetExport(appKit, "NSApp", out var variable)) return 0;
        nint application = *(nint*)variable;
        if (application == 0) return 0;
        nint owner = Send(application, Sel("delegate"));
        return owner != 0 && BoolArg(owner, Sel("respondsToSelector:"), Sel(AttachSelector)) != 0 ? owner : 0;
    }

    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "object_getClass")] private static partial nint ObjectClass(nint value);
}

// Apple events and, through the single instance, the arguments of later launches, such as a command line's.
internal sealed class MacOpenRequests : IDesktopOpenRequests
{
    private readonly OpenRequestQueue _queue = new();

    internal MacOpenRequests(DesktopOpenOptions options, IApplicationInstance? instance, ApplicationLaunch? launch)
    {
        // Validates the options, and delivers this launch's arguments, as from a terminal.
        if (launch is not null && DesktopOpenRequests.FromLaunch(launch, options) is { } first) _queue.Post(first);
        else DesktopOpenRequests.FromLaunch(new ApplicationLaunch([], "/"), options);
        try { MacOpenEvents.Install(_queue, options); }
        catch { _queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
        ApplicationInstance.Attach(_queue, instance, options);
    }

    public event EventHandler<DesktopOpenRequest>? Opened { add => _queue.Opened += value; remove => _queue.Opened -= value; }

    public ValueTask DisposeAsync()
    {
        MacOpenEvents.Uninstall(_queue);
        return _queue.DisposeAsync();
    }
}
