using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;

namespace Runic.Desktop.Internal;

// B11: drags out of the GTK 3 window and the effect of drops onto it.
// - A drag out is gtk_drag_begin_with_coordinates on the web view while the primary button is held, with the last
//   primary button press on the view as its trigger event (kept by the menus' trigger handler). GTK grabs the pointer
//   from WebKit's implicit grab. The targets are GTK's URI targets (text/uri-list, and the FileTransfer portal's
//   application/vnd.portal.files and application/vnd.portal.filetransfer when the portal runs, which GTK 3.24.37 and
//   later serialize itself, so a Flatpak application drags files out without Runic's help) and its text targets
//   (UTF8_STRING, text/plain;charset=utf-8 and the legacy ones).
// - The drag's drag-data-get, drag-failed and drag-end handlers on the web view are connected for that drag only and
//   act only for its context, so WebKit's own drags are unaffected.
internal sealed partial class LinuxWebKitGtkHost
{
    private const uint UriTargets = 1;
    private const uint TextTargets = 2;
    private static readonly Lazy<Gtk3DragApi> DragApiValue = new(static () => new());
    private static Gtk3DragApi DragApi => DragApiValue.Value;

    // GTK thread only: a copy of the last primary button press on the view, and the running drag out.
    private nint _dragPress;
    private Gtk3DragOut? _dragOut;
    // GTK thread only: the effect GTK reported when the pending drop was dropped.
    private DesktopDragEffects _dropEffect = DesktopDragEffects.Copy;

