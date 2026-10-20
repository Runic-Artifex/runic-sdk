using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// The open-documents and get-URL Apple events that LaunchServices sends when the user opens a file or URL registered
// in the bundle's Info.plist, before and after launch. The handlers replace AppKit's, which would look for an
// NSApplication delegate that Runic does not set. finishLaunching installs AppKit's handlers and then dispatches the
// launch's own open event, so they are installed again when it posts NSApplicationWillFinishLaunchingNotification,
// and creating them after launch is refused.
internal static partial class MacOpenEvents
{
    // AEEventClass and AEEventID four-character codes.
    internal const uint CoreEventClass = 0x61657674, OpenDocuments = 0x6F646F63; // 'aevt', 'odoc'
    internal const uint InternetEventClass = 0x4755524C, GetUrl = 0x4755524C;    // 'GURL', 'GURL'
    private const uint DirectObject = 0x2D2D2D2D, FileUrlType = 0x6675726C;      // '----', 'furl'

    private static readonly Lock Gate = new();
    private static nint s_handler;
    private static OpenRequestQueue? s_queue;
    private static DesktopOpenOptions? s_options;

    internal static void Install(OpenRequestQueue queue, DesktopOpenOptions options)
    {
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
            if (s_handler == 0)
            {
                s_handler = Send(Send(HandlerClass.Value, Sel("alloc")), Sel("init"));
                // finishLaunching installs AppKit's own open-documents handler, then posts this notification before it
                // dispatches the launch's open event: install the handlers again there, as an application delegate
                // would in applicationWillFinishLaunching:.
                var center = Send(Class("NSNotificationCenter"), Sel("defaultCenter"));
                Args4(center, Sel("addObserver:selector:name:object:"), s_handler, Sel("willFinishLaunching:"),
                    String("NSApplicationWillFinishLaunchingNotification"), 0);
            }
            SetHandlers();
            s_queue = queue; s_options = options;
        }
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
        lock (Gate)
        {
            if (!ReferenceEquals(s_queue, queue)) return;
            s_queue = null; s_options = null;
        }
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
        RegisterClass(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WillFinishLaunching(nint self, nint selector, nint notification)
    {
        try { SetHandlers(); }
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

    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)] private static partial byte AddMethod(nint type, nint selector, nint implementation, string types);
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
