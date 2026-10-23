using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Runic.Desktop;

// B11 (I-026) on Windows: a drag out of the WebView2 window. SendInput presses the button on a draggable element, the
// page's dragstart cancels the HTML5 drag, and while the button is still held (and the WebView2 browser process holds
// the mouse capture) the host starts an OLE drag on the window thread. The pointer then moves to a drop target window
// of this process and releases.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class WindowsFileDropSmoke
{
    internal const string DragOutPage =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic drag out</title>
        <style>html,body{margin:0;height:100%} #item{position:fixed;left:40px;top:40px;width:160px;height:120px;background:#cde}</style></head>
        <body><div id="item" draggable="true">drag me</div>
        <script>
        window.__dragStarts = 0;
        // The page cancels its own drag and asks the application for a native one, here through the smoke's polling.
        document.getElementById("item").addEventListener("dragstart", event => { event.preventDefault(); window.__dragStarts++; });
        </script></body></html>
        """;

    // Paths, text and an Escape out of the WebView2 window onto a drop target window; a call without a held button.
    // False when the runner cannot drive the drag.
    internal static async Task<bool> RunDragOutAsync(DesktopHost host, string[] paths)
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(DragOutPage) });
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded, Width = 480, Height = 360, X = 300, Y = 200,
        });
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrag))
            throw new InvalidOperationException("The WebView2 window did not advertise drags out.");
        await window.FocusAsync();
        await surface.ExecuteJavaScriptAsync("return document.readyState;", TimeSpan.FromSeconds(10));
        // Without a held button the drag does not start.
        if (await window.StartDragAsync(new Runic.Platform.DesktopDragData { Text = "late" }) != Runic.Platform.DesktopDragEffects.None)
            throw new InvalidOperationException("A drag without a held button did not end at once with no effect.");
        using var target = DropTargetWindow.Start(x: 900, y: 220, size: 200);
        await target.Ready.WaitAsync(TimeSpan.FromSeconds(10));

        var files = await DragOutAsync(surface, window, target, new Runic.Platform.DesktopDragData
        {
            Paths = [.. paths], AllowedEffects = Runic.Platform.DesktopDragEffects.Copy | Runic.Platform.DesktopDragEffects.Move,
        }, DragEnd.Drop);
        if (files is null)
        {
            Console.WriteLine("Drag out: unverified on this runner: its desktop does not deliver SendInput to a window of this process.");
            return false;
        }
        if (files.Effect != Runic.Platform.DesktopDragEffects.Copy || !files.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The drag of files ended with {files.Effect} and dropped [{string.Join(", ", files.Paths)}].");

        var text = await DragOutAsync(surface, window, target, new Runic.Platform.DesktopDragData
        {
            Text = "Runic drag text", Uris = [new Uri("https://example.com/runic")],
        }, DragEnd.Drop) ?? throw new InvalidOperationException("The second drag found no held button.");
        if (text.Effect != Runic.Platform.DesktopDragEffects.Copy || text.Text != "Runic drag text" || text.Paths.Length != 0)
            throw new InvalidOperationException($"The drag of text ended with {text.Effect}, text '{text.Text}' and {text.Paths.Length} paths.");

        var escaped = await DragOutAsync(surface, window, target, new Runic.Platform.DesktopDragData { Paths = [.. paths] }, DragEnd.Escape)
            ?? throw new InvalidOperationException("The third drag found no held button.");
        if (escaped.Effect != Runic.Platform.DesktopDragEffects.None || escaped.Paths.Length != 0)
            throw new InvalidOperationException($"The drag cancelled with Escape ended with {escaped.Effect}.");
        Console.WriteLine($"Drag out passed: files dropped with Copy [{string.Join(", ", files.Paths)}], text dropped, Escape ended with None, a late call with None.");
        return true;
    }

    internal const string FramePage =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic frame drops</title>
        <style>html,body{margin:0;height:100%} iframe{position:fixed;top:0;width:50%;height:100%;border:0}</style></head>
        <body><iframe id="same" src="frame.html" style="left:0"></iframe>
        <iframe id="other" src="data:text/html,%3Cbody%20style%3D%22margin%3A0%3Bheight%3A100vh%3Bbackground%3A%23edc%22%20data-runic-drop-target%3D%22other%22%3E%3C%2Fbody%3E" style="left:50%"></iframe>
        </body></html>
        """;

    internal const string FrameDocument =
        """
        <!doctype html><html><head><meta charset="utf-8"><title>Same-origin frame</title></head>
        <body style="margin:0;height:100vh;background:#cde" data-runic-drop-target="frame-folder" data-runic-drop-effect="move">
        <script>window.__drops = 0; addEventListener("drop", () => window.__drops++);</script></body></html>
        """;

    // D037.2: a drop into a frame of the presented origin is delivered with the frame's label and effect; a drop into a
    // frame of another origin is not, and navigates nothing.
    internal static async Task RunFrameDropsAsync(DesktopHost host, string[] paths)
    {
        var root = Directory.CreateTempSubdirectory("runic-frame-drop-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "index.html"), FramePage);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, "frame.html"), FrameDocument);
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Directory(root.FullName) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 480, Height = 360, X = 300, Y = 200,
            });
            await window.FocusAsync();
            await surface.ExecuteJavaScriptAsync("return document.readyState;", TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            var before = await surface.ExecuteJavaScriptAsync("return location.href;");
            var drops = System.Threading.Channels.Channel.CreateUnbounded<DesktopFileDropEventArgs>();
            window.FilesDropped += (_, drop) => drops.Writer.TryWrite(drop);
            var client = ClientSize(window.NativeHandle);
            var same = await DragOntoAsync(window.NativeHandle, paths, (client.Width / 4, client.Height / 2));
            if (same is null)
            {
                Console.WriteLine("Frame drops: unverified on this runner: its desktop does not deliver SendInput to a window of this process.");
                return;
            }
            var drop = await drops.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            if (drop.Target != "frame-folder" || drop.Effect != Runic.Platform.DesktopDragEffects.Move
                || !drop.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The same-origin frame's drop reported target '{drop.Target}', {drop.Effect} and [{string.Join(", ", drop.Paths)}].");
            _ = await DragOntoAsync(window.NativeHandle, paths, (client.Width * 3 / 4, client.Height / 2));
            await Task.Delay(2000);
            if (drops.Reader.TryRead(out var foreign))
                throw new InvalidOperationException($"A drop into a frame of another origin was delivered with target '{foreign.Target}'.");
            var after = await surface.ExecuteJavaScriptAsync("return location.href;");
            if (after != before) throw new InvalidOperationException($"The window moved from {before} to {after}.");
            Console.WriteLine("Frame drops passed: the same-origin frame's drop carried its label and Move; the other origin's frame delivered nothing.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // The shell's OLE drag of the paths onto a client point of the window, from an STA thread; null without desktop input.
    private static async Task<int?> DragOntoAsync(nint window, string[] paths, (int X, int Y) clientPoint)
    {
        var drag = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { drag.TrySetResult(Drag(window, paths, clientPoint)); }
            catch (Exception error) { drag.TrySetException(error); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        int effect;
        try { effect = await drag.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            Escape();
            throw new TimeoutException("The OLE drag onto the frame did not end within 30 seconds.");
        }
        return effect == NoDesktopInput ? null : effect;
    }

    private static (int X, int Y) ClientToScreenPoint(nint window, (int X, int Y) point)
    {
        var screen = new Point { X = point.X, Y = point.Y };
        _ = ClientToScreen(window, ref screen);
        return (screen.X, screen.Y);
    }

    private static (int Width, int Height) ClientSize(nint window)
    {
        _ = GetClientRect(window, out var bounds);
        return (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect bounds);

    // How a drag of the smoke ends: dropped on the target window, or cancelled with Escape over it.
    internal enum DragEnd { Drop, Escape }

    internal sealed record DragOutcome(Runic.Platform.DesktopDragEffects Effect, string[] Paths, string? Text);

    // One drag per call: press on the element, wait for the page's dragstart, start the drag through the window while
    // the button is held, then move to the target and release or press Escape. Null when the runner cannot drive it.
    internal static async Task<DragOutcome?> DragOutAsync(DesktopSurface surface, DesktopWindow window, DropTargetWindow target,
        Runic.Platform.DesktopDragData data, DragEnd end)
    {
        var client = ClientOrigin(window.NativeHandle);
        // The element's centre, in physical pixels: the page is at 100 % on the runner's 96 DPI desktop.
        var press = (X: client.X + 120, Y: client.Y + 100);
        var drop = (X: target.X + target.Size / 2, Y: target.Y + target.Size / 2);
        var startsBefore = await surface.ExecuteJavaScriptAsync("return String(window.__dragStarts);");
        target.Reset();
        Mouse(press.X, press.Y, MouseMove);
        await Task.Delay(300);
        Mouse(press.X, press.Y, MouseLeftDown);
        await Task.Delay(200);
        if (GetAsyncKeyState(0x01) >= 0)
        {
            Mouse(press.X, press.Y, MouseLeftUp);
            return null;
        }
        // Chromium starts the HTML5 drag after a few pixels of movement with the button held.
        for (var step = 1; step <= 10; step++)
        {
            Mouse(press.X + step * 2, press.Y + step, MouseMove);
            await Task.Delay(30);
        }
        var started = false;
        for (var attempt = 0; attempt < 50 && !started; attempt++)
        {
            started = await surface.ExecuteJavaScriptAsync("return String(window.__dragStarts);") != startsBefore;
            if (!started) await Task.Delay(50);
        }
        if (!started)
        {
            Mouse(press.X, press.Y, MouseLeftUp);
            throw new InvalidOperationException("The page saw no dragstart.");
        }
        var drag = window.StartDragAsync(data).AsTask();
        var mover = Task.Run(async () =>
        {
            await Task.Delay(400);
            var from = (X: press.X + 20, Y: press.Y + 10);
            for (var step = 1; step <= 30; step++)
            {
                Mouse(from.X + (drop.X - from.X) * step / 30, from.Y + (drop.Y - from.Y) * step / 30, MouseMove);
                await Task.Delay(30);
            }
            for (var wiggle = 0; wiggle < 10; wiggle++)
            {
                Mouse(drop.X + wiggle % 2, drop.Y, MouseMove);
                await Task.Delay(50);
            }
            await Task.Delay(300);
            if (end == DragEnd.Escape)
            {
                Escape();
                await Task.Delay(300);
            }
            Mouse(drop.X, drop.Y, MouseLeftUp);
        });
        Runic.Platform.DesktopDragEffects effect;
        try
        {
            effect = await drag.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Escape();
            throw new TimeoutException($"The drag out did not end within 30 seconds (the target saw it enter: {target.Entered}).");
        }
        await mover;
        return new DragOutcome(effect, target.Dropped, target.Text);
    }

    private static (int X, int Y) ClientOrigin(nint window)
    {
        var point = new Point();
        _ = ClientToScreen(window, ref point);
        return (point.X, point.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    // A top-level window on its own STA thread that accepts CF_HDROP drops.
    internal sealed partial class DropTargetWindow : IDisposable
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile string[] _dropped = [];
        private volatile string? _text;
        private volatile bool _entered;
        private Thread? _thread;
        private uint _threadId;

        internal Task Ready => _ready.Task;
        internal string[] Dropped => _dropped;
        internal string? Text => _text;
        internal bool Entered => _entered;
        internal int X { get; private init; }
        internal int Y { get; private init; }
        internal int Size { get; private init; }

        internal void Reset()
        {
            _dropped = [];
            _text = null;
            _entered = false;
        }

        internal static DropTargetWindow Start(int x, int y, int size)
        {
            var target = new DropTargetWindow { X = x, Y = y, Size = size };
            target._thread = new Thread(() => target.Run(x, y, size)) { IsBackground = true };
            target._thread.SetApartmentState(ApartmentState.STA);
            target._thread.Start();
            return target;
        }

        private unsafe void Run(int x, int y, int size)
        {
            _threadId = GetCurrentThreadId();
            _ = SetThreadDpiAwarenessContext(-4);
            var hr = OleInitialize(0);
            if (hr < 0)
            {
                _ready.TrySetException(Marshal.GetExceptionForHR(hr)!);
                return;
            }
            var window = CreateWindowEx(0x00000008 | 0x00000080, "STATIC", "Runic drop target", 0x80000000 | 0x10000000,
                x, y, size, size, 0, 0, 0, 0);
            var dropTarget = new DropTarget(this);
            var pointer = (nint)ComInterfaceMarshaller<IDropTarget>.ConvertToUnmanaged(dropTarget);
            hr = RegisterDragDrop(window, pointer);
            if (hr < 0)
            {
                _ready.TrySetException(Marshal.GetExceptionForHR(hr)!);
                return;
            }
            _ready.TrySetResult();
            var message = stackalloc byte[64];
            while (GetMessage(message, 0, 0, 0) > 0)
            {
                _ = TranslateMessage(message);
                _ = DispatchMessage(message);
            }
            _ = RevokeDragDrop(window);
            ComInterfaceMarshaller<IDropTarget>.Free((void*)pointer);
            _ = DestroyWindow(window);
            OleUninitialize();
        }

        public void Dispose()
        {
            if (_threadId != 0) _ = PostThreadMessage(_threadId, 0x0012, 0, 0); // WM_QUIT
            _thread?.Join(TimeSpan.FromSeconds(5));
        }

        internal void OnEnter() => _entered = true;
        internal void OnDrop(string[] paths, string? text)
        {
            _dropped = paths;
            _text = text;
        }
    }

    [GeneratedComInterface, Guid("00000122-0000-0000-C000-000000000046")]
    internal partial interface IDropTarget
    {
        [PreserveSig] int DragEnter(nint dataObject, uint keyState, long point, ref uint effect);
        [PreserveSig] int DragOver(uint keyState, long point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(nint dataObject, uint keyState, long point, ref uint effect);
    }

    [GeneratedComClass]
    private sealed partial class DropTarget(DropTargetWindow owner) : IDropTarget
    {
        public int DragEnter(nint dataObject, uint keyState, long point, ref uint effect)
        {
            owner.OnEnter();
            effect &= 1;
            return 0;
        }

        public int DragOver(uint keyState, long point, ref uint effect)
        {
            effect &= 1;
            return 0;
        }

        public int DragLeave() => 0;

        public unsafe int Drop(nint dataObject, uint keyState, long point, ref uint effect)
        {
            owner.OnDrop(ReadPaths(dataObject), ReadText(dataObject));
            effect &= 1;
            return 0;
        }

        private static unsafe string[] ReadPaths(nint dataObject)
        {
            var format = new FormatEtc { Format = 15, Aspect = 1, Index = -1, Tymed = 1 }; // CF_HDROP, DVASPECT_CONTENT, TYMED_HGLOBAL
            var medium = default(StgMedium);
            if (((delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int>)(*(nint**)dataObject)[3])(dataObject, &format, &medium) < 0) return [];
            try
            {
                var count = DragQueryFile(medium.Handle, 0xFFFFFFFF, null, 0);
                var paths = new string[count];
                var buffer = stackalloc char[1024];
                for (uint index = 0; index < count; index++)
                {
                    var length = DragQueryFile(medium.Handle, index, buffer, 1024);
                    paths[index] = new string(buffer, 0, (int)length);
                }
                return paths;
            }
            finally
            {
                ReleaseStgMedium(&medium);
            }
        }

        private static unsafe string? ReadText(nint dataObject)
        {
            var format = new FormatEtc { Format = 13, Aspect = 1, Index = -1, Tymed = 1 }; // CF_UNICODETEXT
            var medium = default(StgMedium);
            if (((delegate* unmanaged[Stdcall]<nint, FormatEtc*, StgMedium*, int>)(*(nint**)dataObject)[3])(dataObject, &format, &medium) < 0) return null;
            try
            {
                var locked = GlobalLock(medium.Handle);
                try { return locked == 0 ? null : Marshal.PtrToStringUni(locked); }
                finally { _ = GlobalUnlock(medium.Handle); }
            }
            finally
            {
                ReleaseStgMedium(&medium);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FormatEtc
    {
        internal ushort Format;
        internal nint TargetDevice;
        internal uint Aspect;
        internal int Index;
        internal uint Tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StgMedium
    {
        internal uint Tymed;
        internal nint Handle;
        internal nint Release;
    }

    [LibraryImport("ole32.dll")] private static partial int RegisterDragDrop(nint window, nint target);
    [LibraryImport("ole32.dll")] private static partial int RevokeDragDrop(nint window);
    [LibraryImport("ole32.dll")] private static unsafe partial void ReleaseStgMedium(StgMedium* medium);
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")] private static unsafe partial uint DragQueryFile(nint drop, uint index, char* file, uint length);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] private static unsafe partial int GetMessage(byte* message, nint window, uint minimum, uint maximum);
    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);
    [LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

}
