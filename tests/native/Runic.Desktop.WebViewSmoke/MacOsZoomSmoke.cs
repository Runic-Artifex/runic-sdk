using System.Globalization;
using System.Runtime.InteropServices;
using Runic.Desktop;

// Checks the page zoom keys of the macOS WKWebView window on a real AppKit event loop. The keys are real keyboard
// events, made with CGEventCreateKeyboardEvent and posted to the application's event queue, so they take the path of
// a key press: NSApp sends a Command key to the key window, WKWebView gives it to the page, and the View menu matches
// it when the page leaves it alone. Run with --zoom on macOS.
internal static partial class MacOsZoomSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(15);
    private const ulong Command = 0x100000, Shift = 0x20000, NumericPad = 0x200000;
    // Virtual key codes of the ANSI layout: =, -, 0 and the keypad's +.
    private const ushort KeyEqual = 0x18, KeyMinus = 0x1b, KeyZero = 0x1d, KeypadPlus = 0x45, KeyA = 0x00, KeyC = 0x08, KeyV = 0x09, KeyQ = 0x0c;
    // Command+Q asks the window to close: the guard refuses the first time and allows the second.
    private static int s_closeRequests;

    private static ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Interlocked.Increment(ref s_closeRequests) > 1);

    internal static void Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(async () =>
    {
        await using var host = await DesktopHost.StartAsync(hostOptions);
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 640,
            Height = 480,
            ConfirmCloseAsync = ConfirmCloseAsync,
        });
        try
        {
            await ExerciseAsync(surface, window);
            await ExerciseEditAndQuitAsync(surface, window);
        }
        finally
        {
            if (window.IsOpen) await window.CloseAsync();
        }
    });

    private static async Task ExerciseAsync(DesktopSurface surface, DesktopWindow window)
    {
        await window.FocusAsync();
        var deadline = DateTime.UtcNow + StepTimeout;
        var state = await ReadNativeAsync(window);
        while (!state.Key && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            await window.FocusAsync();
            state = await ReadNativeAsync(window);
        }
        Console.WriteLine($"ZOOM READY {state}");
        if (!state.Key) throw new InvalidOperationException($"The window did not become the key window: {state}.");
        if (!state.WebViewFocused) throw new InvalidOperationException($"The web view is not the first responder: {state}.");
        if (!state.Magnifies) throw new InvalidOperationException("The web view does not allow pinch magnification.");
        await PrintMenuAsync(window);

        await ExpectZoomAsync(window, "Command+=", KeyEqual, Command, 1.1);
        await ExpectZoomAsync(window, "Command+Shift+= (+)", KeyEqual, Command | Shift, 1.2);
        await ExpectZoomAsync(window, "Command+-", KeyMinus, Command, 1.1);
        await ExpectZoomAsync(window, "Command+0", KeyZero, Command, 1.0);
        await ExpectZoomAsync(window, "Command+-", KeyMinus, Command, 0.9);
        await ExpectZoomAsync(window, "Command+keypad +", KeypadPlus, Command | NumericPad, 1.0);
        var seen = await surface.ExecuteJavaScriptAsync("return String(window.commandKeys);", StepTimeout);
        if (int.Parse(seen, CultureInfo.InvariantCulture) < 6)
            throw new InvalidOperationException($"The page saw {seen} of the 6 Command keys before the menu zoomed.");
        Pass($"Command with +, =, -, 0 and the keypad's + zooms in steps and resets; the page saw each key first ({seen}).");

        await ExpectZoomAsync(window, "Control+= (no zoom)", KeyEqual, 0x40000, 1.0, settle: true);
        Pass("Control and = does not zoom on macOS.");

        await surface.ExecuteJavaScriptAsync("window.claimZoom = true; return true;", StepTimeout);
        await ExpectZoomAsync(window, "Command+= that the page cancels", KeyEqual, Command, 1.0, settle: true);
        var claimed = await surface.ExecuteJavaScriptAsync("return String(window.claimed);", StepTimeout);
        if (claimed != "1") throw new InvalidOperationException($"The page did not receive the Command and = it handles ({claimed}).");
        await surface.ExecuteJavaScriptAsync("window.claimZoom = false; return true;", StepTimeout);
        Pass("Command and = that the page cancels does not zoom.");

        await surface.ExecuteJavaScriptAsync("document.querySelector('input').focus(); return true;", StepTimeout);
        await ExpectZoomAsync(window, "Command+= in a text field", KeyEqual, Command, 1.1);
        await ExpectZoomAsync(window, "Command+0 in a text field", KeyZero, Command, 1.0);
        var focused = await surface.ExecuteJavaScriptAsync("return String(document.activeElement?.tagName);", StepTimeout);
        if (focused != "INPUT") throw new InvalidOperationException($"Zooming moved the focus to {focused}.");
        Pass("Command and = zooms while a text field has focus, which keeps it.");

        for (var step = 0; step < 16; step++) await SendAsync(window, KeyEqual, Command);
        await ExpectZoomAsync(window, "Command+=", KeyEqual, Command, 3.0);
        Pass("Zoom stops at 300 %.");
        await ExpectZoomAsync(window, "Command+0", KeyZero, Command, 1.0);
        Console.WriteLine("ZOOM DONE");
    }

    // The Edit menu copies and pastes through the focused WKWebView, and Quit takes the window's close path.
    private static async Task ExerciseEditAndQuitAsync(DesktopSurface surface, DesktopWindow window)
    {
        await surface.ExecuteJavaScriptAsync(
            "const s = document.getElementById('source'); s.value = 'Runic clipboard'; s.focus(); s.setSelectionRange(0, 0); return true;", StepTimeout);
        await SendAsync(window, KeyA, Command);
        await WaitForPageAsync(surface, "const s = document.getElementById('source'); return `${s.selectionStart}-${s.selectionEnd}`;", "0-15",
            "the selection after Command and A");
        await SendAsync(window, KeyC, Command);
        // The copy reaches the pasteboard from the web process; give it time before the paste reads it.
        await Task.Delay(TimeSpan.FromSeconds(1));
        await surface.ExecuteJavaScriptAsync("document.getElementById('target').focus(); return true;", StepTimeout);
        await SendAsync(window, KeyV, Command);
        await WaitForPageAsync(surface, "return document.getElementById('target').value;", "Runic clipboard", "the pasted text");
        Pass("Command and A, C and V select, copy and paste text between fields through the Edit menu.");

        await SendAsync(window, KeyQ, Command);
        await WaitAsync(() => Volatile.Read(ref s_closeRequests) == 1, "the close guard asked by Command and Q");
        await Task.Delay(TimeSpan.FromSeconds(1));
        if (!window.IsOpen) throw new InvalidOperationException("Command and Q closed the window although its guard refused.");
        Pass("Command and Q asks the window's close guard, which can keep it open.");
        await SendAsync(window, KeyQ, Command);
        await window.WaitForCloseAsync().AsTask().WaitAsync(StepTimeout);
        Pass("Command and Q closes the window once its guard allows it.");
    }

    private static async Task WaitForPageAsync(DesktopSurface surface, string script, string expected, string description)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
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

    private static async Task WaitAsync(Func<bool> check, string description)
    {
        var deadline = DateTime.UtcNow + StepTimeout;
        while (!check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {description}.");
            await Task.Delay(100);
        }
    }

    private static async Task ExpectZoomAsync(DesktopWindow window, string keys, ushort key, ulong flags, double expected, bool settle = false)
    {
        await SendAsync(window, key, flags);
        if (settle) await Task.Delay(TimeSpan.FromSeconds(1.5));
        var deadline = DateTime.UtcNow + StepTimeout;
        NativeState state;
        do
        {
            state = await ReadNativeAsync(window);
            if (Math.Abs(state.Zoom - expected) < 0.005)
            {
                Console.WriteLine($"ZOOM OK {expected:0.##} after {keys}: {state}");
                return;
            }
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Expected zoom {expected:0.##} after {keys}; {state}.");
    }

    // Posts the key press and release to the application's queue, which the Desktop event loop sends, as the
    // window server delivers a key press.
    private static ValueTask SendAsync(DesktopWindow window, ushort key, ulong flags) => window.DispatchNativeAsync(_ =>
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try
        {
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            for (var press = 0; press < 2; press++)
            {
                var keyEvent = CreateKeyboardEvent(0, key, down: press == 0);
                SetFlags(keyEvent, flags);
                Args(application, Sel("postEvent:atStart:"), Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), keyEvent), 0);
                CFRelease(keyEvent);
            }
        }
        finally { Send(pool, Sel("drain")); }
    });

    // Prints the main menu, for a failure to explain.
    private static ValueTask PrintMenuAsync(DesktopWindow window) => window.DispatchNativeAsync(_ =>
    {
        var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
        try { Console.WriteLine($"ZOOM MENU {DescribeMenu(Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("mainMenu")), 0)}"); }
        finally { Send(pool, Sel("drain")); }
    });

    private static string DescribeMenu(nint menu, int depth)
    {
        if (menu == 0) return "(none)";
        var parts = new List<string>();
        for (long i = 0, count = Send(menu, Sel("numberOfItems")); i < count; i++)
        {
            var item = Arg(menu, Sel("itemAtIndex:"), (nint)i);
            var action = Send(item, Sel("action"));
            var text = $"'{Text(Send(item, Sel("title")))}' key='{Text(Send(item, Sel("keyEquivalent")))}' mask=0x{Send(item, Sel("keyEquivalentModifierMask")):x}" +
                $" action={(action == 0 ? "-" : Marshal.PtrToStringUTF8(SelName(action)))} enabled={Bool(item, Sel("isEnabled"))} hidden={Bool(item, Sel("isHidden"))}";
            var submenu = Send(item, Sel("submenu"));
            if (submenu != 0 && depth < 2) text += " [" + DescribeMenu(submenu, depth + 1) + "]";
            parts.Add(text);
        }
        return string.Join("; ", parts);
    }

    private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

    private readonly record struct NativeState(double Zoom, bool Key, bool Active, bool WebViewFocused, bool Magnifies)
    {
        public override string ToString() =>
            $"zoom={Zoom:0.###} key={Key} active={Active} webViewFocused={WebViewFocused} magnifies={Magnifies}";
    }

    private static async Task<NativeState> ReadNativeAsync(DesktopWindow window)
    {
        NativeState state = default;
        await window.DispatchNativeAsync(handle =>
        {
            var webView = Arg(Send(Send(handle, Sel("contentView")), Sel("subviews")), Sel("objectAtIndex:"), 0);
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            state = new NativeState(Double(webView, Sel("pageZoom")), Bool(handle, Sel("isKeyWindow")) != 0, Bool(application, Sel("isActive")) != 0,
                Send(handle, Sel("firstResponder")) == webView, Bool(webView, Sel("allowsMagnification")) != 0);
        });
        return state;
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Args(nint target, nint selector, nint first, nint second);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial double Double(nint target, nint selector);
    [LibraryImport(ObjC, EntryPoint = "sel_getName")] private static partial nint SelName(nint selector);
    [LibraryImport(CoreGraphics, EntryPoint = "CGEventCreateKeyboardEvent")] private static partial nint CreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
    [LibraryImport(CoreGraphics, EntryPoint = "CGEventSetFlags")] private static partial void SetFlags(nint keyEvent, ulong flags);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);

    // Counts the Command keys the page sees, and cancels Command and = or + while claimZoom is set.
    private const string Page = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic zoom smoke</title>
        <script>
        window.commandKeys = 0; window.claimed = 0; window.claimZoom = false;
        addEventListener("keydown", event => {
          if (!event.isTrusted || !event.metaKey) return;
          window.commandKeys++;
          if (window.claimZoom && (event.key === "=" || event.key === "+")) { event.preventDefault(); window.claimed++; }
        });
        </script></head>
        <body><main><h1>Zoom</h1><p>Command and + zooms.</p><input aria-label="Field"><input id="source" aria-label="Source"><input id="target" aria-label="Target"></main></body></html>
        """;
}
