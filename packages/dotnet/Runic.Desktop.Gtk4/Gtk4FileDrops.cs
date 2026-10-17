using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Gtk4;

// WebKitGTK 6 receives drops through its own GtkDropTargetAsync on the view, which claims them: a second drop target
// would take the drop from WebKit, and the page would see no drop event. WebKit's handlers of "accept" and "drop"
// return TRUE, which ends the emission, so a handler of the host would never run. The host instead adds emission
// hooks, which GLib runs before any handler, and only acts for WebKit's target on a host's view. When the drag is
// accepted it starts reading the drop's GdkFileList, as WebKit starts reading its own data then, long before the drop
// finishes; when the drop happens, the read's paths are delivered.
[SupportedOSPlatform("linux")]
internal static unsafe partial class Gtk4FileDrops
{
    private const string Gtk = "libgtk-4.so.1";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    // GTK thread only: WebKit's drop target of each open view, and its drop.
    private static readonly Dictionary<nint, Session> Sessions = [];
    private static bool s_hooked;

    // Finds WebKit's drop target on the view; false when the view has none, so the window reports no FileDrop.
    internal static bool Connect(Gtk4WindowHost host, WebKit.WebView webView)
    {
        var targetType = DropTargetAsyncGetType();
        if (!InstallHooks(targetType)) return false;
        var controllers = WidgetObserveControllers(webView.Handle.DangerousGetHandle());
        if (controllers == 0) return false;
        try
        {
            var count = ListModelGetNItems(controllers);
            for (uint index = 0; index < count; index++)
            {
                var controller = ListModelGetItem(controllers, index);
                if (controller == 0) continue;
                try
                {
                    if (TypeCheckInstanceIsA(controller, targetType) == 0) continue;
                    Sessions[controller] = new Session(host, controller);
                    return true;
                }
                finally
                {
                    ObjectUnref(controller);
                }
            }
            return false;
        }
        finally
        {
            ObjectUnref(controllers);
        }
    }

    // When the window closes; the controller goes with the view.
    internal static void Disconnect(Gtk4WindowHost host)
    {
        foreach (var (controller, session) in Sessions.Where(entry => ReferenceEquals(entry.Value.Host, host)).ToArray())
        {
            Forget(session);
            Sessions.Remove(controller);
        }
    }

