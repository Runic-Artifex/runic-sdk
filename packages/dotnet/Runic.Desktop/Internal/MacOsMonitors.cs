using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// Monitors, full screen and placement on a monitor for the macOS WKWebView window. NSScreen.screens on the main thread:
// frame and visibleFrame in points of AppKit screen coordinates, the window's units, backingScaleFactor and
// localizedName (macOS 10.15); the identifier is the display's UUID (CGDisplayCreateUUIDFromDisplayID), which survives
// restarts and reconnection. screens[0] holds the menu bar: the primary screen. Changes come from
// NSApplicationDidChangeScreenParametersNotification.
internal sealed partial class MacOsWkWebViewHost
{
    internal static IDesktopMonitors Monitors { get; } = new MonitorSource();

    private sealed class MonitorSource : DesktopMonitorSource
    {
        protected override async ValueTask<PlatformResult<ImmutableArray<DesktopMonitor>>> ReadAsync(CancellationToken cancellationToken)
        {
            if (!IsSupported)
                return new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            // AppKit is main-thread only; without the event loop nothing runs work there.
            if (!IsMainThread && !DesktopEventLoop.IsRunning)
                return new PlatformResult<ImmutableArray<DesktopMonitor>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
            ImmutableArray<DesktopMonitor> monitors = [];
            await InvokeOnMainAsync(() => monitors = [.. ReadScreens().Select(static screen => screen.Monitor)], cancellationToken)
                .ConfigureAwait(false);
            return new PlatformResult<ImmutableArray<DesktopMonitor>>.Success(monitors);
        }

        protected override IDisposable Subscribe(Action changed)
        {
            ScreenWatchers[changed] = 0;
            EnsureScreenObserver();
            return new Unsubscriber(changed);
        }

        private sealed class Unsubscriber(Action changed) : IDisposable
        {
            public void Dispose() => ScreenWatchers.TryRemove(changed, out _);
        }
    }

    private static readonly ConcurrentDictionary<Action, byte> ScreenWatchers = new();
    private static int s_screenObserver;
    // Display UUIDs by CGDirectDisplayID; a display keeps its ID while connected.
    private static readonly ConcurrentDictionary<uint, string> DisplayIds = new();

    // A screen with its NSScreen, for the window operations. Main thread only.
    private readonly record struct Screen(nint Handle, DesktopMonitor Monitor, CGRect Frame, CGRect VisibleFrame);

    private static List<Screen> ReadScreens()
    {
        List<Screen> screens = [];
        var all = Api.SendNint(Api.GetClass("NSScreen"), "screens");
        if (all == 0) return screens;
        var count = (long)Api.SendUlong(all, "count");
        for (long index = 0; index < count; index++)
        {
            var screen = Api.SendNintNint(all, "objectAtIndex:", (nint)index);
            if (screen == 0) continue;
            var frame = Api.SendRect(screen, "frame");
            var visible = Api.SendRect(screen, "visibleFrame");
            var id = ScreenId(screen);
            var name = Api.RespondsToSelector(screen, "localizedName") ? Api.Text(Api.SendNint(screen, "localizedName")) : "";
            screens.Add(new Screen(screen, new DesktopMonitor(id, name.Length > 0 ? name : id)
            {
                Bounds = ToRect(frame),
                WorkArea = ToRect(visible),
                Scale = Api.SendDouble(screen, "backingScaleFactor") is > 0 and var scale ? scale : 1,
                IsPrimary = index == 0,
            }, frame, visible));
        }
        return screens;
    }

