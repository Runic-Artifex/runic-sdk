using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

// The window chrome of the macOS window (DesktopWindowOptions.MacOS): the hidden or transparent title bar, the traffic
// lights' position, the page's drag regions and a window that can become key without a title bar. The decisions
// without AppKit are in MacOsWindowChrome.cs.
internal sealed partial class MacOsWkWebViewHost
{
    private static readonly nint WindowClass = CreateWindowClass();
    private static readonly Lazy<nint> WindowChromeHandler = new(CreateWindowChromeHandler);

    // Set while the window is created, before the web view; read on the main thread.
    private bool _usesDragRegions;
    private System.Drawing.Point? _trafficLights;
    // The page's last report (MacOsWindowChrome.js); written and read on the main thread.
    private MacOsWindowChrome.DragRegions _dragRegions = MacOsWindowChrome.DragRegions.Empty;

    // Runic.Desktop.WebViewSmoke checks that its windows are of this class.
    internal const string WindowClassName = "RunicDesktopWindow";

    // An NSWindow that can become the key and main window without a title bar or resize bar. AppKit's NSWindow refuses
    // both for a borderless window, which then never takes keyboard input: a Frameless or Kiosk window, and a frameless
    // window that cannot be resized either. A titled window answers YES as before.
    private static unsafe nint CreateWindowClass()
    {
        if (!OperatingSystem.IsMacOS() || !Api.IsAvailable)
        {
            return 0;
        }
        var existing = Api.GetClass(WindowClassName);
        if (existing != 0)
        {
            return existing;
        }
        var type = Api.AllocateClassPair(Api.GetClass("NSWindow"), WindowClassName);
        if (type == 0)
        {
            return 0;
        }
        var boolType = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:" : "c@:";
        Api.AddMethod(type, "canBecomeKeyWindow", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&CanBecomeKeyOrMain, boolType);
        Api.AddMethod(type, "canBecomeMainWindow", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&CanBecomeKeyOrMain, boolType);
        Api.RegisterClassPair(type);
        return type;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte CanBecomeKeyOrMain(nint self, nint selector) => 1;

    // Main thread, while the window is created: a Hidden or Transparent title bar lies transparent over the page, which
    // fills the window (NSWindowStyleMaskFullSizeContentView, set by MacOsWindowChrome.StyleMask), and keeps the
    // traffic lights.
    private void ApplyTitleBar(WebUiEmbeddedHostOptions options)
    {
        _usesDragRegions = MacOsWindowChrome.UsesDragRegions(options);
        if (!MacOsWindowChrome.HasCustomTitleBar(options))
        {
            return;
        }
        Api.SendVoidBool(_window, "setTitlebarAppearsTransparent:", true);
        Api.SendBoolLong(_window, "setTitleVisibility:", MacOsWindowChrome.TitleVisibility(options.MacOS.TitleBar));
        _trafficLights = options.MacOS.TrafficLightPosition;
    }

    // Main thread: moves the close, minimize and zoom buttons to MacOSWindowOptions.TrafficLightPosition, as Electron
    // does. AppKit lays the title bar out again after a resize, a new title and full screen, so the window calls this
    // after each. The buttons' container is a private view of AppKit's frame view; when it is not there, the buttons keep
    // AppKit's place.
    private void ApplyTrafficLights()
    {
        if (_trafficLights is not { } position || _window == 0 || _fullScreenTransition
            || (Api.SendUlong(_window, "styleMask") & StyleFullScreen) != 0)
        {
            return;
        }
        var close = Api.SendNintNint(_window, "standardWindowButton:", 0);
        var minimize = Api.SendNintNint(_window, "standardWindowButton:", 1);
        var zoom = Api.SendNintNint(_window, "standardWindowButton:", 2);
        if (close == 0 || minimize == 0 || zoom == 0)
        {
            return;
        }
        var titleBar = Api.SendNint(close, "superview");
        var container = titleBar == 0 ? 0 : Api.SendNint(titleBar, "superview");
        if (container == 0)
        {
            return;
        }
        var closeFrame = Api.SendRect(close, "frame");
        var layout = MacOsWindowChrome.LayoutTrafficLights(position, Api.SendRect(_window, "frame").Height, closeFrame.Height,
            closeFrame.X, Api.SendRect(minimize, "frame").X);
        var containerFrame = Api.SendRect(container, "frame");
        Api.SendVoidRect(container, "setFrame:", containerFrame with { Y = layout.ContainerY, Height = layout.ContainerHeight });
        var index = 0;
        foreach (var button in (nint[])[close, minimize, zoom])
        {
            Api.SendVoidPoint(button, "setFrameOrigin:", new CGPoint(layout.FirstButtonX + index++ * layout.Step, layout.ButtonY));
        }
    }

    // Main thread: whether the mouse-down is in a drag region of the page's last report.
    private bool IsInDragRegion(nint webView, nint mouseEvent)
    {
        var regions = _dragRegions;
        if (!_usesDragRegions || regions.Regions.IsEmpty || mouseEvent == 0)
        {
            return false;
        }
        // A pinch magnifies and pans the page under the reported viewport.
        if (Math.Abs(Api.SendDouble(webView, "magnification") - 1) > 0.001)
        {
            return false;
        }
        var location = Api.SendPointPointNint(webView, "convertPoint:fromView:", Api.SendPoint(mouseEvent, "locationInWindow"), 0);
        var bounds = Api.SendRect(webView, "bounds");
        var top = Api.SendBool(webView, "isFlipped") ? location.Y : bounds.Height - location.Y;
        var (x, y) = MacOsWindowChrome.ToViewport(regions, bounds.Width, location.X, top);
        return MacOsWindowChrome.IsDraggable(regions, x, y);
    }

    // A mouse-down in a drag region moves the window from this event, which is current now; a double click zooms or
    // minimizes as the user's setting for title bars says. Anywhere else the page receives it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WebViewMouseDown(nint self, nint selector, nint mouseEvent)
    {
        try
        {
            if (WebViews.TryGetValue(self, out var host) && host.IsOpen && host.IsInDragRegion(self, mouseEvent))
            {
                if (Api.SendNint(mouseEvent, "clickCount") == 2)
                {
                    host.PerformDoubleClick();
                }
                else
                {
                    Api.SendVoidNint(host._window, "performWindowDragWithEvent:", mouseEvent);
                }
                return;
            }
        }
        catch
        {
            // Never unwind through AppKit; the page receives the event.
        }
        try
        {
            Api.SendSuperVoidNint(self, Api.GetClass("WKWebView"), "mouseDown:", mouseEvent);
        }
        catch
        {
            // Never unwind through AppKit.
        }
    }

    // The first click into an inactive window drags it from a drag region, as from a title bar.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte WebViewAcceptsFirstMouse(nint self, nint selector, nint mouseEvent)
    {
        try
        {
            if (WebViews.TryGetValue(self, out var host) && host.IsOpen && host.IsInDragRegion(self, mouseEvent))
            {
                return 1;
            }
            return Api.SendSuperBoolNint(self, Api.GetClass("WKWebView"), "acceptsFirstMouse:", mouseEvent) ? (byte)1 : (byte)0;
        }
        catch
        {
            return 0;
        }
    }

    // zoom: and miniaturize: rather than their perform… forms, which beep in a window without those buttons.
    private void PerformDoubleClick()
    {
        var action = MacOsWindowChrome.ForDoubleClick(
            Api.UserDefaultString("AppleActionOnDoubleClick"), Api.UserDefaultBool("AppleMiniaturizeOnDoubleClick"));
        switch (action)
        {
            case MacOsWindowChrome.DoubleClickAction.Zoom:
                Api.SendVoidNint(_window, "zoom:", 0);
                break;
            case MacOsWindowChrome.DoubleClickAction.Minimize:
                Api.SendVoidNint(_window, "miniaturize:", 0);
                break;
        }
    }

    // Receives the page's drag regions from the window chrome script's content world.
    private static unsafe nint CreateWindowChromeHandler()
    {
        var type = Api.GetClass("RunicDesktopWindowChromeHandler");
        if (type == 0)
        {
            type = Api.AllocateClassPair(Api.GetClass("NSObject"), "RunicDesktopWindowChromeHandler");
            if (type == 0)
            {
                return 0;
            }
            Api.AddMethod(type, "userContentController:didReceiveScriptMessage:",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&DidReceiveWindowChromeMessage, "v@:@@");
            Api.RegisterClassPair(type);
        }
        return Api.SendNint(Api.SendNint(type, "alloc"), "init");
    }

    // The script runs in the main frame only; a report that is not one is ignored and the last one stays.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DidReceiveWindowChromeMessage(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            if (WebViews.TryGetValue(Api.SendNint(message, "webView"), out var host) && host.IsOpen)
            {
                var frame = Api.SendNint(message, "frameInfo");
                if (frame != 0 && !Api.SendBool(frame, "isMainFrame")) return;
                var body = Api.SendNint(message, "body");
                if (MacOsWindowChrome.Parse(Api.IsString(body) ? Api.Text(body) : null) is { } regions)
                {
                    Volatile.Write(ref host._dragRegions, regions);
                }
            }
        }
        catch
        {
            // Never unwind through WebKit.
        }
    }

    private sealed unsafe partial class ObjC
    {
        internal void SendVoidRect(nint receiver, string selector, CGRect value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, void>)_messageSend)(receiver, Selector(selector), value);

        internal CGPoint SendPointPointNint(nint receiver, string selector, CGPoint point, nint value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGPoint, nint, CGPoint>)_messageSend)(receiver, Selector(selector), point, value);

        internal void SendSuperVoidNint(nint receiver, nint superclass, string selector, nint value)
        {
            var super = new ObjCSuper(receiver, superclass);
            ((delegate* unmanaged[Cdecl]<ObjCSuper*, nint, nint, void>)_messageSendSuper)(&super, Selector(selector), value);
        }

        // [NSUserDefaults standardUserDefaults], which also reads the global domain of System Settings.
        internal string? UserDefaultString(string key)
        {
            var name = CreateString(key);
            try
            {
                var value = SendNintNint(SendNint(GetClass("NSUserDefaults"), "standardUserDefaults"), "stringForKey:", name);
                return value == 0 ? null : Text(value);
            }
            finally
            {
                SendVoid(name, "release");
            }
        }

        internal bool UserDefaultBool(string key)
        {
            var name = CreateString(key);
            try
            {
                return SendBool(SendNint(GetClass("NSUserDefaults"), "standardUserDefaults"), "boolForKey:", name);
            }
            finally
            {
                SendVoid(name, "release");
            }
        }
    }
}
