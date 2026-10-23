using System.Runtime.InteropServices;
using System.Threading.Channels;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.MacOS;
using TUnit.Core;
using static Runic.Platform.MacOS.MacDesktopNative;

// The open-documents and get-URL Apple events, dispatched through NSAppleEventManager's handler table on the main
// thread while the Desktop event loop runs (Program.cs runs the category inside it). CI runs it on the macOS runner under JIT and
// NativeAOT. A launch through LaunchServices with a registered bundle is the CI bundle script's job (RunBundleProbe).
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
internal sealed partial class MacOpenEventsNativeTests
{
    private const uint CoreEventClass = 0x61657674, OpenDocuments = 0x6F646F63, InternetEventClass = 0x4755524C, GetUrl = 0x4755524C;
    private const uint DirectObject = 0x2D2D2D2D;

    [Test, Explicit, Category("NativeOpenEvents"), DisplayName("native open events: odoc, GURL and universal links reach the open requests; creating them after launch fails"), Timeout(120_000)]
    public async Task AppleEventsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Apple events need macOS.");
        var requests = Channel.CreateUnbounded<DesktopOpenRequest>();
        IDesktopOpenRequests? opens = null;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            opens = MacOSPlatformProvider.CreateOpenRequests(new(["runic-test"]) { IncludeFiles = true, WebLinkHosts = ["links.runic.test"] }, null,
                new ApplicationLaunch(["runic-test://from-arguments"], "/"));
            // As the first window does.
            MacAppKit.EnsureApplication();
            // A universal link as AppKit hands it to the delegate while the application launches, before any handler.
            Check(ContinueActivity("NSUserActivityTypeBrowsingWeb", "https://links.runic.test/at-launch?id=1"), "the delegate takes a link to a web link host");
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            var applicationDelegate = Send(application, Sel("delegate"));
            Check(applicationDelegate != 0 && Text(Send(Send(applicationDelegate, Sel("class")), Sel("description"))) == "RunicApplicationDelegate", "Runic sets the application delegate");
            Check(BoolArg(applicationDelegate, Sel("applicationSupportsSecureRestorableState:"), application) != 0, "the delegate supports secure restorable state");
        });
        await using (opens!)
        {
            opens!.Opened += (_, request) => requests.Writer.TryWrite(request);
            string first = await NextAsync(requests, cancellationToken);
            Check(new Uri(first) == new Uri("runic-test://from-arguments"), $"this launch's arguments: {first}");
            string atLaunch = await NextAsync(requests, cancellationToken);
            Check(atLaunch == "https://links.runic.test/at-launch?id=1", $"the universal link at launch: {atLaunch}");

            string file = Path.Combine(Path.GetTempPath(), "runic open " + Guid.NewGuid().ToString("N")[..8] + ".txt");
            await File.WriteAllTextAsync(file, "probe", cancellationToken);
            try
            {
                // Dispatched through NSAppleEventManager's handler table, as AppKit does for an event it receives; the
                // bundle script checks the delivery by LaunchServices.
                await MacOsMainQueue.InvokeAsync(() =>
                {
                    using var pool = new Pool();
                    var list = Send(Class("NSAppleEventDescriptor"), Sel("listDescriptor"));
                    Args(list, Sel("insertDescriptor:atIndex:"), Arg(Class("NSAppleEventDescriptor"), Sel("descriptorWithFileURL:"), Url(file)), 0);
                    DispatchToSelf(CoreEventClass, OpenDocuments, list);
                    DispatchToSelf(InternetEventClass, GetUrl, Arg(Class("NSAppleEventDescriptor"), Sel("descriptorWithString:"), String("other-scheme://ignored")));
                    DispatchToSelf(InternetEventClass, GetUrl, Arg(Class("NSAppleEventDescriptor"), Sel("descriptorWithString:"), String("RUNIC-TEST://from-event?id=1")));
                });
                var opened = new Uri(await NextAsync(requests, cancellationToken));
                Check(opened.IsFile && opened.LocalPath == file, $"odoc opened the file: {opened}");
                string url = await NextAsync(requests, cancellationToken);
                Check(new Uri(url) == new Uri("runic-test://from-event?id=1"), $"GURL opened the registered URL and skipped the unregistered one: {url}");

                // Universal links while running: only web-browsing activities for a web link host.
                bool[] taken = [];
                await MacOsMainQueue.InvokeAsync(() => taken =
                [
                    ContinueActivity("NSUserActivityTypeBrowsingWeb", "https://other.runic.test/ignored"),
                    ContinueActivity("org.example.handoff", "https://links.runic.test/handoff"),
                    ContinueActivity("NSUserActivityTypeBrowsingWeb", "https://LINKS.runic.test/later#x"),
                ]);
                Check(taken.SequenceEqual([false, false, true]), $"only the web-browsing link to the host is taken: {string.Join(",", taken)}");
                string later = await NextAsync(requests, cancellationToken);
                Check(new Uri(later) == new Uri("https://links.runic.test/later#x"), $"the universal link later: {later}");

                // An application that sets its own delegate gets the method added to the delegate's class.
                nint own = 0, previous = 0;
                bool ownTaken = false, respondsBefore = true, respondsAfter = false;
                await MacOsMainQueue.InvokeAsync(() =>
                {
                    var application = Send(Class("NSApplication"), Sel("sharedApplication"));
                    previous = Send(application, Sel("delegate"));
                    own = Send(Send(OwnDelegateClass.Value, Sel("alloc")), Sel("init"));
                    Arg(application, Sel("setDelegate:"), own);
                    respondsBefore = BoolArg(own, Sel("respondsToSelector:"), Sel("application:continueUserActivity:restorationHandler:")) != 0;
                    MacOpenEvents.SetDelegate();
                    respondsAfter = BoolArg(own, Sel("respondsToSelector:"), Sel("application:continueUserActivity:restorationHandler:")) != 0;
                    ownTaken = ContinueActivity("NSUserActivityTypeBrowsingWeb", "https://links.runic.test/own-delegate");
                    Arg(application, Sel("setDelegate:"), previous);
                });
                Check(!respondsBefore && respondsAfter && ownTaken, $"the method is added to the application's delegate: {respondsBefore} {respondsAfter} {ownTaken}");
                Check(await NextAsync(requests, cancellationToken) == "https://links.runic.test/own-delegate", "the application's delegate delivers the link");
            }
            finally { File.Delete(file); }
        }

        // The launch's own open event is sent as the application finishes launching, so later creation is refused.
        Exception? refused = null;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            try { MacOSPlatformProvider.CreateOpenRequests(new(["runic-test"])).DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (InvalidOperationException error) { refused = error; }
        });
        Check(refused is not null, "creating open requests after the application finished launching throws");
        Console.WriteLine("PASS native open events: arguments, odoc and GURL delivered in order; late creation refused.");
    }

    // The process launched by LaunchServices from the bundle the CI script builds around the NativeAOT test executable:
    // it prints the first request's items beside the bundle and exits.
    internal static int RunBundleProbe(string bundle)
    {
        string output = Path.Combine(Path.GetDirectoryName(bundle)!, Path.GetFileNameWithoutExtension(bundle) + ".log");
        int exitCode = 1;
        DesktopEventLoop.Run(async () =>
        {
            var requests = Channel.CreateUnbounded<DesktopOpenRequest>();
            IDesktopOpenRequests? opens = null;
            await MacOsMainQueue.InvokeAsync(() =>
            {
                opens = MacOSPlatformProvider.CreateOpenRequests(new(["runic-open-probe"]) { IncludeFiles = true });
                MacAppKit.EnsureApplication();
            });
            await using (opens!)
            {
                opens!.Opened += (_, request) => requests.Writer.TryWrite(request);
                try
                {
                    var request = await requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
                    await File.WriteAllLinesAsync(output, request.Items.Select(static item => item.ToString()));
                    exitCode = 0;
                }
                catch (TimeoutException) { await File.WriteAllTextAsync(output, "timeout\n"); }
            }
        });
        return exitCode;
    }

    // Calls the application delegate's universal-link method as AppKit does for a continued activity. Main thread.
    private static bool ContinueActivity(string type, string url)
    {
        using var pool = new Pool();
        var activity = Arg(Send(Class("NSUserActivity"), Sel("alloc")), Sel("initWithActivityType:"), String(type));
        Arg(activity, Sel("setWebpageURL:"), Arg(Class("NSURL"), Sel("URLWithString:"), String(url)));
        var application = Send(Class("NSApplication"), Sel("sharedApplication"));
        var target = Send(application, Sel("delegate"));
        bool taken = Continue(target, Sel("application:continueUserActivity:restorationHandler:"), application, activity, 0) != 0;
        Release(activity);
        return taken;
    }

    // An application's own delegate class, without a universal-link method.
    private static readonly Lazy<nint> OwnDelegateClass = new(static () =>
    {
        var type = AllocateClass(Class("NSObject"), "RunicTestOwnApplicationDelegate", 0);
        RegisterClass(type);
        return type;
    });

    // Run from the open probe bundle before the CI script registers it with lsregister: the registration checks the
    // bundle's Info.plist and registers it with LSRegisterURL, after which Launch Services lists it for its scheme and
    // for plain text.
    internal static int RunRegistrationProbe()
    {
        try
        {
            var registration = MacOSPlatformProvider.CreateOpenRegistration();
            var probe = new DesktopOpenRegistration("Runic Open Probe") { UrlSchemes = ["runic-open-probe"], FileTypes = [new(".txt")] };
            var undeclared = new DesktopOpenRegistration("Runic Open Probe") { UrlSchemes = ["runic-undeclared-probe"], FileTypes = [new(".runic-undeclared")] };
            var refused = registration.RegisterAsync(undeclared).AsTask().GetAwaiter().GetResult();
            Check(refused is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData, Diagnostic.Message: { } message }
                && message.Contains("runic-undeclared-probe:", StringComparison.Ordinal) && message.Contains(".runic-undeclared", StringComparison.Ordinal),
                $"undeclared items are refused: {refused}");
            var registered = registration.RegisterAsync(probe).AsTask().GetAwaiter().GetResult();
            Check(registered.Succeeded, $"LSRegisterURL registers the bundle: {registered}");
            PlatformResult<bool> listed = new PlatformResult<bool>.Success(false);
            // Launch Services updates its database asynchronously.
            for (int attempt = 0; attempt < 40 && listed is not PlatformResult<bool>.Success(true); attempt++)
            {
                if (attempt > 0) Thread.Sleep(500);
                listed = registration.IsRegisteredAsync(probe).AsTask().GetAwaiter().GetResult();
            }
            Check(listed is PlatformResult<bool>.Success(true), $"Launch Services lists the bundle for its scheme and plain text: {listed}");
            Check(registration.IsRegisteredAsync(undeclared).AsTask().GetAwaiter().GetResult() is PlatformResult<bool>.Success(false), "undeclared items are not registered");
            Check(registration.UnregisterAsync(probe).AsTask().GetAwaiter().GetResult() is PlatformResult<bool>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "Launch Services has nothing to unregister");
            Console.WriteLine("PASS LSRegisterURL registered the bundle, which Launch Services lists; undeclared items refused.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error);
            return 1;
        }
    }

    private static unsafe void DispatchToSelf(uint eventClass, uint eventId, nint direct)
    {
        var target = Send(Class("NSAppleEventDescriptor"), Sel("currentProcessDescriptor"));
        var appleEvent = Create(Class("NSAppleEventDescriptor"), Sel("appleEventWithEventClass:eventID:targetDescriptor:returnID:transactionID:"),
            eventClass, eventId, target, -1, 0);
        Args(appleEvent, Sel("setParamDescriptor:forKeyword:"), direct, (nint)DirectObject);
        var reply = new AppleEventDescriptor { Type = 0x6E756C6C /* 'null' */ };
        var manager = Send(Class("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        short status = Dispatch(manager, Sel("dispatchRawAppleEvent:withRawReply:handlerRefCon:"), Send(appleEvent, Sel("aeDesc")), &reply, 0);
        if (status != 0) throw new InvalidOperationException($"Dispatching the Apple event failed with OSErr {status}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppleEventDescriptor { public uint Type; public nint Data; }

    private static async Task<string> NextAsync(Channel<DesktopOpenRequest> requests, CancellationToken cancellationToken) =>
        (await requests.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)).Items.Single().ToString();

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Continue(nint target, nint selector, nint application, nint activity, nint restorationHandler);
    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AllocateClass(nint parent, string name, nuint extra);
    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")] private static partial void RegisterClass(nint type);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Create(nint target, nint selector, uint eventClass, uint eventId, nint targetDescriptor, short returnId, int transactionId);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static unsafe partial short Dispatch(nint target, nint selector, nint appleEvent, AppleEventDescriptor* reply, nint referenceConstant);

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
