using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// B4: power and session events.
// - NSWorkspace's notification center: NSWorkspaceWillSleepNotification and NSWorkspaceDidWakeNotification.
// - The distributed com.apple.screenIsLocked and com.apple.screenIsUnlocked; CGSessionCopyCurrentDictionary's
//   CGSSessionScreenIsLocked for a read.
// - IOKit's providing power source (IOPSGetProvidingPowerSourceType), followed with notify(3)'s
//   com.apple.system.powersources.source on a private dispatch queue.
// - NSProcessInfo's lowPowerModeEnabled (macOS 12) and NSProcessInfoPowerStateDidChangeNotification.
// AppKit posts the workspace and distributed notifications on the main run loop, which Runic Desktop's event loop runs.
internal sealed partial class MacPowerEvents : IPowerEventSource
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private static readonly Lazy<nint> s_queue = new(static () => QueueCreate("org.runic.platform.power", 0));

    public ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<DesktopPowerState>>(new PlatformResult<DesktopPowerState>.Success(ReadState()));
    }

    public ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopPowerEvent>> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IAsyncDisposable>(Watch.Start(report));
    }

    internal static DesktopPowerState ReadState()
    {
        using var pool = new Pool();
        return new DesktopPowerState { IsOnBattery = OnBattery(), IsLowPower = LowPower(), IsLocked = ScreenLocked() };
    }

    // "Battery Power" or "AC Power" ("UPS Power" counts as mains until the UPS runs on battery, which IOKit then reports).
    internal static bool? OnBattery(string? source) => source switch
    {
        "Battery Power" => true,
        "AC Power" or "UPS Power" => false,
        _ => null,
    };

    private static bool? OnBattery()
    {
        var info = PowerSourcesInfo();
        if (info == 0) return null;
        try { return OnBattery(Text(ProvidingPowerSourceType(info))); }
        finally { CFRelease(info); }
    }

    private static bool? LowPower()
    {
        var process = Send(Class("NSProcessInfo"), Sel("processInfo"));
        return BoolArg(process, Sel("respondsToSelector:"), Sel("isLowPowerModeEnabled")) != 0 ? Bool(process, Sel("isLowPowerModeEnabled")) != 0 : null;
    }

    private static bool? ScreenLocked()
    {
        var session = SessionDictionary();
        if (session == 0) return null;
        try
        {
            var value = Arg(session, Sel("objectForKey:"), String("CGSSessionScreenIsLocked"));
            return value != 0 && Bool(value, Sel("boolValue")) != 0;
        }
        finally { CFRelease(session); }
    }

    private sealed class Watch : IAsyncDisposable
    {
        private readonly Lock _gate = new();
        private readonly PowerStateTracker _tracker;
        private readonly List<(nint Center, nint Observer)> _observers = [];
        private int _token = -1;
        private bool _disposed;

        private Watch(Action<PlatformResult<DesktopPowerEvent>> report) => _tracker = new PowerStateTracker(report);

        internal static Watch Start(Action<PlatformResult<DesktopPowerEvent>> report)
        {
            var watch = new Watch(report);
            using var pool = new Pool();
            var workspace = Send(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("notificationCenter"));
            var distributed = Send(Class("NSDistributedNotificationCenter"), Sel("defaultCenter"));
            var local = Send(Class("NSNotificationCenter"), Sel("defaultCenter"));
            lock (watch._gate)
            {
                watch._tracker.Start(ReadState());
                watch.Observe(workspace, "NSWorkspaceWillSleepNotification", tracker => tracker.Suspending());
                watch.Observe(workspace, "NSWorkspaceDidWakeNotification", tracker =>
                {
                    tracker.Resumed();
                    tracker.Update(ReadState() with { IsLocked = tracker.State.IsLocked });
                });
                watch.Observe(distributed, "com.apple.screenIsLocked", static tracker => tracker.Locked(true));
                watch.Observe(distributed, "com.apple.screenIsUnlocked", static tracker => tracker.Locked(false));
                watch.Observe(local, "NSProcessInfoPowerStateDidChangeNotification", Refresh);
                var handler = MacDesktopBlock.Create(_ => watch.Run(Refresh));
                try { _ = NotifyRegisterDispatch("com.apple.system.powersources.source", out watch._token, s_queue.Value, handler); }
                finally { MacDesktopBlock.Release(handler); }
            }
            return watch;
        }

        private static void Refresh(PowerStateTracker tracker) => tracker.Update(ReadState() with { IsLocked = tracker.State.IsLocked });

        // Caller holds the gate. The center copies the block; queue nil runs it on the posting thread.
        private void Observe(nint center, string name, Action<PowerStateTracker> change)
        {
            var block = MacDesktopBlock.Create(_ => Run(change));
            try
            {
                var observer = AddObserver(center, Sel("addObserverForName:object:queue:usingBlock:"), String(name), 0, 0, block);
                if (observer == 0) return;
                Send(observer, Sel("retain"));
                _observers.Add((center, observer));
            }
            finally { MacDesktopBlock.Release(block); }
        }

        private void Run(Action<PowerStateTracker> change)
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    using var pool = new Pool();
                    change(_tracker);
                }
            }
            catch
            {
                // Never unwind through AppKit or libdispatch.
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                foreach (var (center, observer) in _observers)
                {
                    Arg(center, Sel("removeObserver:"), observer);
                    Release(observer);
                }
                _observers.Clear();
                if (_token >= 0) _ = NotifyCancel(_token);
            }
            return ValueTask.CompletedTask;
        }
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint AddObserver(nint target, nint selector, nint name, nint @object, nint queue, nint block);

    [LibraryImport(IOKit, EntryPoint = "IOPSCopyPowerSourcesInfo")] private static partial nint PowerSourcesInfo();
    [LibraryImport(IOKit, EntryPoint = "IOPSGetProvidingPowerSourceType")] private static partial nint ProvidingPowerSourceType(nint info);
    [LibraryImport(CoreGraphics, EntryPoint = "CGSessionCopyCurrentDictionary")] private static partial nint SessionDictionary();
    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);
    [LibraryImport(LibSystem, EntryPoint = "notify_register_dispatch", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint NotifyRegisterDispatch(string name, out int token, nint queue, nint handler);
    [LibraryImport(LibSystem, EntryPoint = "notify_cancel")] private static partial uint NotifyCancel(int token);
    [LibraryImport(LibSystem, EntryPoint = "dispatch_queue_create", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint QueueCreate(string label, nint attributes);
}
