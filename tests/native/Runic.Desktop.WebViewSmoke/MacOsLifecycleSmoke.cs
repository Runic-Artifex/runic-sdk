using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;

// M1 (D045.2, D045.4) on the macOS runner: the application lifecycle that DesktopEventLoop.Run owns. finishLaunching
// once, Runic's application delegate, the Dock menu and its commands, reopen, and the quit Apple event: refused by
// ConfirmQuitAsync, refused by a window's close guard, and accepted with no window open, which cancels
// ShutdownRequested and lets Run return the application's result. The events are sent to the process itself from the
// main thread, where AppKit handles them, as the Dock, a script or loginwindow send them (the logout one names its
// reason). The Apple Event Manager hands an event a process sends itself straight to the handler, and AESend reports
// errAETimeout as soon as Runic's handler suspends it to wait for the decision, so the sender cannot see the reply that
// Runic resumes later: the smoke reads the replies Runic resumed (MacOsApplication.ResumedQuitReplies) instead. Another
// process, such as the Dock, receives the reply when Runic resumes it. Run with --lifecycle on macOS.
internal static partial class MacOsLifecycleSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);
    private const int ApplicationResult = 7;
    private const uint CoreEventClass = 0x61657674, QuitApplication = 0x71756974, ReopenApplication = 0x72617070; // 'aevt', 'quit', 'rapp'
    private const uint QuitReason = 0x7768793F, LogOut = 0x6C6F676F;                                               // 'why?', 'logo'
    private const int UserCanceled = -128;
    private const long AppleEventTimeout = -1712;

    internal static void Run(DesktopHostOptions hostOptions)
    {
        var sources = new ConcurrentQueue<DesktopQuitSource>();
        var quitAnswer = 0;
        var options = hostOptions with
        {
            HandlesShutdownWithoutWindow = true,
            ConfirmQuitAsync = (request, _) =>
            {
                sources.Enqueue(request.Source);
                return ValueTask.FromResult(Volatile.Read(ref quitAnswer) != 0);
            },
            MacOS = new MacOSDesktopOptions
            {
                DockMenu = [new DesktopMenuItem("Smoke Command", "smoke.dock"), new DesktopMenuSeparator(), new DesktopMenuItem("Unavailable", "smoke.unavailable")],
            },
        };
        int result = DesktopEventLoop.Run(options, async host =>
        {
            await ExpectLaunchedAsync();
            await ExerciseDockMenuAsync(host);

            var closeRequests = 0;
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><html><head><script src=\"webui.js\"></script><title>Runic lifecycle</title></head><body>lifecycle</body></html>"),
            });
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 480,
                Height = 320,
                // The guard refuses every time: a quit the application accepted still keeps this window.
                ConfirmCloseAsync = _ => { Interlocked.Increment(ref closeRequests); return ValueTask.FromResult(false); },
            });
            await ExerciseReopenAsync(host);

            // The logout's quit event: ConfirmQuitAsync refuses, so no close guard is asked.
            await SendAsync(QuitApplication, reason: LogOut);
            var refusedLogout = await ReplyAsync(1);
            Expect(refusedLogout == UserCanceled, $"the refused logout's reply carries userCanceledErr, not {refusedLogout}");
            Expect(sources.Count == 1, $"ConfirmQuitAsync was asked once for the logout: {string.Join(",", sources)}");
            Expect(sources.Last() == DesktopQuitSource.System, $"the logout's source is System, not {sources.Last()}");
            Expect(Volatile.Read(ref closeRequests) == 0 && window.IsOpen && !host.ShutdownRequested.IsCancellationRequested,
                "a quit ConfirmQuitAsync refused asks no close guard, keeps the window and does not cancel ShutdownRequested");
            Pass("the logout's quit event asks ConfirmQuitAsync (System), whose refusal keeps the application running.");

            // The Dock's quit event: the application accepts, the window's close guard refuses.
            Volatile.Write(ref quitAnswer, 1);
            await SendAsync(QuitApplication, reason: null);
            var refusedByGuard = await ReplyAsync(2);
            Expect(refusedByGuard == UserCanceled, $"the reply of a quit a close guard refused carries userCanceledErr, not {refusedByGuard}");
            Expect(Volatile.Read(ref closeRequests) == 1, $"the close guard was asked once after ConfirmQuitAsync agreed: {closeRequests}");
            Expect(sources.Last() == DesktopQuitSource.Dock, $"the Dock's source is Dock, not {sources.Last()}");
            Expect(window.IsOpen && !host.ShutdownRequested.IsCancellationRequested,
                "a close guard that refuses keeps its window and the application running");
            Pass("the Dock's quit event asks ConfirmQuitAsync (Dock), then the close guard, which can keep the window.");

            await window.CloseAsync();
            await window.DisposeAsync();

            // With no window open, the quit event cancels ShutdownRequested; the application then returns, and its reply
            // follows when Run's loop ends.
            await SendAsync(QuitApplication, reason: null);
            await Task.Delay(Timeout.Infinite, host.ShutdownRequested).ContinueWith(static _ => { }, TaskScheduler.Default).WaitScaledAsync(StepTimeout);
            Expect(sources.Count == 3 && sources.Last() == DesktopQuitSource.Dock, $"the third quit asked ConfirmQuitAsync: {string.Join(",", sources)}");
            Pass("the quit event with no window open cancels ShutdownRequested.");
            return ApplicationResult;
        });
        Expect(result == ApplicationResult, $"DesktopEventLoop.Run returned {result} instead of the application's result after the quit");
        var replies = MacOsApplication.ResumedQuitReplies.ToArray();
        Expect(replies is [UserCanceled, UserCanceled, 0], $"the accepted quit's reply was resumed with success when Run's loop ended: {string.Join(",", replies)}");
        Expect(MacOsApplication.WillFinishLaunchingCount == 1, $"finishLaunching was sent {MacOsApplication.WillFinishLaunchingCount} times");
        Pass("DesktopEventLoop.Run returned the application's result after the quit event, whose reply then reported success.");
    }

    private static async Task ExpectLaunchedAsync()
    {
        string delegateClass = "";
        await WaitAsync(() => MacOsApplication.WillFinishLaunchingCount > 0, "applicationWillFinishLaunching:");
        await MacOsMainThread.InvokeAsync(() =>
        {
            var applicationDelegate = Send(Application(), Sel("delegate"));
            delegateClass = Text(Send(Send(applicationDelegate, Sel("class")), Sel("description")));
        }, CancellationToken.None);
        Expect(delegateClass == "RunicDesktopApplicationDelegate", $"NSApp's delegate is {delegateClass}");
        Expect(MacOsApplication.WillFinishLaunchingCount == 1, "finishLaunching was sent once");
        Pass("DesktopEventLoop.Run set Runic's application delegate before the application started and launched once.");
    }

    private sealed class DockTarget : IDesktopMenuCommandTarget
    {
        internal readonly TaskCompletionSource<string> Executed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanExecute(DesktopMenuItem item) => item.CommandId == "smoke.dock";
        public void Execute(DesktopMenuItem item) => Executed.TrySetResult(item.CommandId);
    }

    // applicationDockMenu: as the Dock asks it when its menu opens; choosing the item runs host.MenuCommands.
    private static async Task ExerciseDockMenuAsync(DesktopHost host)
    {
        var target = new DockTarget();
        host.MenuCommands = target;
        string titles = "";
        bool[] enabled = [];
        await MacOsMainThread.InvokeAsync(() =>
        {
            var application = Application();
            var menu = Arg(Send(application, Sel("delegate")), Sel("applicationDockMenu:"), application);
            Expect(menu != 0, "the delegate returns a Dock menu");
            var count = (int)Send(menu, Sel("numberOfItems"));
            var items = Enumerable.Range(0, count).Select(index => Arg(menu, Sel("itemAtIndex:"), index)).ToArray();
            titles = string.Join("|", items.Select(item => Text(Send(item, Sel("title")))));
            enabled = [.. items.Select(item => Bool(item, Sel("isEnabled")) != 0)];
            Arg(menu, Sel("performActionForItemAtIndex:"), 0);
        }, CancellationToken.None);
        Expect(titles == "Smoke Command||Unavailable", $"the Dock menu items: {titles}");
        Expect(enabled is [true, _, false], $"the items follow CanExecute: {string.Join(",", enabled)}");
        Expect(await target.Executed.Task.WaitScaledAsync(StepTimeout) == "smoke.dock", "the chosen item runs its command");
        Pass("the Dock menu shows MacOSDesktopOptions.DockMenu, enabled from CanExecute, and runs host.MenuCommands.");
    }

    // The reopen event, as a Dock click sends it.
    private static async Task ExerciseReopenAsync(DesktopHost host)
    {
        var reopened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ReopenRequested += (_, e) => reopened.TrySetResult(e.HasVisibleWindows);
        var replied = await SendAsync(ReopenApplication, reason: null);
        Expect(replied == 0, $"sending the reopen event, which AppKit answers at once, reports {replied}");
        bool visible = await reopened.Task.WaitScaledAsync(StepTimeout);
        Pass($"the reopen event raises ReopenRequested (HasVisibleWindows={visible}).");
    }

    // Sends the Apple event to this process from the main thread without waiting for a reply, and returns AESend's
    // status: errAETimeout when the handler suspended the event.
    private static async Task<long> SendAsync(uint eventId, uint? reason)
    {
        long status = 0;
        await MacOsMainThread.InvokeAsync(() =>
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try
            {
                var descriptor = Class("NSAppleEventDescriptor");
                var appleEvent = Create(descriptor, Sel("appleEventWithEventClass:eventID:targetDescriptor:returnID:transactionID:"),
                    CoreEventClass, eventId, Send(descriptor, Sel("currentProcessDescriptor")), -1, 0);
                if (reason is { } why)
                    SetParam(appleEvent, Sel("setParamDescriptor:forKeyword:"), EnumDescriptor(descriptor, Sel("descriptorWithEnumCode:"), why), QuitReason);
                nint error = 0;
                // NSAppleEventSendNoReply.
                SendEvent(appleEvent, Sel("sendEventWithOptions:timeout:error:"), 1, 10, ref error);
                if (error != 0) status = (long)Send(error, Sel("code"));
            }
            finally { Send(pool, Sel("drain")); }
        }, CancellationToken.None);
        if (status is not (0 or AppleEventTimeout))
            throw new InvalidOperationException($"Lifecycle: sending the Apple event failed with {status}.");
        return status;
    }

    // The error number of the count-th quit reply Runic resumed.
    private static async Task<long> ReplyAsync(int count)
    {
        await WaitAsync(() => MacOsApplication.ResumedQuitReplies.Count >= count, $"the quit reply {count} resumed");
        return MacOsApplication.ResumedQuitReplies.ElementAt(count - 1);
    }

    private static nint Application() => Send(Class("NSApplication"), Sel("sharedApplication"));

    private static async Task WaitAsync(Func<bool> check, string description)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(StepTimeout);
        while (!check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {description}.");
            await Task.Delay(100);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Lifecycle: " + message);
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Create(nint target, nint selector, uint eventClass, uint eventId, nint targetDescriptor, short returnId, int transactionId);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint EnumDescriptor(nint target, nint selector, uint code);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial void SetParam(nint target, nint selector, nint descriptor, uint keyword);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint SendEvent(nint target, nint selector, nuint options, double timeout, ref nint error);
}
