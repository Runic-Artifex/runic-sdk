using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Runic.Platform.Prototype;

// GTK owns X11/Wayland parent export and the portal request. Never substitute a
// GtkWindow pointer for an XID or a Wayland portal token. No grant is revoked by
// this provider: portal grants belong to the user's permission store, not us.
internal sealed partial class LinuxFilePicker(DesktopPickerOwner owner) : INativeFilePicker
{
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RequiresPortal && !await Task.Run(ProbePortal, cancellationToken).ConfigureAwait(false))
            throw new NativeBackendUnavailableException();
        cancellationToken.ThrowIfCancellationRequested();
        var state = new Selection();
        nint dialog = 0;
        GCHandle handle = default;
        CancellationTokenRegistration registration = default;
        Task cancellation = Task.CompletedTask;
        try
        {
            await owner.InvokeAsync(parent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                dialog = New(save ? "Save file" : "Open file", parent, save ? 1 : 0, save ? "Save" : "Open", "Cancel");
                if (dialog == 0) throw new IOException("GTK could not create a file chooser.");
                handle = GCHandle.Alloc(state);
                Connect(dialog, "response", ResponsePointer, GCHandle.ToIntPtr(handle), 0, 0);
                SetModal(dialog, 1);
                SetLocalOnly(dialog, 1);
                if (save) { SetOverwrite(dialog, 1); SetName(dialog, suggestedName!); }
                Show(dialog);
                Shown.TrySetResult();
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() => cancellation = CancelAsync());
            async Task CancelAsync()
            {
                try { await owner.InvokeAsync(_ => { Hide(dialog); state.Result.TrySetResult(null); }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { state.Result.TrySetException(error); }
            }
            return await state.Result.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellation.ConfigureAwait(false);
            if (dialog != 0)
                await owner.InvokeAsync(_ => { Destroy(dialog); Unref(dialog); if (handle.IsAllocated) handle.Free(); }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class Selection
    {
        internal TaskCompletionSource<NativeFileSelection?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static unsafe nint ResponsePointer => (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&Response;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Response(nint dialog, int response, nint context)
    {
        var state = (Selection)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (response != -3) { state.Result.TrySetResult(null); return; } // GTK_RESPONSE_ACCEPT
            nint filename = Filename(dialog);
            try
            {
                string path = Marshal.PtrToStringUTF8(filename) ?? throw new IOException("GTK did not return a local file.");
                // Portal documents grant the selected file, not sibling creation.
                // Be conservative for all portal-backed/sandboxed selections.
                bool sandbox = RequiresPortal || path.Contains("/doc/", StringComparison.Ordinal) && path.StartsWith("/run/user/", StringComparison.Ordinal);
                state.Result.TrySetResult(new(path, null, !sandbox));
            }
            finally { if (filename != 0) Free(filename); }
        }
        catch (Exception error) { state.Result.TrySetException(error); }
    }
    private static bool RequiresPortal => File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP") is not null
        || Environment.GetEnvironmentVariable("GTK_USE_PORTAL") == "1";

    // Probe required portal availability off the GTK owner thread. Bound the
    // D-Bus call even when the session service has stopped responding.
    private static bool ProbePortal()
    {
        nint cancellable = NewCancellable();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var registration = deadline.Token.Register(() => Cancel(cancellable));
        nint bus = 0;
        try
        {
            bus = GetBus(2, cancellable, out nint error);
            if (error != 0) FreeError(error);
            if (bus == 0) return false;
            nint reply = Call(bus, "org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                "org.freedesktop.DBus.Peer", "Ping", 0, 0, 0, 3000, cancellable, out error);
            if (error != 0) FreeError(error);
            if (reply == 0) return false;
            VariantUnref(reply);
            return true;
        }
        finally
        {
            registration.Dispose(); // Join cancellation before freeing its native target.
            Unref(cancellable);
            if (bus != 0) Unref(bus);
        }
    }
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")] private static partial nint NewCancellable();
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")] private static partial void Cancel(nint cancellable);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_bus_get_sync")] private static partial nint GetBus(int type, nint cancellable, out nint error);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_dbus_connection_call_sync", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Call(nint connection, string bus, string path, string face, string method, nint parameters, nint replyType, int flags, int timeout, nint cancellable, out nint error);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static partial void FreeError(nint error);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_variant_unref")] private static partial void VariantUnref(nint value);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_native_new", StringMarshalling = StringMarshalling.Utf8)] private static partial nint New(string title, nint parent, int action, string accept, string cancel);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_native_dialog_set_modal")] private static partial void SetModal(nint dialog, int modal);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_set_local_only")] private static partial void SetLocalOnly(nint dialog, int value);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_set_do_overwrite_confirmation")] private static partial void SetOverwrite(nint dialog, int value);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_set_current_name", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetName(nint dialog, string name);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_native_dialog_show")] private static partial void Show(nint dialog);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_native_dialog_hide")] private static partial void Hide(nint dialog);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_native_dialog_destroy")] private static partial void Destroy(nint dialog);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_get_filename")] private static partial nint Filename(nint dialog);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)] private static partial ulong Connect(nint instance, string signal, nint callback, nint data, nint destroy, int flags);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static partial void Unref(nint instance);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")] private static partial void Free(nint data);
}
