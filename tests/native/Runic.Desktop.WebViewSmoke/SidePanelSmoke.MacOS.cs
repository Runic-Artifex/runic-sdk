using System.Globalization;
using System.Runtime.InteropServices;
using Runic.Desktop;

// sdk#864, P3, --side-panel on macOS: the checks of SidePanelSmoke.cs that read AppKit.
internal static partial class SidePanelSmoke
{
    private static partial class MacOS
    {
        private const ushort KeyTab = 0x30;
        private const ulong ShiftFlag = 0x20000;
        private static DesktopWindow? s_window;
        // The page's WKWebView: the content view's only subview before a panel opens.
        private static nint s_page;

        internal static async Task FindPageAsync(DesktopWindow window)
        {
            s_window = window;
            var count = 0L;
            await window.InvokeNativeAsync(handle =>
            {
                var subviews = Send(Send(handle, Sel("contentView")), Sel("subviews"));
                count = Send(subviews, Sel("count"));
                s_page = count == 0 ? 0 : Arg(subviews, Sel("objectAtIndex:"), 0);
            });
            Require(count == 1 && s_page != 0, $"one view in the window's content view before a panel opens, not {count}");
        }

        // The page's rectangle on the screen stays the same across operation: AppKit set the frame and both views in one
        // turn of the main thread. Whether each displayed frame showed it there is unverified: no frame capture on macOS.
        internal static async Task KeepsPageAsync(string what, Func<Task> operation)
        {
            var before = await PageOnScreenAsync();
            await operation();
            await Task.Delay(Settle);
            var after = await PageOnScreenAsync();
            Require(Same(before, after), $"the page at {Describe(before)} on the screen after '{what}', not {Describe(after)}");
            Console.WriteLine($"Side panel: {what}: the page stayed at {Describe(after)} on the screen (frames not captured on macOS).");
        }

        private static async Task<CGRect> PageOnScreenAsync()
        {
            CGRect rect = default;
            await s_window!.InvokeNativeAsync(handle =>
            {
                var inWindow = RectRectNint(s_page, Sel("convertRect:toView:"), Rect(s_page, Sel("bounds")), 0);
                rect = RectRect(handle, Sel("convertRectToScreen:"), inWindow);
            });
            return rect;
        }

        // The content view's accessibility children, which VoiceOver reads in order, are in the views' visual order.
        // VoiceOver itself does not run here.
        internal static async Task RequireReadingOrderAsync(DesktopWindow window, string edge)
        {
            var elements = new List<(string Role, double X)>();
            await window.InvokeNativeAsync(handle =>
            {
                var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
                try
                {
                    var children = Send(Send(handle, Sel("contentView")), Sel("accessibilityChildren"));
                    // One group around both views: its children.
                    if (children != 0 && Send(children, Sel("count")) == 1)
                        children = Send(Arg(children, Sel("objectAtIndex:"), 0), Sel("accessibilityChildren"));
                    for (long i = 0, count = children == 0 ? 0 : Send(children, Sel("count")); i < count; i++)
                    {
                        var child = Arg(children, Sel("objectAtIndex:"), (nint)i);
                        if (Bool(child, Sel("respondsToSelector:"), Sel("accessibilityFrame")) == 0) continue;
                        var role = Bool(child, Sel("respondsToSelector:"), Sel("accessibilityRole")) != 0 ? Text(Send(child, Sel("accessibilityRole"))) : "?";
                        elements.Add((role, Rect(child, Sel("accessibilityFrame")).X));
                    }
                }
                finally { Send(pool, Sel("drain")); }
            });
            var listed = string.Join(", ", elements.Select(element => $"{element.Role} at x {element.X.ToString("0.#", CultureInfo.InvariantCulture)}"));
            if (elements.Count < 2)
            {
                Console.WriteLine($"Side panel: accessibility order on {edge} unverified: the content view lists {elements.Count} element(s) ({listed}).");
                return;
            }
            Require(elements.Zip(elements.Skip(1)).All(pair => pair.First.X < pair.Second.X),
                $"the accessibility children in visual order on {edge}: {listed}");
            Console.WriteLine($"Side panel: accessibility lists the views in visual order on {edge}: {listed}.");
        }

        internal static ValueTask PressTabAsync(DesktopWindow window, bool shift) => window.InvokeNativeAsync(_ =>
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try
            {
                var application = Send(Class("NSApplication"), Sel("sharedApplication"));
                for (var press = 0; press < 2; press++)
                {
                    var keyEvent = CreateKeyboardEvent(0, KeyTab, down: press == 0);
                    SetFlags(keyEvent, shift ? ShiftFlag : 0);
                    Args(application, Sel("postEvent:atStart:"), Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), keyEvent), 0);
                    CFRelease(keyEvent);
                }
            }
            finally { Send(pool, Sel("drain")); }
        });

