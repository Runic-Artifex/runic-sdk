using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop;
using Runic.Desktop.Internal;

// Checks the window chrome of the macOS WKWebView window (DesktopWindowOptions.MacOS, M4) on a real AppKit event loop:
// a Hidden title bar keeps its traffic lights where TrafficLightPosition puts them; a mouse-down in a drag region of the
// page reaches performWindowDragWithEvent: and a double click there the system's title bar action, while a mouse-down
// in a no-drag region or outside every region reaches the page; and a frameless window that cannot be resized becomes
// key and takes typed keys. Run with --window-chrome on macOS.
//
// The mouse-downs are NSEvents sent to the window, which hit-tests them to the web view as the window server's would.
// performWindowDragWithEvent:, zoom: and miniaturize: of Runic's window class are replaced by recorders, so the run
// counts the calls without starting AppKit's drag loop or changing the window.
internal static partial class MacOsWindowChromeSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);
    private const ulong FullSizeContentView = 1UL << 15;
    private const nuint LeftMouseDown = 1, LeftMouseUp = 2;
    private const ushort KeyA = 0x00;
    private static int s_drags, s_zooms, s_miniaturizes;

    internal static void Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(async () =>
    {
        await using var host = await DesktopHost.StartAsync(hostOptions);
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 640,
            Height = 480,
            MacOS = new MacOSWindowOptions { TitleBar = MacOSTitleBarStyle.Hidden, TrafficLightPosition = new Point(20, 18) },
        }))
        {
            await window.InvokeNativeAsync(handle => InstallRecorders(Send(handle, Sel("class"))));
            await ExpectKeyAsync(window);
            await CheckTitleBarAsync(window);
            await CheckDragRegionsAsync(surface, window, "Hidden title bar");
            await CheckDoubleClickAsync(window);
            await window.CloseAsync();
        }

        await using var frameless = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 640,
            Height = 480,
            Frameless = true,
            Resizable = false,
        });
        await ExpectKeyAsync(frameless);
        Pass("A frameless window that cannot be resized becomes the key window.");
        await CheckTypingAsync(surface, frameless);
        await CheckDragRegionsAsync(surface, frameless, "frameless window");
        await frameless.CloseAsync();
        Console.WriteLine("WINDOW CHROME DONE");
    });

    // The window class is Runic's own; the recorders replace NSWindow's methods for its windows only.
    private static unsafe void InstallRecorders(nint windowClass)
    {
        var name = Marshal.PtrToStringUTF8(ClassName(windowClass));
        if (name != MacOsWkWebViewHost.WindowClassName) throw new InvalidOperationException($"The window is a {name}, not Runic's window class.");
        AddMethod(windowClass, Sel("performWindowDragWithEvent:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RecordDrag, "v@:@");
        AddMethod(windowClass, Sel("zoom:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RecordZoom, "v@:@");
        AddMethod(windowClass, Sel("miniaturize:"), (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&RecordMiniaturize, "v@:@");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void RecordDrag(nint self, nint selector, nint mouseEvent) => Interlocked.Increment(ref s_drags);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void RecordZoom(nint self, nint selector, nint sender) => Interlocked.Increment(ref s_zooms);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static void RecordMiniaturize(nint self, nint selector, nint sender) => Interlocked.Increment(ref s_miniaturizes);

    private static async Task ExpectKeyAsync(DesktopWindow window)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(StepTimeout);
        for (;;)
        {
            await window.FocusAsync();
            var (key, canBecomeKey) = (false, false);
            await window.InvokeNativeAsync(handle =>
            {
                key = Bool(handle, Sel("isKeyWindow")) != 0;
                canBecomeKey = Bool(handle, Sel("canBecomeKeyWindow")) != 0;
            });
            if (key) return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The window did not become the key window (canBecomeKeyWindow={canBecomeKey}).");
            await Task.Delay(200);
        }
    }

    private static async Task CheckTitleBarAsync(DesktopWindow window)
    {
        string? failure = null;
        string description = "";
        await window.InvokeNativeAsync(handle =>
        {
            var style = (ulong)Send(handle, Sel("styleMask"));
            var transparent = Bool(handle, Sel("titlebarAppearsTransparent")) != 0;
            var visibility = (long)Send(handle, Sel("titleVisibility"));
            var buttons = new List<string>();
            for (nint index = 0; index < 3; index++)
            {
                var button = Arg(handle, Sel("standardWindowButton:"), index);
                if (button == 0) { failure ??= $"standard window button {index} is missing"; continue; }
                if (Bool(button, Sel("isHidden")) != 0) failure ??= $"standard window button {index} is hidden";
                var frame = Rect(button, Sel("frame"));
                var titleBar = Send(button, Sel("superview"));
                var bar = titleBar == 0 ? default : Rect(titleBar, Sel("bounds"));
                // From the title bar's top-left: AppKit's views are not flipped.
                buttons.Add(FormattableString.Invariant($"({frame.X:0.#}, {bar.Height - frame.Y - frame.Height:0.#})"));
                if (index == 0 && (Math.Abs(frame.X - 20) > 0.5 || Math.Abs(bar.Height - frame.Y - frame.Height - 18) > 0.5))
                    failure ??= FormattableString.Invariant($"the close button is at ({frame.X}, {bar.Height - frame.Y - frame.Height}), not (20, 18)");
            }
            if ((style & FullSizeContentView) == 0) failure ??= "the content view does not fill the window";
            if (!transparent) failure ??= "the title bar is not transparent";
            if (visibility != 1) failure ??= "the title is visible";
            var content = Rect(Send(handle, Sel("contentView")), Sel("bounds"));
            var frameRect = Rect(handle, Sel("frame"));
            if (Math.Abs(content.Height - frameRect.Height) > 0.5) failure ??= "the content view is not as high as the window";
            description = $"style=0x{style:x} transparent={transparent} titleVisibility={visibility} buttons={string.Join(" ", buttons)}";
        });
        Console.WriteLine($"WINDOW CHROME TITLE BAR {description}");
        if (failure is not null) throw new InvalidOperationException($"Hidden title bar: {failure}; {description}.");
        Pass("A Hidden title bar is transparent over the page, hides the title and keeps the traffic lights at TrafficLightPosition.");
    }

    // The header drags; the button in it and the page below do not, and receive the mouse-down.
    private static async Task CheckDragRegionsAsync(DesktopSurface surface, DesktopWindow window, string kind)
    {
        await surface.ExecuteJavaScriptAsync("window.downs = {}; return true;", StepTimeout);
        // The page reports its regions after it lays out: retry until the first drag.
        var before = Volatile.Read(ref s_drags);
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(StepTimeout);
        while (Volatile.Read(ref s_drags) == before)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"A mouse-down in the drag region of the {kind} did not drag the window.");
            await ClickAsync(window, 100, 50, clickCount: 1);
            await Task.Delay(200);
        }
        Pass($"A mouse-down in a drag region of the {kind} calls performWindowDragWithEvent:.");
        var drags = Volatile.Read(ref s_drags);
        await ClickAsync(window, 450, 50, clickCount: 1);
        await ClickAsync(window, 300, 200, clickCount: 1);
        await WaitForPageAsync(surface, "return String(window.downs.button ?? 0) + '/' + String(window.downs.main ?? 0);", "1/1",
            "the page's mouse-downs on the no-drag button and the page");
        if (Volatile.Read(ref s_drags) != drags) throw new InvalidOperationException($"A mouse-down outside the drag region of the {kind} dragged the window.");
        var header = await surface.ExecuteJavaScriptAsync("return String(window.downs.header ?? 0);", StepTimeout);
        if (header != "0") throw new InvalidOperationException($"The page received {header} mouse-downs of the drag region.");
        Pass($"Mouse-downs on a no-drag button and outside every region of the {kind} reach the page and do not drag.");
    }

    // A double click in a drag region does what System Settings says for a title bar.
    private static async Task CheckDoubleClickAsync(DesktopWindow window)
    {
        var action = MacOsWindowChrome.ForDoubleClick(DefaultString("AppleActionOnDoubleClick"), DefaultBool("AppleMiniaturizeOnDoubleClick"));
        var (zooms, miniaturizes) = (Volatile.Read(ref s_zooms), Volatile.Read(ref s_miniaturizes));
        await ClickAsync(window, 100, 50, clickCount: 1);
        await ClickAsync(window, 100, 50, clickCount: 2);
        await Task.Delay(300);
        var seen = (Volatile.Read(ref s_zooms) - zooms, Volatile.Read(ref s_miniaturizes) - miniaturizes);
        var expected = action switch
        {
            MacOsWindowChrome.DoubleClickAction.Zoom => (1, 0),
            MacOsWindowChrome.DoubleClickAction.Minimize => (0, 1),
            _ => (0, 0),
        };
        Console.WriteLine($"WINDOW CHROME DOUBLE CLICK setting={action} zooms={seen.Item1} miniaturizes={seen.Item2}");
        if (seen != expected) throw new InvalidOperationException($"A double click in a drag region with the setting {action} gave {seen}, not {expected}.");
        Pass($"A double click in a drag region follows the title bar setting ({action}).");
    }

    private static async Task CheckTypingAsync(DesktopSurface surface, DesktopWindow window)
    {
        await surface.ExecuteJavaScriptAsync("const f = document.getElementById('field'); f.value = ''; f.focus(); return true;", StepTimeout);
        await window.InvokeNativeAsync(_ =>
        {
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            for (var press = 0; press < 2; press++)
            {
                var keyEvent = CreateKeyboardEvent(0, KeyA, down: press == 0);
                Args(application, Sel("postEvent:atStart:"), Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), keyEvent), 0);
                CFRelease(keyEvent);
            }
        });
        await WaitForPageAsync(surface, "return document.getElementById('field').value;", "a", "the typed key in the frameless window");
        Pass("A frameless window that cannot be resized takes a typed key.");
    }

    // A press and release at a point of the page, in points from the web view's top-left corner.
    private static ValueTask ClickAsync(DesktopWindow window, double x, double y, long clickCount) =>
        window.InvokeNativeAsync(handle => SendClick(handle, x, y, clickCount));

    private static unsafe void SendClick(nint handle, double x, double y, long clickCount)
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            var webView = Arg(Send(Send(handle, Sel("contentView")), Sel("subviews")), Sel("objectAtIndex:"), 0);
            var bounds = Rect(webView, Sel("bounds"));
            var flipped = Bool(webView, Sel("isFlipped")) != 0;
            var local = new CGPoint(x, flipped ? y : bounds.Height - y);
            var inWindow = ConvertPoint(webView, Sel("convertPoint:toView:"), local, 0);
            var number = Send(handle, Sel("windowNumber"));
            foreach (var type in (nuint[])[LeftMouseDown, LeftMouseUp])
            {
                var mouseEvent = ((delegate* unmanaged[Cdecl]<nint, nint, nuint, CGPoint, nuint, double, nint, nint, nint, nint, float, nint>)MessageSend)(
                    Class("NSEvent"), Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                    type, inWindow, 0, Uptime(), number, 0, 0, (nint)clickCount, type == LeftMouseDown ? 1f : 0f);
                Arg(handle, Sel("sendEvent:"), mouseEvent);
            }
        }
        finally { Send(pool, Sel("drain")); }
    }

    private static async Task WaitForPageAsync(DesktopSurface surface, string script, string expected, string description)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(StepTimeout);
        string value;
        do
        {
            value = await surface.ExecuteJavaScriptAsync(script, StepTimeout);
            if (value == expected) return;
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Expected {description} '{expected}'; the page has '{value}'.");
    }

    private static string? DefaultString(string key)
    {
        var name = NewString(key);
        try
        {
            var value = Arg(Send(Class("NSUserDefaults"), Sel("standardUserDefaults")), Sel("stringForKey:"), name);
            return value == 0 ? null : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String")));
        }
        finally { Send(name, Sel("release")); }
    }

    private static bool DefaultBool(string key)
    {
        var name = NewString(key);
        try { return BoolArg(Send(Class("NSUserDefaults"), Sel("standardUserDefaults")), Sel("boolForKey:"), name) != 0; }
        finally { Send(name, Sel("release")); }
    }

    private static nint NewString(string value)
    {
        var text = Marshal.StringToCoTaskMemUTF8(value);
        try { return Arg(Send(Class("NSString"), Sel("alloc")), Sel("initWithUTF8String:"), text); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    private static unsafe double Uptime() => ((delegate* unmanaged[Cdecl]<nint, nint, double>)MessageSend)(
        Send(Class("NSProcessInfo"), Sel("processInfo")), Sel("systemUptime"));

    private static unsafe CGRect Rect(nint target, nint selector) => ((delegate* unmanaged[Cdecl]<nint, nint, CGRect>)MessageSend)(target, selector);

    private static unsafe CGPoint ConvertPoint(nint target, nint selector, CGPoint point, nint view) =>
        ((delegate* unmanaged[Cdecl]<nint, nint, CGPoint, nint, CGPoint>)MessageSend)(target, selector, point, view);

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    [StructLayout(LayoutKind.Sequential)] private readonly record struct CGPoint(double X, double Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct CGRect(double X, double Y, double Width, double Height);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private static nint MessageSend { get; } = OperatingSystem.IsMacOS() ? NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend") : 0;
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "class_getName")] private static partial nint ClassName(nint type);
    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AddMethod(nint type, nint selector, nint implementation, string types);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Args(nint target, nint selector, nint first, nint second);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte BoolArg(nint target, nint selector, nint value);
    [LibraryImport(CoreGraphics, EntryPoint = "CGEventCreateKeyboardEvent")] private static partial nint CreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);

    // A header that drags, with a button that does not; mouse-downs are counted by the id or tag of their target.
    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic window chrome smoke</title>
        <style>
          body { margin: 0; }
          header { height: 80px; -webkit-app-region: drag; background: #ddd; }
          #button { position: absolute; left: 400px; top: 30px; width: 120px; height: 40px; -webkit-app-region: no-drag; }
          main { height: 300px; }
        </style>
        <script>
        window.downs = {};
        addEventListener("mousedown", event => {
          const name = event.target.id || event.target.localName;
          window.downs[name] = (window.downs[name] ?? 0) + 1;
        }, true);
        </script></head>
        <body><header><button id="button">Button</button></header><main><input id="field" aria-label="Field"></main></body></html>
        """;
}
