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
internal sealed partial class MacOpenEventsNativeTests
{
    private const uint CoreEventClass = 0x61657674, OpenDocuments = 0x6F646F63, InternetEventClass = 0x4755524C, GetUrl = 0x4755524C;
    private const uint DirectObject = 0x2D2D2D2D;

    [Test, Explicit, Category("NativeOpenEvents"), DisplayName("native open events: odoc and GURL reach the open requests; creating them after launch fails"), Timeout(120_000)]
    public async Task AppleEventsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Apple events need macOS.");
        var requests = Channel.CreateUnbounded<DesktopOpenRequest>();
        IDesktopOpenRequests? opens = null;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            opens = MacOSPlatformProvider.CreateOpenRequests(new(["runic-test"]) { IncludeFiles = true }, null, new ApplicationLaunch(["runic-test://from-arguments"], "/"));
            // As the first window does.
            MacAppKit.EnsureApplication();
        });
        await using (opens!)
        {
            opens!.Opened += (_, request) => requests.Writer.TryWrite(request);
            string first = await NextAsync(requests, cancellationToken);
            Check(new Uri(first) == new Uri("runic-test://from-arguments"), $"this launch's arguments: {first}");

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

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Create(nint target, nint selector, uint eventClass, uint eventId, nint targetDescriptor, short returnId, int transactionId);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static unsafe partial short Dispatch(nint target, nint selector, nint appleEvent, AppleEventDescriptor* reply, nint referenceConstant);

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