    public ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!DragApi.IsAvailable)
            return ValueTask.FromException<DesktopDragEffects>(new NotSupportedException("This GTK 3 starts no drags."));
        cancellationToken.ThrowIfCancellationRequested();
        var drag = new Gtk3DragOut(data);
        var started = InvokeWindowAsync(() => BeginDragOut(drag), cancellationToken);
        return new ValueTask<DesktopDragEffects>(WaitForDragOutAsync(drag, started));
    }

    private static async Task<DesktopDragEffects> WaitForDragOutAsync(Gtk3DragOut drag, ValueTask started)
    {
        await started.ConfigureAwait(false);
        return await drag.Result.Task.ConfigureAwait(false);
    }

    // GTK thread. None at once when no primary button is held, another drag out runs, or GTK cannot grab the pointer.
    private unsafe void BeginDragOut(Gtk3DragOut drag)
    {
        if (_dragOut is not null || _webView == 0 || !DragApi.IsPrimaryButtonHeld(_webView))
        {
            drag.Result.TrySetResult(DesktopDragEffects.None);
            return;
        }
        var uris = GtkDragSupport.UriList(drag.Data);
        var text = GtkDragSupport.PlainText(drag.Data);
        var targets = DragApi.TargetListNew();
        try
        {
            if (uris.Count != 0) DragApi.AddUriTargets(targets, UriTargets);
            if (text is not null) DragApi.AddTextTargets(targets, TextTargets);
            drag.Uris = uris;
            drag.FileUris = GtkDragSupport.FileUris(drag.Data);
            drag.Text = text;
            _dragOut = drag;
            drag.DataGetHandler = Api.Connect(_webView, "drag-data-get",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, uint, uint, nint, void>)&OnDragOutDataGet, this);
            drag.FailedHandler = Api.Connect(_webView, "drag-failed",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&OnDragOutFailed, this);
            drag.EndHandler = Api.Connect(_webView, "drag-end",
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnDragOutEnd, this);
            drag.Context = DragApi.Begin(_webView, targets, GtkDragSupport.ToGtk3Actions(drag.Data.AllowedEffects), _dragPress);
            if (drag.Context == 0) FinishDragOut(drag, DesktopDragEffects.None);
        }
        catch
        {
            FinishDragOut(drag, DesktopDragEffects.None);
            throw;
        }
        finally
        {
            DragApi.TargetListUnref(targets);
        }
    }

    // GTK thread. Disconnects the drag's handlers, which releases their handles, and completes the call.
    private void FinishDragOut(Gtk3DragOut drag, DesktopDragEffects effect)
    {
        if (ReferenceEquals(_dragOut, drag)) _dragOut = null;
        foreach (var handler in (ulong[])[drag.DataGetHandler, drag.FailedHandler, drag.EndHandler])
        {
            if (handler != 0 && _webView != 0) DragApi.Disconnect(_webView, handler);
        }
        drag.DataGetHandler = drag.FailedHandler = drag.EndHandler = 0;
        drag.Result.TrySetResult(effect);
    }

    // From the menus' trigger handler, GTK thread: keeps a copy of a primary button press on the view.
    private void KeepDragPress(nint nativeEvent)
    {
        if (!DragApi.IsAvailable || !DragApi.IsPrimaryPress(nativeEvent)) return;
        var copy = DragApi.EventCopy(nativeEvent);
        if (_dragPress != 0) DragApi.EventFree(_dragPress);
        _dragPress = copy;
    }

    // GTK thread, when the window is destroyed: a running drag out completes with None.
    private void ReleaseDragOut()
    {
        if (_dragOut is { } drag) FinishDragOut(drag, DesktopDragEffects.None);
        if (_dragPress != 0)
        {
            DragApi.EventFree(_dragPress);
            _dragPress = 0;
        }
    }

    // The action GTK reports for a drop on the view: in drag-drop, the one the target (WebKit) last chose.
    private static DesktopDragEffects DropEffectOf(nint context) =>
        DragApi.IsAvailable && context != 0
            ? GtkDragSupport.DropEffect(GtkDragSupport.FromGtk3Actions(DragApi.SelectedAction(context)))
            : DesktopDragEffects.Copy;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDragOutDataGet(nint widget, nint context, nint selection, uint info, uint time, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not LinuxWebKitGtkHost { _dragOut: { } drag } || drag.Context != context || selection == 0) return;
            if (info == UriTargets)
            {
                // The portal targets carry files only; text/uri-list also the URIs.
                var uris = DragApi.SelectionTargetIsUriList(selection) ? drag.Uris : drag.FileUris;
                if (uris.Count != 0) DragApi.SetUris(selection, uris);
            }
            else if (info == TextTargets && drag.Text is { } text)
            {
                DragApi.SetText(selection, text);
            }
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    // GTK 3 signals a drop nowhere, a refused drop and Escape with drag-failed, before drag-end.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnDragOutFailed(nint widget, nint context, int result, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is LinuxWebKitGtkHost { _dragOut: { } drag } && drag.Context == context) drag.Failed = true;
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
        // GTK animates the drag back to where it started, then ends it.
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDragOutEnd(nint widget, nint context, nint userData)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not LinuxWebKitGtkHost { _dragOut: { } drag } host || drag.Context != context) return;
            var effect = drag.Failed
                ? DesktopDragEffects.None
                : DesktopDragRules.Single(GtkDragSupport.FromGtk3Actions(DragApi.SelectedAction(context)), drag.Data.AllowedEffects);
            host.FinishDragOut(drag, effect);
        }
        catch
        {
            // Native callbacks cannot unwind through GTK.
        }
    }

    private sealed class Gtk3DragOut(DesktopDragData data)
    {
        internal DesktopDragData Data { get; } = data;
        internal TaskCompletionSource<DesktopDragEffects> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // GTK thread only.
        internal nint Context { get; set; }
        internal List<string> Uris { get; set; } = [];
        internal List<string> FileUris { get; set; } = [];
        internal string? Text { get; set; }
        internal bool Failed { get; set; }
        internal ulong DataGetHandler { get; set; }
        internal ulong FailedHandler { get; set; }
        internal ulong EndHandler { get; set; }
    }

    // The GTK 3 drag source functions; GTK 3.22, which WebKitGTK 4.1 requires, has every one.
    private sealed unsafe class Gtk3DragApi
    {
        private const int GdkButtonPress = 4;
        private readonly nint _targetListNew, _targetListUnref, _targetListAddUriTargets, _targetListAddTextTargets, _dragBegin,
            _selectionDataSetUris, _selectionDataSetText, _selectionDataGetTarget, _dragContextGetSelectedAction, _widgetGetWindow,
            _widgetGetDisplay, _displayGetDefaultSeat, _seatGetPointer, _windowGetDevicePosition, _eventGetEventType, _eventGetButton,
            _eventCopy, _eventFree, _atomIntern, _signalHandlerDisconnect;

        internal Gtk3DragApi()
        {
            if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libgtk-3.so.0", out var gtk)
                || !NativeLibrary.TryLoad("libgobject-2.0.so.0", out var gObject)) return;
            nint Export(nint library, string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;
            _targetListNew = Export(gtk, "gtk_target_list_new");
            _targetListUnref = Export(gtk, "gtk_target_list_unref");
            _targetListAddUriTargets = Export(gtk, "gtk_target_list_add_uri_targets");
            _targetListAddTextTargets = Export(gtk, "gtk_target_list_add_text_targets");
            _dragBegin = Export(gtk, "gtk_drag_begin_with_coordinates");
            _selectionDataSetUris = Export(gtk, "gtk_selection_data_set_uris");
            _selectionDataSetText = Export(gtk, "gtk_selection_data_set_text");
            _selectionDataGetTarget = Export(gtk, "gtk_selection_data_get_target");
            _dragContextGetSelectedAction = Export(gtk, "gdk_drag_context_get_selected_action");
            _widgetGetWindow = Export(gtk, "gtk_widget_get_window");
            _widgetGetDisplay = Export(gtk, "gtk_widget_get_display");
            _displayGetDefaultSeat = Export(gtk, "gdk_display_get_default_seat");
            _seatGetPointer = Export(gtk, "gdk_seat_get_pointer");
            _windowGetDevicePosition = Export(gtk, "gdk_window_get_device_position");
            _eventGetEventType = Export(gtk, "gdk_event_get_event_type");
            _eventGetButton = Export(gtk, "gdk_event_get_button");
            _eventCopy = Export(gtk, "gdk_event_copy");
            _eventFree = Export(gtk, "gdk_event_free");
            _atomIntern = Export(gtk, "gdk_atom_intern");
            _signalHandlerDisconnect = Export(gObject, "g_signal_handler_disconnect");
            IsAvailable = new[]
            {
                _targetListNew, _targetListUnref, _targetListAddUriTargets, _targetListAddTextTargets, _dragBegin, _selectionDataSetUris,
                _selectionDataSetText, _selectionDataGetTarget, _dragContextGetSelectedAction, _widgetGetWindow, _widgetGetDisplay,
                _displayGetDefaultSeat, _seatGetPointer, _windowGetDevicePosition, _eventGetEventType, _eventGetButton, _eventCopy,
                _eventFree, _atomIntern, _signalHandlerDisconnect,
            }.All(static address => address != 0);
        }

        internal bool IsAvailable { get; }

        internal nint TargetListNew() => ((delegate* unmanaged[Cdecl]<nint, uint, nint>)_targetListNew)(0, 0);

        internal void TargetListUnref(nint list) => ((delegate* unmanaged[Cdecl]<nint, void>)_targetListUnref)(list);

        internal void AddUriTargets(nint list, uint info) => ((delegate* unmanaged[Cdecl]<nint, uint, void>)_targetListAddUriTargets)(list, info);

        internal void AddTextTargets(nint list, uint info) => ((delegate* unmanaged[Cdecl]<nint, uint, void>)_targetListAddTextTargets)(list, info);

        // Button 1, from the pointer's current position (-1, -1); the GdkDragContext, which GTK owns, or 0.
        internal nint Begin(nint widget, nint targets, int actions, nint triggerEvent) =>
            ((delegate* unmanaged[Cdecl]<nint, nint, int, int, nint, int, int, nint>)_dragBegin)(widget, targets, actions, 1, triggerEvent, -1, -1);

        // Whether the seat's pointer holds the primary button, read from the display server.
        internal bool IsPrimaryButtonHeld(nint widget)
        {
            var window = ((delegate* unmanaged[Cdecl]<nint, nint>)_widgetGetWindow)(widget);
            var display = ((delegate* unmanaged[Cdecl]<nint, nint>)_widgetGetDisplay)(widget);
            var seat = display == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)_displayGetDefaultSeat)(display);
            var pointer = seat == 0 ? 0 : ((delegate* unmanaged[Cdecl]<nint, nint>)_seatGetPointer)(seat);
            if (window == 0 || pointer == 0) return false;
            int x = 0, y = 0;
            uint mask = 0;
            ((delegate* unmanaged[Cdecl]<nint, nint, int*, int*, uint*, nint>)_windowGetDevicePosition)(window, pointer, &x, &y, &mask);
            return (mask & GtkDragSupport.Button1Mask) != 0;
        }

        internal bool IsPrimaryPress(nint nativeEvent)
        {
            if (nativeEvent == 0 || ((delegate* unmanaged[Cdecl]<nint, int>)_eventGetEventType)(nativeEvent) != GdkButtonPress) return false;
            uint button = 0;
            return ((delegate* unmanaged[Cdecl]<nint, uint*, int>)_eventGetButton)(nativeEvent, &button) != 0 && button == 1;
        }

        internal nint EventCopy(nint nativeEvent) => ((delegate* unmanaged[Cdecl]<nint, nint>)_eventCopy)(nativeEvent);

        internal void EventFree(nint nativeEvent) => ((delegate* unmanaged[Cdecl]<nint, void>)_eventFree)(nativeEvent);

        internal int SelectedAction(nint context) => ((delegate* unmanaged[Cdecl]<nint, int>)_dragContextGetSelectedAction)(context);

        internal void Disconnect(nint instance, ulong handler) =>
            ((delegate* unmanaged[Cdecl]<nint, ulong, void>)_signalHandlerDisconnect)(instance, handler);

        internal bool SelectionTargetIsUriList(nint selection)
        {
            using var name = Utf8String.Create("text/uri-list");
            var uriList = ((delegate* unmanaged[Cdecl]<byte*, int, nint>)_atomIntern)(name.Pointer, 0);
            return ((delegate* unmanaged[Cdecl]<nint, nint>)_selectionDataGetTarget)(selection) == uriList;
        }

        // gtk_selection_data_set_uris: text/uri-list, or the files registered with the FileTransfer portal.
        internal void SetUris(nint selection, List<string> uris)
        {
            var strings = new nint[uris.Count + 1];
            try
            {
                for (var index = 0; index < uris.Count; index++) strings[index] = Marshal.StringToCoTaskMemUTF8(uris[index]);
                fixed (nint* values = strings) ((delegate* unmanaged[Cdecl]<nint, nint*, int>)_selectionDataSetUris)(selection, values);
            }
            finally
            {
                foreach (var value in strings) Marshal.FreeCoTaskMem(value);
            }
        }

        internal void SetText(nint selection, string text)
        {
            using var value = Utf8String.Create(text);
            ((delegate* unmanaged[Cdecl]<nint, byte*, int, int>)_selectionDataSetText)(selection, value.Pointer, -1);
        }
    }
}
