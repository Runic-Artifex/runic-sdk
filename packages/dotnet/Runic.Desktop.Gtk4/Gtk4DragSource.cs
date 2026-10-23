using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Desktop.Gtk4;

// B11: drags out of the GTK 4 window. gdk_drag_begin on the window's surface with the default seat's pointer while it
// holds the primary button, as GtkDragSource does: the content is a union of a GdkFileList of the paths (GDK offers it
// as text/uri-list and through the FileTransfer portal), the plain text (G_TYPE_STRING: text/plain;charset=utf-8 and
// UTF8_STRING) and, for a drag of URIs without paths, text/uri-list bytes. The GdkDrag's dnd-finished gives the selected
// action, cancel gives None.
[SupportedOSPlatform("linux")]
internal sealed partial class Gtk4WindowHost : IDesktopDragSourceWindowHost
{
    // GTK thread only: the running drag out.
    private Gtk4DragOut? _dragOut;

    public ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsOpen) return ValueTask.FromException<DesktopDragEffects>(new InvalidOperationException("The GTK 4 window is not open."));
        cancellationToken.ThrowIfCancellationRequested();
        var drag = new Gtk4DragOut(data);
        var started = Gtk4Dispatcher.Instance.InvokeAsync(() => BeginDragOut(drag), cancellationToken);
        return new ValueTask<DesktopDragEffects>(WaitForDragOutAsync(drag, started));
    }

    private static async Task<DesktopDragEffects> WaitForDragOutAsync(Gtk4DragOut drag, Task started)
    {
        await started.ConfigureAwait(false);
        return await drag.Result.Task.ConfigureAwait(false);
    }

    // GTK thread. None at once when no primary button is held, another drag out runs, or GDK starts no drag.
    private unsafe void BeginDragOut(Gtk4DragOut drag)
    {
        if (_dragOut is not null || !IsOpen || _window is not { } window)
        {
            drag.Result.TrySetResult(DesktopDragEffects.None);
            return;
        }
        var surface = SurfaceOf(window);
        var pointer = surface == 0 ? 0 : Gtk4DragApi.DefaultPointer(surface);
        if (pointer == 0 || !Gtk4DragApi.IsPrimaryButtonHeld(surface, pointer))
        {
            drag.Result.TrySetResult(DesktopDragEffects.None);
            return;
        }
        var content = Gtk4DragApi.CreateContent(drag.Data);
        nint native;
        try
        {
            native = Gtk4DragApi.DragBegin(surface, pointer, content, GtkDragSupport.ToGtk4Actions(drag.Data.AllowedEffects), 0, 0);
        }
        finally
        {
            Gtk4DragApi.ObjectUnref(content);
        }
        if (native == 0)
        {
            drag.Result.TrySetResult(DesktopDragEffects.None);
            return;
        }
        // As GtkDragSource does after gdk_drag_begin (gtk_widget_reset_controllers): the drag now owns the pointer, so the
        // press that started it ends in no release the view's gestures see. A gesture keeps a reference to the target of
        // each of its points until the sequence ends, so on GTK 4.12 to 4.20 under X11 WebKit's gestures held the view,
        // and closing the window waited for its finalization forever (I-197).
        if (_webView is { } view) Gtk4DragApi.ResetGestures(view.Handle.DangerousGetHandle(), window.Handle.DangerousGetHandle());
        drag.Drag = native;
        drag.Owner = this;
        drag.Handle = GCHandle.Alloc(drag);
        var context = GCHandle.ToIntPtr(drag.Handle);
        drag.FinishedHandler = Gtk4DragApi.Connect(native, "dnd-finished", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDndFinished, context);
        drag.CancelHandler = Gtk4DragApi.Connect(native, "cancel", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnCancel, context);
        _dragOut = drag;
        Gtk4DragApi.SetIcon(native);
    }

    // GTK thread. Ends GDK's side of the drag, releases it and completes the call; repeated calls do nothing.
    private void FinishDragOut(Gtk4DragOut drag, DesktopDragEffects effect, bool success)
    {
        if (drag.Drag == 0) return;
        if (ReferenceEquals(_dragOut, drag)) _dragOut = null;
        var native = drag.Drag;
        drag.Drag = 0;
        Gtk4DragApi.Disconnect(native, drag.FinishedHandler);
        Gtk4DragApi.Disconnect(native, drag.CancelHandler);
        Gtk4DragApi.DragDropDone(native, success);
        Gtk4DragApi.ObjectUnref(native);
        if (drag.Handle.IsAllocated) drag.Handle.Free();
        drag.Result.TrySetResult(effect);
    }

    // GTK thread, when the window's native objects are released: a running drag out completes with None.
    private void ReleaseDragOut()
    {
        if (_dragOut is { } drag) FinishDragOut(drag, DesktopDragEffects.None, success: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDndFinished(nint native, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not Gtk4DragOut { Owner: { } host } drag || drag.Drag != native) return;
            var effect = DesktopDragRules.Single(GtkDragSupport.FromGtk4Actions(Gtk4DragApi.SelectedAction(native)), drag.Data.AllowedEffects);
            host.FinishDragOut(drag, effect, success: true);
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // Escape, a drop nowhere or a refused drop.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancel(nint native, int reason, nint context)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is Gtk4DragOut { Owner: { } host } drag && drag.Drag == native)
                host.FinishDragOut(drag, DesktopDragEffects.None, success: false);
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    private sealed class Gtk4DragOut(DesktopDragData data)
    {
        internal DesktopDragData Data { get; } = data;
        internal TaskCompletionSource<DesktopDragEffects> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // GTK thread only.
        internal Gtk4WindowHost? Owner { get; set; }
        internal nint Drag { get; set; }
        internal GCHandle Handle { get; set; }
        internal nuint FinishedHandler { get; set; }
        internal nuint CancelHandler { get; set; }
    }
}

// The GDK 4 drag source functions GirCore leaves to the caller (GTK 4.12 has every one).
[SupportedOSPlatform("linux")]
internal static unsafe partial class Gtk4DragApi
{
    private const string Gtk = "libgtk-4.so.1";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0";
    // G_TYPE_STRING: the fundamental type 16, shifted by G_TYPE_FUNDAMENTAL_SHIFT.
    private const nuint StringType = 16 << 2;
    // GTK_ICON_SIZE_LARGE.
    private const int LargeIcon = 2;
    // A GValue is a GType and two 64-bit words.
    private const int ValueSize = 24;

    internal static nint DefaultPointer(nint surface)
    {
        var display = SurfaceGetDisplay(surface);
        var seat = display == 0 ? 0 : DisplayGetDefaultSeat(display);
        return seat == 0 ? 0 : SeatGetPointer(seat);
    }

    // The modifier state GDK reads for the pointer, also when it is outside the surface.
    internal static bool IsPrimaryButtonHeld(nint surface, nint pointer)
    {
        double x, y;
        uint mask = 0;
        _ = SurfaceGetDevicePosition(surface, pointer, &x, &y, &mask);
        return (mask & GtkDragSupport.Button1Mask) != 0;
    }

    // A new GdkContentProvider (transfer full) for the drag's data.
    internal static nint CreateContent(DesktopDragData data)
    {
        List<nint> providers = [];
        try
        {
            if (!data.Paths.IsDefaultOrEmpty) providers.Add(FileListProvider(data.Paths));
            else if (!data.Uris.IsDefaultOrEmpty) providers.Add(BytesProvider("text/uri-list",
                string.Concat(data.Uris.Select(static uri => uri.AbsoluteUri + "\r\n"))));
            if (GtkDragSupport.PlainText(data) is { } text) providers.Add(StringProvider(text));
            if (providers.Count == 1) return providers[0];
            fixed (nint* array = providers.ToArray())
            {
                // The union takes the providers.
                var union = ContentProviderNewUnion(array, (nuint)providers.Count);
                providers.Clear();
                return union;
            }
        }
        catch
        {
            foreach (var provider in providers) ObjectUnref(provider);
            throw;
        }
    }

    private static nint FileListProvider(IReadOnlyList<string> paths)
    {
        var files = new nint[paths.Count];
        var value = stackalloc byte[ValueSize];
        new Span<byte>(value, ValueSize).Clear();
        try
        {
            for (var index = 0; index < files.Length; index++) files[index] = FileNewForPath(paths[index]);
            nint list;
            fixed (nint* array = files) list = FileListNewFromArray(array, (nuint)files.Length);
            ValueInit(value, FileListGetType());
            ValueTakeBoxed(value, list);
            return ContentProviderNewForValue(value);
        }
        finally
        {
            if (*(nuint*)value != 0) ValueUnset(value);
            foreach (var file in files) if (file != 0) ObjectUnref(file);
        }
    }

    private static nint StringProvider(string text)
    {
        var value = stackalloc byte[ValueSize];
        new Span<byte>(value, ValueSize).Clear();
        ValueInit(value, StringType);
        try
        {
            ValueSetString(value, text);
            return ContentProviderNewForValue(value);
        }
        finally
        {
            ValueUnset(value);
        }
    }

    private static nint BytesProvider(string mimeType, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        nint data;
        fixed (byte* pointer = bytes) data = BytesNew(pointer, (nuint)bytes.Length);
        try
        {
            return ContentProviderNewForBytes(mimeType, data);
        }
        finally
        {
            BytesUnref(data);
        }
    }

    // A generic document icon under the pointer, as GtkDragSource shows for content it cannot draw.
    internal static void SetIcon(nint drag)
    {
        var icon = DragIconGetForDrag(drag);
        if (icon == 0) return;
        var image = ImageNewFromIconName("text-x-generic");
        ImageSetIconSize(image, LargeIcon);
        DragIconSetChild(icon, image);
    }

    // Ends the sequences of the gestures on the widget and its ancestors up to the top-level, as
    // gtk_widget_reset_controllers (private) does for one widget; other controllers are left alone.
    internal static void ResetGestures(nint widget, nint toplevel)
    {
        var gestureType = GestureGetType();
        for (var current = widget; current != 0; current = current == toplevel ? 0 : WidgetGetParent(current))
        {
            var controllers = WidgetObserveControllers(current);
            if (controllers == 0) continue;
            try
            {
                var count = ListModelGetNItems(controllers);
                for (uint index = 0; index < count; index++)
                {
                    var controller = ListModelGetItem(controllers, index);
                    if (controller == 0) continue;
                    try
                    {
                        if (TypeCheckInstanceIsA(controller, gestureType) != 0) EventControllerReset(controller);
                    }
                    finally
                    {
                        ObjectUnref(controller);
                    }
                }
            }
            finally
            {
                ObjectUnref(controllers);
            }
        }
    }

    internal static nuint Connect(nint instance, string signal, nint callback, nint context) =>
        SignalConnectData(instance, signal, callback, context, 0, 0);

    internal static void Disconnect(nint instance, nuint handler)
    {
        if (handler != 0) SignalHandlerDisconnect(instance, handler);
    }

    [LibraryImport(Gtk, EntryPoint = "gtk_gesture_get_type")]
    private static partial nuint GestureGetType();
    [LibraryImport(Gtk, EntryPoint = "gtk_event_controller_reset")]
    private static partial void EventControllerReset(nint controller);
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_observe_controllers")]
    private static partial nint WidgetObserveControllers(nint widget);
    [LibraryImport(Gtk, EntryPoint = "gtk_widget_get_parent")]
    private static partial nint WidgetGetParent(nint widget);
    [LibraryImport(Gio, EntryPoint = "g_list_model_get_n_items")]
    private static partial uint ListModelGetNItems(nint list);
    [LibraryImport(Gio, EntryPoint = "g_list_model_get_item")]
    private static partial nint ListModelGetItem(nint list, uint index);
    [LibraryImport(GObject, EntryPoint = "g_type_check_instance_is_a")]
    private static partial int TypeCheckInstanceIsA(nint instance, nuint type);
    [LibraryImport(Gtk, EntryPoint = "gdk_surface_get_display")]
    private static partial nint SurfaceGetDisplay(nint surface);
    [LibraryImport(Gtk, EntryPoint = "gdk_display_get_default_seat")]
    private static partial nint DisplayGetDefaultSeat(nint display);
    [LibraryImport(Gtk, EntryPoint = "gdk_seat_get_pointer")]
    private static partial nint SeatGetPointer(nint seat);
    [LibraryImport(Gtk, EntryPoint = "gdk_surface_get_device_position")]
    private static partial int SurfaceGetDevicePosition(nint surface, nint device, double* x, double* y, uint* mask);
    [LibraryImport(Gtk, EntryPoint = "gdk_drag_begin")]
    internal static partial nint DragBegin(nint surface, nint device, nint content, int actions, double dx, double dy);
    [LibraryImport(Gtk, EntryPoint = "gdk_drag_get_selected_action")]
    internal static partial int SelectedAction(nint drag);
    [LibraryImport(Gtk, EntryPoint = "gdk_drag_drop_done")]
    private static partial void DragDropDoneCore(nint drag, int success);
    internal static void DragDropDone(nint drag, bool success) => DragDropDoneCore(drag, success ? 1 : 0);
    [LibraryImport(Gtk, EntryPoint = "gtk_drag_icon_get_for_drag")]
    private static partial nint DragIconGetForDrag(nint drag);
    [LibraryImport(Gtk, EntryPoint = "gtk_drag_icon_set_child")]
    private static partial void DragIconSetChild(nint icon, nint child);
    [LibraryImport(Gtk, EntryPoint = "gtk_image_new_from_icon_name", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ImageNewFromIconName(string name);
    [LibraryImport(Gtk, EntryPoint = "gtk_image_set_icon_size")]
    private static partial void ImageSetIconSize(nint image, int size);
    [LibraryImport(Gtk, EntryPoint = "gdk_content_provider_new_for_value")]
    private static partial nint ContentProviderNewForValue(byte* value);
    [LibraryImport(Gtk, EntryPoint = "gdk_content_provider_new_for_bytes", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ContentProviderNewForBytes(string mimeType, nint bytes);
    [LibraryImport(Gtk, EntryPoint = "gdk_content_provider_new_union")]
    private static partial nint ContentProviderNewUnion(nint* providers, nuint count);
    [LibraryImport(Gtk, EntryPoint = "gdk_file_list_get_type")]
    private static partial nuint FileListGetType();
    [LibraryImport(Gtk, EntryPoint = "gdk_file_list_new_from_array")]
    private static partial nint FileListNewFromArray(nint* files, nuint count);
    [LibraryImport(Gio, EntryPoint = "g_file_new_for_path", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint FileNewForPath(string path);
    [LibraryImport(GObject, EntryPoint = "g_value_init")]
    private static partial nint ValueInit(byte* value, nuint type);
    [LibraryImport(GObject, EntryPoint = "g_value_unset")]
    private static partial void ValueUnset(byte* value);
    [LibraryImport(GObject, EntryPoint = "g_value_take_boxed")]
    private static partial void ValueTakeBoxed(byte* value, nint boxed);
    [LibraryImport(GObject, EntryPoint = "g_value_set_string", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void ValueSetString(byte* value, string text);
    [LibraryImport(GObject, EntryPoint = "g_object_unref")]
    internal static partial void ObjectUnref(nint instance);
    [LibraryImport(GObject, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nuint SignalConnectData(nint instance, string signal, nint handler, nint data, nint destroy, int flags);
    [LibraryImport(GObject, EntryPoint = "g_signal_handler_disconnect")]
    private static partial void SignalHandlerDisconnect(nint instance, nuint handler);
    [LibraryImport(GLib, EntryPoint = "g_bytes_new")]
    private static partial nint BytesNew(byte* data, nuint size);
    [LibraryImport(GLib, EntryPoint = "g_bytes_unref")]
    private static partial void BytesUnref(nint bytes);
}
