using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop;

// W315-006: a real X11 drag and drop from a GTK 3 drag source onto the GTK 3 WebKitGTK window, driven through XTEST
// under Xvfb. The window reports the native paths with the page's routing label, the page still receives its drop
// event, and the window does not navigate to the dropped file. Then B11's drag out and frame drops (LinuxDragOutSmoke).
internal static partial class FileDropSmoke
{
    internal const string Page =
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script><title>Runic file drop</title>
        <style>html,body{margin:0;height:100%} #target{position:fixed;inset:0;background:#cde}</style></head>
        <body><div id="target" data-runic-drop-target="folder-7">drop here</div>
        <script>
        window.__pageDrops = 0;
        window.__pageDragOvers = 0;
        addEventListener("dragover", () => window.__pageDragOvers++);
        addEventListener("drop", () => window.__pageDrops++);
        </script></body></html>
        """;

    internal static async Task RunAsync(DesktopHost host)
    {
        if (OperatingSystem.IsWindows())
        {
            await WindowsFileDropSmoke.RunAsync(host);
            return;
        }
        if (!OperatingSystem.IsLinux()) return;
        if (Pointer.Open() is not { } pointer)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                throw new InvalidOperationException("XTEST (libXtst.so.6) is required for the file drop smoke in CI.");
            Console.WriteLine("File drop: skipped, XTEST is unavailable (set RUNIC_LIBXTST to libXtst.so.6).");
            return;
        }
        using var _ = pointer;
        var root = Directory.CreateTempSubdirectory("runic-drop-smoke-");
        try
        {
            var file = Path.Combine(root.FullName, "dropped notes.txt");
            await File.WriteAllTextAsync(file, "dropped");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "a folder")).FullName;
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 400, Height = 300, X = 220, Y = 20,
            });
            if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrop))
                throw new InvalidOperationException("The GTK 3 window did not advertise file drops.");
            var before = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(10));
            var dropped = new TaskCompletionSource<DesktopFileDropEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FilesDropped += (_, drop) => dropped.TrySetResult(drop);

            var uris = new[] { new Uri(file).AbsoluteUri, new Uri(folder).AbsoluteUri };
            nint source = 0;
            await window.InvokeNativeAsync(_ => source = DragSource.Create(uris, x: 10, y: 10, size: 150, actions: DragSource.Copy));
            try
            {
                await Task.Delay(500);
                await pointer.DragAsync((85, 85), (420, 170));
                var drop = await dropped.Task.WaitScaledAsync(TimeSpan.FromSeconds(15));
                if (!drop.Paths.SequenceEqual([file, folder]) || drop.Target != "folder-7")
                    throw new InvalidOperationException($"The drop reported [{string.Join(", ", drop.Paths)}] with target '{drop.Target}'.");
                var pageDrops = await surface.ExecuteJavaScriptAsync("return String(window.__pageDrops);");
                var after = await surface.ExecuteJavaScriptAsync("return location.href;");
                if (pageDrops != "1" || after != before)
                    throw new InvalidOperationException($"The page saw {pageDrops} drop events and moved from {before} to {after}.");
                Console.WriteLine($"File drop passed: native paths [{string.Join(", ", drop.Paths)}], target '{drop.Target}', one page drop event, no navigation.");
            }
            finally
            {
                if (source != 0) await window.InvokeNativeAsync(_ => DragSource.Destroy(source));
            }
            await window.CloseAsync();
            // The page window where FileDropSmoke's was; the test windows where its drag source was.
            await LinuxDragOutSmoke.RunAsync(host, pointer, [file, folder], new LinuxDragOutSmoke.Toolkit
            {
                Name = "GTK 3",
                PageWindow = new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 400, Height = 300, X = 220, Y = 20 },
                PageCenter = (420, 170),
                SameFrame = (320, 170),
                OtherFrame = (520, 170),
                CreateDropTarget = static () => DropTarget.Create(x: 10, y: 10, size: 150),
                DropPoint = (85, 85),
                CreateDragSource = static uris => DragSource.Create(uris, x: 10, y: 10, size: 150, actions: DragSource.Copy | DragSource.Move),
                SourcePoint = (85, 85),
                DestroyWindow = static window => DragSource.Destroy(window),
            });
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // A GTK 3 window that offers text/uri-list when dragged.
    private static unsafe partial class DragSource
    {
        internal const int Copy = 1 << 1;
        internal const int Move = 1 << 2;
        private static nint[] s_uris = [];

        internal static nint Create(string[] uris, int x, int y, int size, int actions)
        {
            foreach (var pointer in s_uris) Marshal.FreeCoTaskMem(pointer);
            s_uris = [.. uris.Select(Marshal.StringToCoTaskMemUTF8), 0];
            var window = WindowNew(0);
            WindowSetDefaultSize(window, size, size);
            WindowMove(window, x, y);
            var target = Marshal.StringToCoTaskMemUTF8("text/uri-list");
            var entry = new TargetEntry { Target = target, Flags = 0, Info = 0 };
            DragSourceSet(window, 1 << 8, &entry, 1, actions); // GDK_BUTTON1_MASK
            Marshal.FreeCoTaskMem(target);
            SignalConnectData(window, "drag-data-get",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void>)&OnDragDataGet, 0, 0, 0);
            WidgetShowAll(window);
            return window;
        }

        internal static void Destroy(nint window)
        {
            if (window != 0) WidgetDestroy(window);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnDragDataGet(nint widget, nint context, nint data, uint info, uint time, nint userData)
        {
            fixed (nint* uris = s_uris) _ = SelectionDataSetUris(data, uris);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetEntry
        {
            internal nint Target;
            internal uint Flags;
            internal uint Info;
        }

        private const string Gtk = "libgtk-3.so.0";
        [LibraryImport(Gtk, EntryPoint = "gtk_window_new")] private static partial nint WindowNew(int type);
        [LibraryImport(Gtk, EntryPoint = "gtk_window_set_default_size")] private static partial void WindowSetDefaultSize(nint window, int width, int height);
        [LibraryImport(Gtk, EntryPoint = "gtk_window_move")] private static partial void WindowMove(nint window, int x, int y);
        [LibraryImport(Gtk, EntryPoint = "gtk_drag_source_set")] private static partial void DragSourceSet(nint widget, int mask, TargetEntry* targets, int count, int actions);
        [LibraryImport(Gtk, EntryPoint = "gtk_selection_data_set_uris")] private static partial int SelectionDataSetUris(nint data, nint* uris);
        [LibraryImport(Gtk, EntryPoint = "gtk_widget_show_all")] private static partial void WidgetShowAll(nint widget);
        [LibraryImport(Gtk, EntryPoint = "gtk_widget_destroy")] private static partial void WidgetDestroy(nint widget);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    }

    // A GTK 3 window that accepts text/uri-list with Copy or Move (GTK_DEST_DEFAULT_ALL answers, reads and finishes the
    // drop) and records the dropped URIs. It notes the drag's first motion, leave, drop and data as stages (I-214).
    private static unsafe partial class DropTarget
    {
        // GTK thread only: motions since the last leave or drop.
        private static int s_motions;

        internal static nint Create(int x, int y, int size)
        {
            var window = WindowNew(0);
            WindowSetDefaultSize(window, size, size);
            WindowMove(window, x, y);
            var target = Marshal.StringToCoTaskMemUTF8("text/uri-list");
            var entry = new TargetEntry { Target = target, Flags = 0, Info = 0 };
            DragDestSet(window, 7, &entry, 1, DragSource.Copy | DragSource.Move); // GTK_DEST_DEFAULT_ALL
            Marshal.FreeCoTaskMem(target);
            SignalConnectData(window, "drag-data-received",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, nint, uint, uint, nint, void>)&OnDragDataReceived, 0, 0, 0);
            SignalConnectData(window, "drag-motion",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, uint, nint, int>)&OnDragMotion, 0, 0, 0);
            SignalConnectData(window, "drag-leave",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, void>)&OnDragLeave, 0, 0, 0);
            SignalConnectData(window, "drag-drop",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, int, uint, nint, int>)&OnDragDrop, 0, 0, 0);
            WidgetShowAll(window);
            return window;
        }

        // GTK_DEST_DEFAULT_MOTION and _DROP answer the drag; these handlers only observe it.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnDragMotion(nint widget, nint context, int x, int y, uint time, nint userData)
        {
            if (s_motions++ == 0) LinuxDragOutSmoke.Stage($"the drop target saw the drag at ({x}, {y}), action {SelectedAction(context)}");
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnDragLeave(nint widget, nint context, uint time, nint userData)
        {
            LinuxDragOutSmoke.Stage($"the drag left the drop target after {s_motions} motions");
            s_motions = 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int OnDragDrop(nint widget, nint context, int x, int y, uint time, nint userData)
        {
            LinuxDragOutSmoke.Stage($"the drag dropped on the drop target after {s_motions} motions, action {SelectedAction(context)}");
            s_motions = 0;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void OnDragDataReceived(nint widget, nint context, int x, int y, nint data, uint info, uint time, nint userData)
        {
            var uris = SelectionDataGetUris(data);
            if (uris == null)
            {
                LinuxDragOutSmoke.Stage($"the drop target received no URIs ({SelectionDataGetLength(data)} bytes)");
                return;
            }
            List<string> values = [];
            for (var entry = uris; *entry != 0; entry++) values.Add(Marshal.PtrToStringUTF8(*entry)!);
            StrFreev(uris);
            LinuxDragOutSmoke.RecordDrop([.. values]);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetEntry
        {
            internal nint Target;
            internal uint Flags;
            internal uint Info;
        }

        private const string Gtk = "libgtk-3.so.0";
        [LibraryImport(Gtk, EntryPoint = "gtk_window_new")] private static partial nint WindowNew(int type);
        [LibraryImport(Gtk, EntryPoint = "gtk_window_set_default_size")] private static partial void WindowSetDefaultSize(nint window, int width, int height);
        [LibraryImport(Gtk, EntryPoint = "gtk_window_move")] private static partial void WindowMove(nint window, int x, int y);
        [LibraryImport(Gtk, EntryPoint = "gtk_drag_dest_set")] private static partial void DragDestSet(nint widget, int flags, TargetEntry* targets, int count, int actions);
        [LibraryImport(Gtk, EntryPoint = "gtk_selection_data_get_uris")] private static partial nint* SelectionDataGetUris(nint data);
        [LibraryImport(Gtk, EntryPoint = "gtk_selection_data_get_length")] private static partial int SelectionDataGetLength(nint data);
        [LibraryImport(Gtk, EntryPoint = "gdk_drag_context_get_selected_action")] private static partial int SelectedAction(nint context);
        [LibraryImport(Gtk, EntryPoint = "gtk_widget_show_all")] private static partial void WidgetShowAll(nint widget);
        [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_strfreev")] private static partial void StrFreev(nint* values);
        [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nuint SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    }
}


// W315-006 on Windows: an in-process OLE drag of real shell files (the data object Explorer would offer) onto the
// WebView2 window, driven with SendInput on the runner's desktop.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static partial class WindowsFileDropSmoke
{
    internal static async Task RunAsync(DesktopHost host)
    {
        var root = Directory.CreateTempSubdirectory("runic-drop-smoke-");
        try
        {
            var file = Path.Combine(root.FullName, "dropped notes.txt");
            await File.WriteAllTextAsync(file, "dropped");
            var folder = Directory.CreateDirectory(Path.Combine(root.FullName, "a folder")).FullName;
            await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(FileDropSmoke.Page) });
            await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded, Width = 480, Height = 360, X = 300, Y = 200,
            });
            if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.FileDrop))
                throw new InvalidOperationException("The WebView2 window did not advertise file drops.");
            await window.FocusAsync();
            var before = await surface.ExecuteJavaScriptAsync("return location.href;", TimeSpan.FromSeconds(10));
            var dropped = new TaskCompletionSource<DesktopFileDropEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FilesDropped += (_, drop) => dropped.TrySetResult(drop);
            var handle = window.NativeHandle;
            var drag = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { drag.TrySetResult(Drag(handle, [file, folder], null)); }
                catch (Exception error) { drag.TrySetException(error); }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            int effect;
            try
            {
                effect = await drag.Task.WaitScaledAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                // End a stuck drag loop, so the window and the process can close.
                Escape();
                // On the arm64 runner the drag starts but its modal loop never sees the release (#391).
                if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    Console.WriteLine("File drop: unverified on Windows arm64: the OLE drag did not end within 30 seconds (#391).");
                    return;
                }
                throw new TimeoutException("The OLE drag did not end within 30 seconds.");
            }
            if (effect == NoDesktopInput)
            {
                Console.WriteLine("File drop: unverified on this runner: its desktop does not deliver SendInput to a window of this process.");
                return;
            }
            Console.WriteLine($"File drop: OLE drag ended with effect {effect}.");
            var drop = await dropped.Task.WaitScaledAsync(TimeSpan.FromSeconds(15));
            if (!drop.Paths.SequenceEqual([file, folder], StringComparer.OrdinalIgnoreCase) || drop.Target != "folder-7")
                throw new InvalidOperationException($"The drop reported [{string.Join(", ", drop.Paths)}] with target '{drop.Target}'.");
            var pageDrops = await surface.ExecuteJavaScriptAsync("return String(window.__pageDrops);");
            var after = await surface.ExecuteJavaScriptAsync("return location.href;");
            if (pageDrops != "1" || after != before)
                throw new InvalidOperationException($"The page saw {pageDrops} drop events and moved from {before} to {after}.");
            Console.WriteLine($"File drop passed: native paths [{string.Join(", ", drop.Paths)}], target '{drop.Target}', one page drop event, no navigation.");
            await DragOutAsync(host, [file, folder]);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task DragOutAsync(DesktopHost host, string[] paths)
    {
        // win-arm64: the OLE drag loop does not start on the runner (I-077, I-103).
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Console.WriteLine("Drag out and frame drops: unverified on Windows arm64, whose runner does not run the OLE drag loop (I-077).");
            return;
        }
        if (!await RunDragOutAsync(host, paths)) return;
        await RunFrameDropsAsync(host, paths);
    }

    // STA thread: the button goes down outside the window, DoDragDrop runs its modal loop, and another thread moves
    // the pointer over the WebView and releases it.
    private const int NoDesktopInput = -1;

    // STA thread: as in a real drag source, the button goes down on a window of this thread, so its input state
    // holds the pressed button and DoDragDrop's modal loop can track it; another thread moves the pointer over the
    // WebView and releases it.
    private static unsafe int Drag(nint window, string[] paths, (int X, int Y)? clientTarget)
    {
        _ = SetThreadDpiAwarenessContext(-4);
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        nint helper = 0;
        try
        {
            var dataObject = CreateDataObject(paths);
            var source = new DropSource();
            var sourcePointer = (nint)System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IDropSource>.ConvertToUnmanaged(source);
            try
            {
                if (!GetWindowRect(window, out var bounds)) throw new InvalidOperationException("GetWindowRect failed.");
                var start = (X: bounds.Left - 60, Y: bounds.Top + 40);
                var target = clientTarget is { } point
                    ? ClientToScreenPoint(window, point)
                    : (X: (bounds.Left + bounds.Right) / 2, Y: (bounds.Top + bounds.Bottom) / 2 + 20);
                helper = CreateWindowEx(0x00000008 | 0x00000080, "STATIC", "Runic drag source", 0x80000000 | 0x10000000, // TOPMOST|TOOLWINDOW, POPUP|VISIBLE
                    start.X - 20, start.Y - 20, 40, 40, 0, 0, 0, 0);
                _ = SetForegroundWindow(helper);
                Pump(TimeSpan.FromMilliseconds(300));
                Mouse(start.X, start.Y, MouseMove);
                Pump(TimeSpan.FromMilliseconds(200));
                Mouse(start.X, start.Y, MouseLeftDown);
                var pressed = System.Diagnostics.Stopwatch.StartNew();
                while (GetKeyState(0x01) >= 0 && pressed.Elapsed < TestTimeouts.Scale(TimeSpan.FromSeconds(3))) Pump(TimeSpan.FromMilliseconds(20)); // VK_LBUTTON
                if (GetKeyState(0x01) >= 0)
                {
                    Mouse(start.X, start.Y, MouseLeftUp);
                    return NoDesktopInput;
                }
                var mover = Task.Run(() => MoveAndReleaseAsync(start, target));
                var hr = DoDragDrop(dataObject, sourcePointer, clientTarget is null ? 1 : 3, out var effect); // DROPEFFECT_COPY, or COPY | MOVE onto a frame
                mover.GetAwaiter().GetResult();
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                return effect;
            }
            finally
            {
                System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IDropSource>.Free((void*)sourcePointer);
                Release(dataObject);
            }
        }
        finally
        {
            if (helper != 0) _ = DestroyWindow(helper);
            OleUninitialize();
        }
    }

    private static unsafe void Pump(TimeSpan duration)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var message = stackalloc byte[64];
        while (elapsed.Elapsed < duration)
        {
            while (PeekMessage(message, 0, 0, 0, 1) != 0) // PM_REMOVE
            {
                _ = TranslateMessage(message);
                _ = DispatchMessage(message);
            }
            Thread.Sleep(5);
        }
    }

    private static unsafe void Escape()
    {
        var inputs = stackalloc Input[2];
        inputs[0] = new Input { Type = 1 };
        inputs[1] = new Input { Type = 1 };
        // A keyboard input shares the mouse field's offset: wVk = VK_ESCAPE, then KEYEVENTF_KEYUP.
        ((ushort*)&inputs[0].Mouse)[0] = 0x1B;
        ((ushort*)&inputs[1].Mouse)[0] = 0x1B;
        ((uint*)&inputs[1].Mouse)[1] = 0x0002;
        _ = SendInput(2, inputs, sizeof(Input));
    }

    private static async Task MoveAndReleaseAsync((int X, int Y) start, (int X, int Y) target)
    {
        await Task.Delay(300);
        for (var step = 1; step <= 30; step++)
        {
            Mouse(start.X + (target.X - start.X) * step / 30, start.Y + (target.Y - start.Y) * step / 30, MouseMove);
            await Task.Delay(30);
        }
        for (var wiggle = 0; wiggle < 10; wiggle++)
        {
            Mouse(target.X + wiggle % 2, target.Y, MouseMove);
            await Task.Delay(50);
        }
        await Task.Delay(500);
        Mouse(target.X, target.Y, MouseLeftUp);
    }

    // The shell's own data object for the files, with CF_HDROP and the shell formats.
    private static unsafe nint CreateDataObject(string[] paths)
    {
        var pidls = new nint[paths.Length];
        try
        {
            for (var index = 0; index < paths.Length; index++)
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index], 0, out pidls[index], 0, out _));
            nint array;
            fixed (nint* items = pidls) Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, items, out array));
            try
            {
                var handler = new Guid("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4"); // BHID_DataObject
                var iid = new Guid("0000010e-0000-0000-C000-000000000046"); // IID_IDataObject
                nint dataObject;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, Guid*, nint*, int>)(*(nint**)array)[3])(
                    array, 0, &handler, &iid, &dataObject));
                return dataObject;
            }
            finally
            {
                Release(array);
            }
        }
        finally
        {
            foreach (var pidl in pidls) if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }

    private static unsafe void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);
    }

    private const uint MouseMove = 0x0001 | 0x8000; // MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
    private const uint MouseLeftDown = 0x0002 | 0x8000;
    private const uint MouseLeftUp = 0x0004 | 0x8000;

    private static unsafe void Mouse(int x, int y, uint flags)
    {
        // Absolute input spans the virtual desktop with MOUSEEVENTF_VIRTUALDESK.
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = Math.Max(1, GetSystemMetrics(78) - 1);
        var height = Math.Max(1, GetSystemMetrics(79) - 1);
        var input = new Input
        {
            Type = 0,
            Mouse = new MouseInput
            {
                X = (int)((x - left) * 65535L / width),
                Y = (int)((y - top) * 65535L / height),
                Flags = flags | 0x4000,
            },
        };
        if (SendInput(1, &input, sizeof(Input)) != 1)
            throw new InvalidOperationException($"SendInput was refused ({Marshal.GetLastPInvokeError()}); the runner has no interactive desktop.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        internal int X;
        internal int Y;
        internal uint Data;
        internal uint Flags;
        internal uint Time;
        internal nuint Extra;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [System.Runtime.InteropServices.Marshalling.GeneratedComInterface, Guid("00000121-0000-0000-C000-000000000046")]
    internal partial interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    [System.Runtime.InteropServices.Marshalling.GeneratedComClass]
    internal sealed partial class DropSource : IDropSource
    {
        public int QueryContinueDrag(int escapePressed, uint keyState) =>
            escapePressed != 0 ? 0x00040101 : (keyState & 1) == 0 ? 0x00040100 : 0; // CANCEL, DROP, S_OK
        public int GiveFeedback(uint effect) => 0x00040102; // DRAGDROP_S_USEDEFAULTCURSORS
    }

    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] private static partial void OleUninitialize();
    [LibraryImport("ole32.dll")] private static partial int DoDragDrop(nint dataObject, nint dropSource, int okEffects, out int effect);
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributes, out uint result);
    [LibraryImport("shell32.dll")] private static unsafe partial int SHCreateShellItemArrayFromIDLists(uint count, nint* pidls, out nint array);
    [LibraryImport("user32.dll", SetLastError = true)] private static unsafe partial uint SendInput(uint count, Input* inputs, int size);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect bounds);
    [LibraryImport("user32.dll")] private static partial nint SetThreadDpiAwarenessContext(nint context);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")] private static partial short GetKeyState(int key);
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] private static unsafe partial int PeekMessage(byte* message, nint window, uint minimum, uint maximum, uint remove);
    [LibraryImport("user32.dll")] private static unsafe partial int TranslateMessage(byte* message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] private static unsafe partial nint DispatchMessage(byte* message);
}
