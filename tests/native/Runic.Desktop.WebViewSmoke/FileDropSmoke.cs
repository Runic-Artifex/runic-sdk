using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Desktop;

// W315-006: a real X11 drag and drop from a GTK 3 drag source onto the GTK 3 WebKitGTK window, driven through XTEST
// under Xvfb. The window reports the native paths with the page's routing label, the page still receives its drop
// event, and the window does not navigate to the dropped file.
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
            await window.DispatchNativeAsync(_ => source = DragSource.Create(uris, x: 10, y: 10, size: 150));
            try
            {
                await Task.Delay(500);
                await pointer.DragAsync((85, 85), (420, 170));
                var drop = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(15));
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
                if (source != 0) await window.DispatchNativeAsync(_ => DragSource.Destroy(source));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // A GTK 3 window that offers text/uri-list when dragged.
    private static unsafe partial class DragSource
    {
        private static nint[] s_uris = [];

        internal static nint Create(string[] uris, int x, int y, int size)
        {
            foreach (var pointer in s_uris) Marshal.FreeCoTaskMem(pointer);
            s_uris = [.. uris.Select(Marshal.StringToCoTaskMemUTF8), 0];
            var window = WindowNew(0);
            WindowSetDefaultSize(window, size, size);
            WindowMove(window, x, y);
            var target = Marshal.StringToCoTaskMemUTF8("text/uri-list");
            var entry = new TargetEntry { Target = target, Flags = 0, Info = 0 };
            DragSourceSet(window, 1 << 8, &entry, 1, 1 << 1); // GDK_BUTTON1_MASK, GDK_ACTION_COPY
            Marshal.FreeCoTaskMem(target);
            SignalConnectData(window, "drag-data-get",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void>)&OnDragDataGet, 0, 0, 0);
            WidgetShowAll(window);
            return window;
        }

        internal static void Destroy(nint window) => WidgetDestroy(window);

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
                try { drag.TrySetResult(Drag(handle, [file, folder])); }
                catch (Exception error) { drag.TrySetException(error); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            var effect = await drag.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"File drop: OLE drag ended with effect {effect}.");
            var drop = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (!drop.Paths.SequenceEqual([file, folder], StringComparer.OrdinalIgnoreCase) || drop.Target != "folder-7")
                throw new InvalidOperationException($"The drop reported [{string.Join(", ", drop.Paths)}] with target '{drop.Target}'.");
            var pageDrops = await surface.ExecuteJavaScriptAsync("return String(window.__pageDrops);");
            var after = await surface.ExecuteJavaScriptAsync("return location.href;");
            if (pageDrops != "1" || after != before)
                throw new InvalidOperationException($"The page saw {pageDrops} drop events and moved from {before} to {after}.");
            Console.WriteLine($"File drop passed: native paths [{string.Join(", ", drop.Paths)}], target '{drop.Target}', one page drop event, no navigation.");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // STA thread: the button goes down outside the window, DoDragDrop runs its modal loop, and another thread moves
    // the pointer over the WebView and releases it.
    private static unsafe int Drag(nint window, string[] paths)
    {
        _ = SetThreadDpiAwarenessContext(-4);
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try
        {
            var dataObject = CreateDataObject(paths);
            var source = new DropSource();
            var sourcePointer = (nint)System.Runtime.InteropServices.Marshalling.ComInterfaceMarshaller<IDropSource>.ConvertToUnmanaged(source);
            try
            {
                if (!GetWindowRect(window, out var bounds)) throw new InvalidOperationException("GetWindowRect failed.");
                var start = (X: bounds.Left - 60, Y: bounds.Top + 40);
                var target = (X: (bounds.Left + bounds.Right) / 2, Y: (bounds.Top + bounds.Bottom) / 2 + 20);
                Mouse(start.X, start.Y, MouseMove);
                Mouse(start.X, start.Y, MouseLeftDown);
                var mover = Task.Run(() => MoveAndReleaseAsync(start, target));
                var hr = DoDragDrop(dataObject, sourcePointer, 1, out var effect); // DROPEFFECT_COPY
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
            OleUninitialize();
        }
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
}