    private static bool InstallHooks(nuint targetType)
    {
        if (s_hooked) return true;
        var accept = SignalLookup("accept", targetType);
        var drop = SignalLookup("drop", targetType);
        var leave = SignalLookup("drag-leave", targetType);
        if (accept == 0 || drop == 0 || leave == 0) return false;
        var hook = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, nint, int>)&OnEmission;
        SignalAddEmissionHook(accept, 0, hook, 1, 0);
        SignalAddEmissionHook(drop, 0, hook, 2, 0);
        SignalAddEmissionHook(leave, 0, hook, 3, 0);
        s_hooked = true;
        return true;
    }

    // A GValue is a GType and two 64-bit words.
    private static nint Parameter(nint values, int index) => values + index * (sizeof(nuint) + 16);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int OnEmission(nint hint, uint count, nint values, nint kind)
    {
        try
        {
            if (count < 2) return 1;
            var controller = ValueGetObject(Parameter(values, 0));
            if (!Sessions.TryGetValue(controller, out var session)) return 1;
            var drop = ValueGetObject(Parameter(values, 1));
            switch (kind)
            {
                case 1:
                    Accept(session, drop);
                    break;
                case 2:
                    if (drop == session.Drop)
                    {
                        session.Dropped = true;
                        if (!session.Reading) Deliver(session);
                    }
                    break;
                case 3:
                    // GTK also leaves after a drop; a dropped drop is delivered when its read completes.
                    if (!session.Dropped && drop == session.Drop) Forget(session);
                    break;
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        // Stay installed.
        return 1;
    }

    private static void Accept(Session session, nint drop)
    {
        Forget(session);
        // A drop from another application lists only MIME types; GDK deserializes text/uri-list and the portal's
        // file transfer into a GdkFileList, and a drop without files fails the read.
        if (drop == 0) return;
        session.Drop = ObjectRef(drop);
        session.Reading = true;
        DropReadValueAsync(drop, FileListGetType(), 0, 0,
            (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnRead, GCHandle.ToIntPtr(GCHandle.Alloc(session)));
    }

    // GTK thread only: the drop being read and whether it was dropped.
    private sealed class Session(Gtk4WindowHost host, nint controller)
    {
        internal Gtk4WindowHost Host { get; } = host;
        internal nint Controller { get; } = controller;
        internal nint Drop;
        internal bool Dropped;
        internal List<string>? Paths;
        internal bool Reading;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnRead(nint source, nint result, nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        try
        {
            if (handle.Target is not Session session || !Sessions.ContainsKey(session.Controller)) return;
            nint error = 0;
            var value = DropReadValueFinish(source, result, &error);
            if (error != 0) ErrorFree(error);
            if (source != session.Drop) return;
            session.Reading = false;
            session.Paths = value == 0 ? [] : ReadFileList(ValueGetBoxed(value));
            if (session.Dropped) Deliver(session);
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        finally
        {
            handle.Free();
        }
    }

    private static List<string> ReadFileList(nint fileList)
    {
        List<string> paths = [];
        if (fileList == 0) return paths;
        var files = FileListGetFiles(fileList);
        try
        {
            for (var item = files; item != 0; item = ((nint*)item)[1])
            {
                var path = FileGetPath(((nint*)item)[0]);
                if (path == 0) continue;
                try
                {
                    var value = Marshal.PtrToStringUTF8(path);
                    if (value is { Length: > 0 } && Path.IsPathFullyQualified(value) && !paths.Contains(value, StringComparer.Ordinal))
                        paths.Add(value);
                }
                finally
                {
                    Free(path);
                }
            }
        }
        finally
        {
            SListFree(files);
        }
        return paths;
    }

    private static void Deliver(Session session)
    {
        var paths = session.Paths;
        Forget(session);
        if (paths is { Count: > 0 }) session.Host.NativeFileDrop(paths);
    }

    private static void Forget(Session session)
    {
        if (session.Drop != 0) ObjectUnref(session.Drop);
        session.Drop = 0;
        session.Dropped = false;
        session.Reading = false;
        session.Paths = null;
    }

    [LibraryImport(GObject, EntryPoint = "g_signal_lookup", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint SignalLookup(string name, nuint type);
    [LibraryImport(GObject, EntryPoint = "g_signal_add_emission_hook")]
    private static partial nuint SignalAddEmissionHook(uint signal, uint detail, nint hook, nint data, nint destroy);
    [LibraryImport(GObject, EntryPoint = "g_value_get_object")]
    private static partial nint ValueGetObject(nint value);
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_observe_controllers")]
    private static partial nint WidgetObserveControllers(nint widget);
    [LibraryImport(Gtk, EntryPoint = "gtk_drop_target_async_get_type")]
    private static partial nuint DropTargetAsyncGetType();
    [LibraryImport(Gio, EntryPoint = "g_list_model_get_n_items")]
    private static partial uint ListModelGetNItems(nint model);
    [LibraryImport(Gio, EntryPoint = "g_list_model_get_item")]
    private static partial nint ListModelGetItem(nint model, uint position);
    [LibraryImport(GObject, EntryPoint = "g_type_check_instance_is_a")]
    private static partial int TypeCheckInstanceIsA(nint instance, nuint type);
    [LibraryImport(GObject, EntryPoint = "g_object_ref")]
    private static partial nint ObjectRef(nint instance);
    [LibraryImport(GObject, EntryPoint = "g_object_unref")]
    private static partial void ObjectUnref(nint instance);
    [LibraryImport(GObject, EntryPoint = "g_value_get_boxed")]
    private static partial nint ValueGetBoxed(nint value);
    [LibraryImport(Gtk, EntryPoint = "gdk_file_list_get_type")]
    private static partial nuint FileListGetType();
    [LibraryImport(Gtk, EntryPoint = "gdk_file_list_get_files")]
    private static partial nint FileListGetFiles(nint fileList);
    [LibraryImport(Gtk, EntryPoint = "gdk_drop_read_value_async")]
    private static partial void DropReadValueAsync(nint drop, nuint type, int priority, nint cancellable, nint callback, nint data);
    [LibraryImport(Gtk, EntryPoint = "gdk_drop_read_value_finish")]
    private static partial nint DropReadValueFinish(nint drop, nint result, nint* error);
    [LibraryImport(Gio, EntryPoint = "g_file_get_path")]
    private static partial nint FileGetPath(nint file);
    [LibraryImport(GLib, EntryPoint = "g_slist_free")]
    private static partial void SListFree(nint list);
    [LibraryImport(GLib, EntryPoint = "g_free")]
    private static partial void Free(nint memory);
    [LibraryImport(GLib, EntryPoint = "g_error_free")]
    private static partial void ErrorFree(nint error);
}