    private static DesktopRect ToRect(CGRect rect) => new((int)Math.Round(rect.X), (int)Math.Round(rect.Y),
        (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    // Main thread only.
    private static string ScreenId(nint screen)
    {
        var display = Api.ScreenNumber(screen);
        if (DisplayIds.TryGetValue(display, out var known)) return known;
        var id = DisplayUuid(display) ?? $"display-{display}";
        DisplayIds[display] = id;
        return id;
    }

    private static string? DisplayUuid(uint display)
    {
        if (!DisplayFunctions.Value.Available) return null;
        var uuid = DisplayFunctions.Value.CreateUuid(display);
        if (uuid == 0) return null;
        try
        {
            var text = DisplayFunctions.Value.CreateString(0, uuid);
            if (text == 0) return null;
            try { return Api.Text(text); }
            finally { DisplayFunctions.Value.Release(text); }
        }
        finally
        {
            DisplayFunctions.Value.Release(uuid);
        }
    }

    // CGDisplayCreateUUIDFromDisplayID lives in ColorSync on current macOS and in CoreGraphics before it.
    private static readonly Lazy<DisplayUuidFunctions> DisplayFunctions = new(DisplayUuidFunctions.Load);

    private sealed unsafe class DisplayUuidFunctions
    {
        private nint _createUuid;
        private nint _createString;
        private nint _release;

        internal bool Available => _createUuid != 0 && _createString != 0 && _release != 0;

        internal nint CreateUuid(uint display) => ((delegate* unmanaged[Cdecl]<uint, nint>)_createUuid)(display);

        internal nint CreateString(nint allocator, nint uuid) => ((delegate* unmanaged[Cdecl]<nint, nint, nint>)_createString)(allocator, uuid);

        internal void Release(nint value) => ((delegate* unmanaged[Cdecl]<nint, void>)_release)(value);

        internal static DisplayUuidFunctions Load()
        {
            var functions = new DisplayUuidFunctions();
            if (!OperatingSystem.IsMacOS()) return functions;
            foreach (var path in (string[])["/System/Library/Frameworks/ColorSync.framework/ColorSync",
                "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics",
                "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices"])
            {
                if (NativeLibrary.TryLoad(path, out var library) && NativeLibrary.TryGetExport(library, "CGDisplayCreateUUIDFromDisplayID", out var export))
                {
                    functions._createUuid = export;
                    break;
                }
            }
            if (NativeLibrary.TryLoad("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", out var coreFoundation))
            {
                NativeLibrary.TryGetExport(coreFoundation, "CFUUIDCreateString", out functions._createString);
                NativeLibrary.TryGetExport(coreFoundation, "CFRelease", out functions._release);
            }
            return functions;
        }
    }

    private static void EnsureScreenObserver()
    {
        if (!IsSupported || Interlocked.Exchange(ref s_screenObserver, 1) != 0) return;
        var type = CreateScreenObserverClass();
        if (type == 0) return;
        var observer = Api.SendNint(Api.SendNint(type, "alloc"), "init");
        // Never released: it observes for the rest of the process.
        Api.AddNotificationObserver(observer, "runicScreensChanged:", "NSApplicationDidChangeScreenParametersNotification");
    }

    private static unsafe nint CreateScreenObserverClass()
    {
        const string name = "RunicDesktopScreenObserver";
        var existing = Api.GetClass(name);
        if (existing != 0) return existing;
        var type = Api.AllocateClassPair(Api.GetClass("NSObject"), name);
        if (type == 0) return 0;
        Api.AddMethod(type, "runicScreensChanged:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&ScreensChanged, "v@:@");
        Api.RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ScreensChanged(nint self, nint selector, nint notification)
    {
        try
        {
            DisplayIds.Clear();
            foreach (var watcher in ScreenWatchers.Keys)
            {
                try { watcher(); }
                catch { } // Never unwind through AppKit.
            }
            foreach (var host in Hosts.Values)
            {
                if (host.IsOpen) host.UpdateState();
            }
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // The window's screen, by identifier. Main thread only.
    private string? CurrentMonitorId()
    {
        var screen = Api.SendNint(_window, "screen");
        return screen == 0 ? null : ScreenId(screen);
    }

    private static Screen? FindScreen(string id)
    {
        foreach (var screen in ReadScreens())
        {
            if (screen.Monitor.Id == id) return screen;
        }
        return null;
    }

    private bool _kiosk;
    // Between windowWill…FullScreen: and windowDid…FullScreen: (main thread only).
    private bool _fullScreenTransition;
    // A monitor chosen while the window was full-screen: it moves there when it leaves full screen.
    private string? _pendingMonitor;

    public ValueTask SetFullScreenAsync(bool fullScreen, CancellationToken cancellationToken = default)
        => InvokeAsync(() =>
        {
            if (_kiosk || IsFullScreen == fullScreen) return;
            if (fullScreen)
            {
                RequestFullScreen();
                return;
            }
            _fullScreenRetries = 0;
            Api.SendVoidNint(_window, "toggleFullScreen:", 0);
        }, cancellationToken);

    // The retries left for a requested full screen that AppKit refused, as while another window's space animates.
    private int _fullScreenRetries;

    // Main thread only. NSWindowCollectionBehaviorFullScreenPrimary gives the window its own full-screen space.
    private void RequestFullScreen()
    {
        _fullScreenRetries = 3;
        Api.SendVoidUlong(_window, "setCollectionBehavior:", Api.SendUlong(_window, "collectionBehavior") | (1UL << 7));
        Api.SendVoidNint(_window, "toggleFullScreen:", 0);
    }

    // Main thread only, from windowDidFailToEnterFullScreen:: asks again a little later while retries remain.
    private void RetryFullScreen()
    {
        if (_fullScreenRetries <= 0 || IsFullScreen) return;
        _fullScreenRetries--;
        _ = Task.Delay(TimeSpan.FromMilliseconds(750)).ContinueWith(_ =>
        {
            if (!IsOpen) return Task.CompletedTask;
            return InvokeAsync(() =>
            {
                if (IsOpen && !IsFullScreen && !_fullScreenTransition) Api.SendVoidNint(_window, "toggleFullScreen:", 0);
            }, CancellationToken.None).AsTask();
        }, TaskScheduler.Default).Unwrap().ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    public ValueTask MoveToMonitorAsync(string monitorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(monitorId);
        return InvokeAsync(() =>
        {
            if (FindScreen(monitorId) is not { } screen)
                throw new ArgumentException($"No connected monitor has the identifier '{monitorId}'.", nameof(monitorId));
            if (IsFullScreen)
            {
                _pendingMonitor = monitorId;
                return;
            }
            MoveToScreen(screen);
        }, cancellationToken);
    }

    private bool IsFullScreen => (Api.SendUlong(_window, "styleMask") & StyleFullScreen) != 0;

    // Main thread only. Centers the frame on the screen's visible frame, shrunk to fit; a zoomed window zooms again there.
    private void MoveToScreen(Screen screen)
    {
        var zoomed = Api.SendBool(_window, "isZoomed");
        if (zoomed)
        {
            Api.SendVoidNint(_window, "zoom:", 0);
        }
        var frame = Api.SendRect(_window, "frame");
        var area = screen.VisibleFrame;
        var width = Math.Min(frame.Width, area.Width);
        var height = Math.Min(frame.Height, area.Height);
        Api.SendVoidRectBool(_window, "setFrame:display:",
            new CGRect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height), true);
        if (zoomed)
        {
            Api.SendVoidNint(_window, "zoom:", 0);
        }
    }

    // Main thread only: after the window left full screen, a monitor chosen meanwhile.
    private void ApplyPendingMonitor()
    {
        if (_pendingMonitor is not { } id || IsFullScreen) return;
        _pendingMonitor = null;
        if (FindScreen(id) is { } screen) MoveToScreen(screen);
    }
}
