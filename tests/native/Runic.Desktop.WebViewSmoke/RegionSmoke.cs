using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;

// --region: region mode's frames in the platform's web view (host-protocol.md, Regions; #866 R5). A host page frames a
// guest surface's page through a one-time frame launch address on the other loopback family, as a hosted application's
// region does, and the window routes that frame (RegionFrames). Checked on every engine:
// - the frame loads cross-site and gets nothing of the host page's: no bootstrap credential, and on WebView2 no message
//   of the frame reaches the window's top-level handlers;
// - a click and a Tab into the frame, as the host page sees them (blur, the active element, user activation), and
//   what a getUserMedia in the frame gets with the guest's grant: reported as REGION FINDING lines;
// - WebView2: a shortcut the frame leaves unhandled runs the host window's menu item;
// - WebView2 and WebKitGTK: a drop onto the frame goes to the region's guest, never to the window (D059.3).
// With --elevated, in an elevated process (GitHub's Windows runner), every listener of the host admits only elevated
// peers, as an elevated guest's do (Elevated-peer admission): the page and the frame still load, so the web view's own
// processes pass.
internal static partial class RegionSmoke
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(20);

    internal static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("Region", [new DesktopMenuItem("Region shortcut", "region.shortcut") { Shortcut = DesktopShortcut.Parse("Primary+Shift+K") }]),
    ];

    private const string HostPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic region host</title>
        <style>html,body{margin:0;height:100%} #before{position:fixed;left:8px;top:8px;height:40px}
        iframe{position:fixed;left:0;top:60px;width:100%;height:calc(100% - 60px);border:0}</style></head>
        <body><button id="before" type="button">Host button</button>
        <iframe id="region" sandbox="allow-scripts allow-same-origin allow-forms" allow="camera; microphone" title="Region guest"></iframe>
        <script>
        window.__guest = [];
        window.__events = [];
        const frame = document.getElementById("region");
        addEventListener("message", event => { if (event.source === frame.contentWindow) window.__guest.push(event.data); });
        addEventListener("blur", () => window.__events.push("window-blur"));
        addEventListener("focus", () => window.__events.push("window-focus"));
        frame.addEventListener("focus", () => window.__events.push("frame-focus"));
        addEventListener("keydown", event => { if (event.key === "Tab") window.__events.push("host-tab"); }, true);
        addEventListener("drop", () => window.__events.push("host-drop"), true);
        window.__state = () => JSON.stringify({
          events: window.__events,
          active: document.activeElement ? (document.activeElement.id || document.activeElement.localName) : null,
          hasFocus: document.hasFocus(),
          activation: navigator.userActivation ? navigator.userActivation.isActive : null,
        });
        window.__frameCenter = () => {
          const box = frame.getBoundingClientRect();
          return JSON.stringify({ x: box.left + box.width / 2, y: box.top + box.height / 2, scale: devicePixelRatio });
        };
        </script></body></html>
        """;

    private const string GuestPage = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Region guest</title></head>
        <body style="margin:0;height:100vh;background:#cde" data-runic-drop-target="guest-folder">
        <main><h1>Region guest</h1><input id="field" aria-label="Guest field"> <button id="act" type="button">Run system check</button></main>
        <script>
        const say = data => parent.postMessage(data, "*");
        addEventListener("drop", () => say({ kind: "drop" }));
        addEventListener("focus", () => say({ kind: "focus" }));
        addEventListener("keydown", event => say({ kind: "key", key: event.key }));
        document.getElementById("act").addEventListener("click", () => say({ kind: "pressed" }));
        const probe = {
          kind: "ready",
          origin: location.origin,
          bootstrap: typeof globalThis[Symbol.for("runic-desktop.bootstrap")],
          chromeWebview: typeof globalThis.chrome?.webview,
          webkitHandlers: typeof globalThis.webkit?.messageHandlers,
        };
        try { globalThis.chrome.webview.postMessage("runic-region-isolation-probe"); probe.posted = "posted"; }
        catch (error) { probe.posted = String(error && error.name); }
        say(probe);
        if (navigator.mediaDevices?.getUserMedia)
          navigator.mediaDevices.getUserMedia({ audio: true }).then(
            stream => { stream.getTracks().forEach(track => track.stop()); say({ kind: "media", result: "granted" }); },
            error => say({ kind: "media", result: String(error && error.name) }));
        else say({ kind: "media", result: "no mediaDevices" });
        </script></body></html>
        """;

    private sealed class Target : IDesktopMenuCommandTarget
    {
        internal ConcurrentQueue<string> Executed { get; } = new();
        public bool CanExecute(DesktopMenuItem item) => item.CommandId == "region.shortcut";
        public void Execute(DesktopMenuItem item) => Executed.Enqueue(item.CommandId);
    }

    internal static async Task<int> RunAsync(DesktopHost host, string hostName, bool elevated)
    {
        var refused = 0;
        if (elevated && !Environment.IsPrivilegedProcess)
        {
            Finding(hostName, "the smoke does not run elevated: elevated-peer admission unverified");
            elevated = false;
        }
        if (elevated)
        {
            host.PeerAdmission = LoopbackPeerAdmission.Elevated(peer =>
            {
                Interlocked.Increment(ref refused);
                Console.WriteLine($"REGION refused the loopback peer {peer}.");
            });
        }
        var topLevel = new ConcurrentQueue<string>();
        if (OperatingSystem.IsWindows()) WindowsWebView2Controller.TopLevelMessageObserver = (source, text) => topLevel.Enqueue($"{source} {text}");
        await using var guest = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(GuestPage) });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(HostPage) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 560, Height = 440, X = 220, Y = 20,
        });
        var target = new Target();
        window.MenuCommands = target;
        var hostDrops = Channel.CreateUnbounded<DesktopFileDropEventArgs>();
        window.FilesDropped += (_, drop) => hostDrops.Writer.TryWrite(drop);
        await window.FocusAsync();
        await surface.ExecuteJavaScriptAsync("return document.readyState;", StepTimeout);

        // The host page's origin is the frame's embedder; the guest's frame surface listens on the other family.
        var embedder = RegionFrames.Origin(surface.Url);
        var launch = await guest.BeginFrameLaunchAsync(embedder, allowSameSite: true, measuresHeight: false, CancellationToken.None);
        var guestOrigin = RegionFrames.Origin(launch.Url);
        Finding(hostName, $"frame origin {guestOrigin} for the page of {embedder}: {(launch.SameSite ? "same-site (no IPv6)" : "cross-site")}");
        var routed = Channel.CreateUnbounded<DesktopFileDropEventArgs>();
        using var route = window.AttachRegionFrame(new RegionFrameRoute(launch.Url, DesktopPermissionGrant.MediaCapture,
            drop => routed.Writer.TryWrite(drop), hostPage => Console.WriteLine($"REGION crashed (host page: {hostPage}).")))
            ?? throw new InvalidOperationException($"The {hostName} window routes no region frames.");
        await surface.ExecuteJavaScriptAsync($"document.getElementById('region').src = {JsonSerializer.Serialize(launch.Url.AbsoluteUri)}; return '';");

        var ready = (await GuestMessageAsync(surface, "ready"))!.Value;
        Require(ready.GetProperty("origin").GetString() == guestOrigin, $"the frame loaded {ready}, not the guest's page of {guestOrigin}");
        Require(ready.GetProperty("bootstrap").GetString() == "undefined", "the host page's bootstrap credential reached the guest's frame");
        Finding(hostName, $"in the guest's frame: chrome.webview {ready.GetProperty("chromeWebview")}, webkit.messageHandlers " +
            $"{ready.GetProperty("webkitHandlers")}, chrome.webview.postMessage {ready.GetProperty("posted")}");
        if (OperatingSystem.IsWindows())
        {
            await Task.Delay(1000);
            var heard = topLevel.Where(static message => message.Contains("runic-region-isolation-probe", StringComparison.Ordinal)).ToArray();
            Require(heard.Length == 0, $"the guest frame's message reached the window's top-level handlers: {string.Join("; ", heard)}");
            Pass(hostName, "the guest frame's chrome.webview messages reach only the frame (ICoreWebView2Frame), never the window's own handlers");
        }
        Pass(hostName, "the frame loaded the guest's page from its own origin without the host page's credential");
        var media = await GuestMessageAsync(surface, "media", required: false);
        Finding(hostName, $"getUserMedia in the frame with the guest's MediaCapture grant: {(media is { } result ? result.GetProperty("result").ToString() : "no answer")}");

        var input = Input.Open(hostName);
        if (input is null)
        {
            Finding(hostName, "no desktop input on this runner: focus, keys and drops unverified");
        }
        else
        {
            using (input)
            {
                await FocusChecksAsync(surface, window, input, hostName);
                if (OperatingSystem.IsWindows()) await MenuKeyAsync(surface, window, input, target, hostName);
                await DropAsync(surface, window, input, hostName, routed.Reader, hostDrops.Reader);
            }
        }
        if (elevated)
        {
            Require(Volatile.Read(ref refused) == 0, $"{refused} loopback peers were refused: the elevated window's own processes must pass");
            Pass(hostName, "elevated-peer admission let the elevated window's page and frame load and refused nothing");
        }
        if (OperatingSystem.IsWindows()) WindowsWebView2Controller.TopLevelMessageObserver = null;
        Console.WriteLine($"PASS {hostName} region smoke.");
        return 0;
    }

    // R4's open findings: a click and a Tab into a cross-origin frame as the host page sees them.
    private static async Task FocusChecksAsync(DesktopSurface surface, DesktopWindow window, Input input, string hostName)
    {
        await ResetAsync(surface);
        var center = await FrameCenterAsync(surface);
        await input.ClickAsync(window, center);
        await Task.Delay(800);
        var afterClick = await surface.ExecuteJavaScriptAsync("return window.__state();");
        Finding(hostName, $"click into the frame: {afterClick}");
        var guestFocus = await GuestMessageAsync(surface, "focus", required: false, clear: false);
        Finding(hostName, $"the guest's page {(guestFocus is null ? "saw no focus" : "got the focus")} after the click");

        await ResetAsync(surface);
        await input.TabAsync(window);
        await Task.Delay(800);
        var afterTab = await surface.ExecuteJavaScriptAsync("return window.__state();");
        Finding(hostName, $"Tab from the host's button: {afterTab}");
        using var state = JsonDocument.Parse(afterTab);
        if (state.RootElement.GetProperty("active").GetString() != "region")
            Finding(hostName, "the frame is not the active element after Tab");
    }

    // WebView2: Ctrl+Shift+K in the guest's frame, which leaves it unhandled, runs the host window's menu item.
    private static async Task MenuKeyAsync(DesktopSurface surface, DesktopWindow window, Input input, Target target, string hostName)
    {
        await ResetAsync(surface);
        await input.ClickAsync(window, await FrameCenterAsync(surface));
        await Task.Delay(500);
        _ = await GuestMessageAsync(surface, "focus", required: false);
        target.Executed.Clear();
        await input.ShortcutAsync(window, 'K');
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(TimeSpan.FromSeconds(5));
        while (target.Executed.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(100);
        var key = await GuestMessageAsync(surface, "key", required: false);
        Finding(hostName, $"Ctrl+Shift+K in the frame: the guest's page saw {(key is { } seen ? seen.GetProperty("key").ToString() : "no key")}");
        Require(target.Executed.TryDequeue(out var executed) && executed == "region.shortcut",
            "a shortcut the guest's frame left unhandled did not run the host window's menu item");
        Pass(hostName, "a shortcut the guest's frame left unhandled ran the host window's menu item");
    }

    // A drop onto the frame reaches the region's guest with the frame's label, never the window.
    private static async Task DropAsync(DesktopSurface surface, DesktopWindow window, Input input, string hostName,
        ChannelReader<DesktopFileDropEventArgs> routed, ChannelReader<DesktopFileDropEventArgs> hostDrops)
    {
        var root = Directory.CreateTempSubdirectory("runic-region-drop-");
        try
        {
            var file = Path.Combine(root.FullName, "region notes.txt");
            await File.WriteAllTextAsync(file, "dropped");
            var center = await FrameCenterAsync(surface);
            if (!await input.DropAsync(window, center, [file]))
            {
                Finding(hostName, "no drag source on this host: drops into the frame unverified");
                return;
            }
            var drop = await routed.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(15));
            Require(drop.Paths.SequenceEqual([file], StringComparer.OrdinalIgnoreCase) && drop.Target == "guest-folder",
                $"the guest's drop reported [{string.Join(", ", drop.Paths)}] with target '{drop.Target}'");
            await Task.Delay(1000);
            Require(!hostDrops.TryRead(out var leaked), $"the window received the frame's drop with target '{leaked?.Target}'");
            Pass(hostName, "a drop onto the region's frame reached its guest with the frame's label, and not the window");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task ResetAsync(DesktopSurface surface) => _ = await surface.ExecuteJavaScriptAsync(
        "window.__events = []; window.__guest = []; document.getElementById('before').focus(); return '';");

    private static async Task<(double X, double Y)> FrameCenterAsync(DesktopSurface surface)
    {
        using var center = JsonDocument.Parse(await surface.ExecuteJavaScriptAsync("return window.__frameCenter();"));
        var scale = center.RootElement.GetProperty("scale").GetDouble();
        return (center.RootElement.GetProperty("x").GetDouble() * scale, center.RootElement.GetProperty("y").GetDouble() * scale);
    }

    // The guest's first message of this kind; null when none arrived and it is not required.
    private static async Task<JsonElement?> GuestMessageAsync(DesktopSurface surface, string kind, bool required = true, bool clear = true)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(required ? StepTimeout : TimeSpan.FromSeconds(5));
        do
        {
            var json = await surface.ExecuteJavaScriptAsync(
                $"const found = window.__guest.find(m => m && m.kind === {JsonSerializer.Serialize(kind)}); " +
                (clear ? $"if (found) window.__guest = window.__guest.filter(m => m !== found); " : "") +
                "return found ? JSON.stringify(found) : '';", StepTimeout);
            if (json.Length > 0)
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);
        if (required) throw new TimeoutException($"The guest's frame sent no '{kind}' message.");
        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Region smoke: " + message + ".");
    }

    private static void Pass(string hostName, string message) => Console.WriteLine($"PASS {hostName} region: {message}.");

    private static void Finding(string hostName, string message) => Console.WriteLine($"REGION FINDING {hostName}: {message}");

    // Real input on the test desktop: SendInput (Windows), NSEvent (macOS) or XTEST (Linux, under Xvfb). Points are in
    // the page's device pixels from the web view's top-left corner.
    private abstract class Input : IDisposable
    {
        internal static Input? Open(string hostName) =>
            OperatingSystem.IsWindows() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? null : new WindowsInput())
            : OperatingSystem.IsMacOS() ? new MacInput()
            : OperatingSystem.IsLinux() && Pointer.Open() is { } pointer ? new LinuxInput(pointer)
            : null;

        internal abstract Task ClickAsync(DesktopWindow window, (double X, double Y) point);

        internal abstract Task TabAsync(DesktopWindow window);

        internal virtual Task ShortcutAsync(DesktopWindow window, char key) => Task.CompletedTask;

        // False when this host has no drag source for the smoke.
        internal virtual Task<bool> DropAsync(DesktopWindow window, (double X, double Y) point, string[] paths) => Task.FromResult(false);

        public virtual void Dispose() { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed partial class WindowsInput : Input
    {
        internal override async Task ClickAsync(DesktopWindow window, (double X, double Y) point)
        {
            var screen = await OnDpiAwareThreadAsync(() => ClientToScreenPoint(window.NativeHandle, point));
            await OnDpiAwareThreadAsync(() =>
            {
                _ = SetCursorPos(screen.X, screen.Y);
                Mouse(0x0002); // MOUSEEVENTF_LEFTDOWN
                Thread.Sleep(60);
                Mouse(0x0004); // MOUSEEVENTF_LEFTUP
                return 0;
            });
        }

        internal override async Task TabAsync(DesktopWindow window)
        {
            await PressAsync([(0x09, false), (0x09, true)]);
        }

        internal override async Task ShortcutAsync(DesktopWindow window, char key)
        {
            await PressAsync([(0x11, false), (0x10, false), (key, false), (key, true), (0x10, true), (0x11, true)]);
        }

        internal override async Task<bool> DropAsync(DesktopWindow window, (double X, double Y) point, string[] paths) =>
            await WindowsFileDropSmoke.DragOntoAsync(window.NativeHandle, paths, ((int)point.X, (int)point.Y)) is not null;

        private static async Task PressAsync((int Key, bool Up)[] keys)
        {
            foreach (var (key, up) in keys)
            {
                Key((ushort)key, up);
                await Task.Delay(60);
            }
        }

        private static Task<T> OnDpiAwareThreadAsync<T>(Func<T> work)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                _ = SetThreadDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
                try { done.TrySetResult(work()); }
                catch (Exception error) { done.TrySetException(error); }
            }) { IsBackground = true };
            thread.Start();
            return done.Task;
        }

        // The client area's device pixels: the web view fills it.
        private static (int X, int Y) ClientToScreenPoint(nint window, (double X, double Y) point)
        {
            var screen = new NativePoint { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
            _ = ClientToScreen(window, ref screen);
            return (screen.X, screen.Y);
        }

        private static unsafe void Mouse(uint flags)
        {
            var input = new NativeInput { Type = 0 };
            input.Data.Mouse.Flags = flags;
            _ = SendInput(1, &input, sizeof(NativeInput));
        }

        private static unsafe void Key(ushort key, bool up)
        {
            var input = new NativeInput { Type = 1 };
            input.Data.Keyboard.Key = key;
            input.Data.Keyboard.Flags = up ? 0x0002u : 0;
            _ = SendInput(1, &input, sizeof(NativeInput));
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint Data;
            internal uint Flags;
            internal uint Time;
            internal nint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            internal ushort Key;
            internal ushort Scan;
            internal uint Flags;
            internal uint Time;
            internal nint Extra;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputData
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeInput
        {
            internal uint Type;
            internal InputData Data;
        }

        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint window, ref NativePoint point);
        [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetCursorPos(int x, int y);
        [LibraryImport("user32.dll", SetLastError = true)] private static unsafe partial uint SendInput(uint count, NativeInput* inputs, int size);
        [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
    }

    // NSEvent clicks sent to the window, and CGEvent key presses posted to the application's queue, as the macOS
    // smokes drive the window (MacOsWindowChromeSmoke, MacOsZoomSmoke). Points are the page's CSS pixels: points.
    private sealed partial class MacInput : Input
    {
        private const nuint LeftMouseDown = 1, LeftMouseUp = 2;

        internal override Task ClickAsync(DesktopWindow window, (double X, double Y) point) =>
            window.InvokeNativeAsync(handle => SendClick(handle, point.X, point.Y)).AsTask();

        internal override Task TabAsync(DesktopWindow window) => window.InvokeNativeAsync(_ =>
        {
            var pool = Send(Send(Class("NSAutoreleasePool"), Sel("alloc")), Sel("init"));
            try
            {
                var application = Send(Class("NSApplication"), Sel("sharedApplication"));
                for (var press = 0; press < 2; press++)
                {
                    var keyEvent = CreateKeyboardEvent(0, 48, down: press == 0); // kVK_Tab
                    Args(application, Sel("postEvent:atStart:"), Arg(Class("NSEvent"), Sel("eventWithCGEvent:"), keyEvent), 0);
                    CFRelease(keyEvent);
                }
            }
            finally { Send(pool, Sel("drain")); }
        }).AsTask();

        private static unsafe void SendClick(nint handle, double x, double y)
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
                        type, inWindow, 0, Uptime(), number, 0, 0, 1, type == LeftMouseDown ? 1f : 0f);
                    Arg(handle, Sel("sendEvent:"), mouseEvent);
                }
            }
            finally { Send(pool, Sel("drain")); }
        }

        private static unsafe double Uptime() => ((delegate* unmanaged[Cdecl]<nint, nint, double>)MessageSend)(
            Send(Class("NSProcessInfo"), Sel("processInfo")), Sel("systemUptime"));

        private static unsafe CGRect Rect(nint target, nint selector) => ((delegate* unmanaged[Cdecl]<nint, nint, CGRect>)MessageSend)(target, selector);

        private static unsafe CGPoint ConvertPoint(nint target, nint selector, CGPoint point, nint view) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, CGPoint, nint, CGPoint>)MessageSend)(target, selector, point, view);

        [StructLayout(LayoutKind.Sequential)] private readonly record struct CGPoint(double X, double Y);
        [StructLayout(LayoutKind.Sequential)] private readonly record struct CGRect(double X, double Y, double Width, double Height);

        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private static nint MessageSend { get; } = OperatingSystem.IsMacOS() ? NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend") : 0;
        [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Class(string name);
        [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Arg(nint target, nint selector, nint value);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Args(nint target, nint selector, nint first, nint second);
        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial byte Bool(nint target, nint selector);
        [LibraryImport(CoreGraphics, EntryPoint = "CGEventCreateKeyboardEvent")] private static partial nint CreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.U1)] bool down);
        [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")] private static partial void CFRelease(nint value);
    }

    // XTEST on the test display, at the web view's position on the root window (GDK 3).
    private sealed partial class LinuxInput(Pointer pointer) : Input
    {
        internal override async Task ClickAsync(DesktopWindow window, (double X, double Y) point)
        {
            var origin = await OriginAsync(window);
            await pointer.ClickAsync(origin.X + (int)point.X, origin.Y + (int)point.Y);
        }

        internal override Task TabAsync(DesktopWindow window)
        {
            pointer.Key(0xff09);
            return Task.CompletedTask;
        }

        // A GTK drag source dragged onto the frame, as FileDropSmoke drags onto a window.
        internal override async Task<bool> DropAsync(DesktopWindow window, (double X, double Y) point, string[] paths)
        {
            var origin = await OriginAsync(window);
            nint source = 0;
            await window.InvokeNativeAsync(_ => source = FileDropSmoke.DragSource.Create([.. paths.Select(static path => new Uri(path).AbsoluteUri)],
                x: 10, y: 10, size: 150, actions: FileDropSmoke.DragSource.Copy));
            try
            {
                await Task.Delay(500);
                await pointer.DragAsync((85, 85), (origin.X + (int)point.X, origin.Y + (int)point.Y));
                return true;
            }
            finally
            {
                if (source != 0) await window.InvokeNativeAsync(_ => FileDropSmoke.DragSource.Destroy(source));
            }
        }

        private static async Task<(int X, int Y)> OriginAsync(DesktopWindow window)
        {
            int x = 0, y = 0;
            await window.InvokeNativeAsync(handle =>
            {
                var gdkWindow = WidgetGetWindow(handle);
                if (gdkWindow != 0) _ = WindowGetOrigin(gdkWindow, out x, out y);
            });
            return (x, y);
        }

        public override void Dispose()
        {
            pointer.Dispose();
            base.Dispose();
        }

        [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_window")] private static partial nint WidgetGetWindow(nint widget);
        [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_window_get_origin")] private static partial int WindowGetOrigin(nint window, out int x, out int y);
    }
}