        // The window's first responder: the page's view, another subview of the content view (the panel's), or its class.
        internal static async Task<string> DescribeFirstResponderAsync(DesktopWindow window)
        {
            var description = "";
            await window.InvokeNativeAsync(handle =>
            {
                var responder = Send(handle, Sel("firstResponder"));
                var subviews = Send(Send(handle, Sel("contentView")), Sel("subviews"));
                var index = -1L;
                for (long i = 0, count = Send(subviews, Sel("count")); i < count; i++)
                    if (Arg(subviews, Sel("objectAtIndex:"), (nint)i) == responder) index = i;
                description = responder == 0 ? "none" : responder == s_page ? "the page's view" : index >= 0 ? $"the content view's subview {index} (the panel's)"
                    : Text(Send(Send(responder, Sel("className")), Sel("description")));
            });
            return description;
        }

        internal static async Task<double> ContentMinimumWidthAsync(DesktopWindow window)
        {
            double width = 0;
            await window.InvokeNativeAsync(handle => width = Size(handle, Sel("contentMinSize")).Width);
            return width;
        }

        // A zoomed window keeps its frame when its panel closes, and once it is restored it has its bounds before the panel.
        internal static async Task ExerciseZoomedCloseAsync(DesktopWindow window, DesktopWindowState start, int native)
        {
            var panel = await OpenQuietlyAsync(window);
            await WaitForAsync(() => window.State!.Bounds!.Width == start.Bounds!.Width + native, "the window grown by the panel");
            await window.ToggleMaximizedAsync();
            await WaitForAsync(() => window.State!.IsMaximized, "the window zoomed with the panel");
            await Task.Delay(Settle);
            var zoomed = window.State!.Bounds!;
            await panel.CloseAsync();
            Require(!panel.IsOpen && window.SidePanel is null, "the panel of the zoomed window closed");
            await Task.Delay(Settle);
            Require(window.State!.IsMaximized && SameFrame(window.State.Bounds!, zoomed), $"the zoomed window to keep its frame {zoomed}, not {window.State.Bounds}");
            await window.ToggleMaximizedAsync();
            await WaitForAsync(() => !window.State!.IsMaximized && SameFrame(window.State.Bounds!, start.Bounds!),
                $"the restored window at its bounds before the panel, {start.Bounds}");
            Console.WriteLine($"Side panel: a zoomed window's panel closed; restored, the window is at {window.State!.Bounds}.");
        }

        private static bool Same(CGRect a, CGRect b) =>
            Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(a.Width - b.Width) < 0.01 && Math.Abs(a.Height - b.Height) < 0.01;

        private static string Describe(CGRect rect) => string.Create(CultureInfo.InvariantCulture,
            $"({rect.X:0.##}, {rect.Y:0.##}, {rect.Width:0.##} × {rect.Height:0.##})");

        private static string Text(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"))) ?? "";

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct CGRect(double X, double Y, double Width, double Height);

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct CGSize(double Width, double Height);

        // A CGRect comes back in registers on arm64 and through memory (objc_msgSend_stret) on x86-64.
        private static readonly nint MessageSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
        private static readonly nint MessageSendStructure = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? MessageSend : NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret");

        private static unsafe CGRect Rect(nint target, nint selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect>)MessageSendStructure)(target, selector);

        private static unsafe CGRect RectRect(nint target, nint selector, CGRect value) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, CGRect>)MessageSendStructure)(target, selector, value);

        private static unsafe CGRect RectRectNint(nint target, nint selector, CGRect value, nint view) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGRect, nint, CGRect>)MessageSendStructure)(target, selector, value, view);

        private static unsafe CGSize Size(nint target, nint selector) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGSize>)MessageSend)(target, selector);

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
        [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Args(nint target, nint selector, nint first, nint second);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector, nint value);
        [LibraryImport(CoreGraphics, EntryPoint = "CGEventCreateKeyboardEvent")] private static partial nint CreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
        [LibraryImport(CoreGraphics, EntryPoint = "CGEventSetFlags")] private static partial void SetFlags(nint keyEvent, ulong flags);
        [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);
    }
}
