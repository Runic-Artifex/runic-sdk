using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Runic.Platform.Runtime;

namespace Runic.Platform.Linux;

// GTK owns X11/Wayland parent export and the portal request. Never substitute a
// GtkWindow pointer for an XID or a Wayland portal token. No grant is revoked by
// this provider: portal grants belong to the user's permission store, not us.
internal sealed partial class LinuxFilePicker(INativePickerOwner owner) : INativeFilePicker, INativeDirectoryPicker
{
    internal TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Host paths stage beside the file; portal document views stage through the
    // document portal's tempfile and rename. Refusals are reported per save.
    public bool SupportsAtomicReplace => true;

    public bool SupportsMultipleSelection => true;

    public async ValueTask<NativeFileSelection?> SelectAsync(bool save, string? suggestedName, CancellationToken cancellationToken) =>
        (await SelectAsync(new NativeFileDialogRequest(save, suggestedName), cancellationToken).ConfigureAwait(false)) is [var selection] ? selection : null;

    public async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selections = await SelectCoreAsync(request, false, cancellationToken).ConfigureAwait(false);
        if (!request.Save || selections is not [var selection]) return selections;
        return [selection with { Location = await Portal.DocumentPortalPaths.Session.ClassifyAsync(selection.Path, cancellationToken).ConfigureAwait(false) }];
    }

    public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(CancellationToken cancellationToken) => SelectDirectoryAsync(null, cancellationToken);

    public async ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken)
    {
        var selections = await SelectCoreAsync(new NativeFileDialogRequest(false, null) { InitialDirectory = initialDirectory }, true, cancellationToken)
            .ConfigureAwait(false);
        return selections is [var selection] ? new(selection.Path, selection.Access) : null;
    }

    private async ValueTask<IReadOnlyList<NativeFileSelection>?> SelectCoreAsync(NativeFileDialogRequest request, bool directory, CancellationToken cancellationToken)
    {
        bool save = request.Save, multiple = request.AllowMultiple && !save && !directory;
        string? suggestedName = request.SuggestedName;
        cancellationToken.ThrowIfCancellationRequested();
        if (RequiresPortal && !await Task.Run(ProbePortal, cancellationToken).ConfigureAwait(false))
            throw new NativeBackendUnavailableException();
        cancellationToken.ThrowIfCancellationRequested();
        var state = new Selection(multiple);
        nint dialog = 0;
        nint context = 0;
        nint retainedParent = 0;
        ulong responseSignal = 0, parentSignal = 0;
        GCHandle handle = default;
        CancellationTokenRegistration registration = default;
        Task cancellation = Task.CompletedTask;
        bool released = false, loopStopped = false;
        try
        {
            await owner.InvokeAsync(parent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                context = RefThreadDefaultContext();
                dialog = New(directory ? "Open directory" : save ? "Save file" : "Open file", parent, directory ? 2 : save ? 1 : 0, save ? "Save" : "Open", "Cancel");
                if (dialog == 0) throw new IOException("GTK could not create a file chooser.");
                handle = GCHandle.Alloc(state);
                responseSignal = Connect(dialog, "response", ResponsePointer, GCHandle.ToIntPtr(handle), 0, 0);
                retainedParent = Ref(parent);
                parentSignal = Connect(parent, "destroy", ParentDestroyedPointer, GCHandle.ToIntPtr(handle), 0, 0);
                SetModal(dialog, 1);
                SetLocalOnly(dialog, 1);
                if (multiple) SetSelectMultiple(dialog, 1);
                // A folder that does not exist leaves GTK's own choice; the request is a hint.
                if (request.InitialDirectory is { } folder) _ = SetCurrentFolder(dialog, folder);
                if (!directory) AddFilters(dialog, request.Filters);
                if (save) { SetOverwrite(dialog, 1); SetName(dialog, suggestedName ?? "Untitled"); }
                Show(dialog);
                Shown.TrySetResult();
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() => cancellation = CancelAsync());
            async Task CancelAsync()
            {
                try
                {
                    // A stopped loop delivers no response; report the cancellation without it.
                    if (!await TryOnContextAsync(context, () => { if (!released) Hide(dialog); state.Result.TrySetResult(null); }, ClosedOwnerGrace).ConfigureAwait(false))
                    {
                        loopStopped = true;
                        state.Result.TrySetResult(null);
                    }
                }
                catch (Exception error) { state.Result.TrySetException(error); }
            }
            var result = await state.Result.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellation.ConfigureAwait(false);
            if (context != 0)
            {
                // The verified owner can be gone. The context and parent
                // references keep cleanup valid until native signals are disconnected.
                // Native objects are touched only on the chooser's GTK context. If that
                // loop no longer iterates, the queued cleanup keeps its references and
                // the context: the dialog leaks with the stopped loop rather than
                // hanging this picker and presentation shutdown.
                await TryOnContextAsync(context, () =>
                {
                    released = true;
                    if (dialog != 0)
                    {
                        if (responseSignal != 0 && IsConnected(dialog, responseSignal) != 0) Disconnect(dialog, responseSignal);
                        Destroy(dialog);
                        Unref(dialog);
                    }
                    if (retainedParent != 0)
                    {
                        if (parentSignal != 0 && IsConnected(retainedParent, parentSignal) != 0) Disconnect(retainedParent, parentSignal);
                        Unref(retainedParent);
                    }
                    if (handle.IsAllocated) handle.Free();
                }, loopStopped ? TimeSpan.Zero : ClosedOwnerGrace, releaseContext: true).ConfigureAwait(false);
            }
        }
    }

    // Context work has no other escape once the owner closed: its loop may already
    // have quit. A live loop runs queued work well within this bound.
    private static readonly TimeSpan ClosedOwnerGrace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OwnerPoll = TimeSpan.FromMilliseconds(100);

    // Returns false when the action did not run: GLib destroyed its source, or the owner
    // closed and the context did not dispatch it within the grace period. Abandoned work
    // stays queued with every native reference it captured and may still run if the loop
    // iterates again. With releaseContext, the caller's context reference is released
    // after the work finishes or its source is destroyed, never while it is still queued.
    private async ValueTask<bool> TryOnContextAsync(nint context, Action action, TimeSpan grace, bool releaseContext = false)
    {
        Task<bool> work;
        try { work = OnContextAsync(context, action); }
        catch { if (releaseContext) UnrefContext(context); throw; }
        if (releaseContext)
            _ = work.ContinueWith(_ => UnrefContext(context), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        while (!work.IsCompleted)
        {
            bool closed = !owner.IsAvailable;
            await Task.WhenAny(work, Task.Delay(closed ? grace : OwnerPoll)).ConfigureAwait(false);
            if (closed && !work.IsCompleted) return false;
        }
        return await work.ConfigureAwait(false);
    }

    private sealed class Selection(bool multiple)
    {
        internal bool Multiple => multiple;
        internal TaskCompletionSource<IReadOnlyList<NativeFileSelection>?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The chooser owns each filter: gtk_file_chooser_add_filter sinks its floating reference.
    private static void AddFilters(nint dialog, ImmutableArray<FileDialogFilter> filters)
    {
        if (filters.IsDefaultOrEmpty) return;
        nint first = 0;
        foreach (var filter in filters)
        {
            nint native = NewFilter();
            SetFilterName(native, filter.Name);
            foreach (var pattern in Portal.FileDialogPatterns.Globs(filter)) AddPattern(native, pattern);
            AddFilter(dialog, native);
            if (first == 0) first = native;
        }
        SetFilter(dialog, first);
    }
    private static unsafe nint ResponsePointer => (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&Response;
    private static unsafe nint ParentDestroyedPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ParentDestroyed;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ParentDestroyed(nint parent, nint context)
    {
        var state = (Selection)GCHandle.FromIntPtr(context).Target!;
        state.Result.TrySetException(new OwnerClosedException());
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Response(nint dialog, int response, nint context)
    {
        var state = (Selection)GCHandle.FromIntPtr(context).Target!;
        try
        {
            if (response != -3) { state.Result.TrySetResult(null); return; } // GTK_RESPONSE_ACCEPT
            if (state.Multiple) { state.Result.TrySetResult(ReadFilenames(Filenames(dialog))); return; }
            nint filename = Filename(dialog);
            try
            {
                state.Result.TrySetResult([new(ReadExactPath(filename), null, true)]);
            }
            finally { if (filename != 0) Free(filename); }
        }
        catch (Exception error) { state.Result.TrySetException(error); }
    }
    // Takes ownership of a GSList of g_malloc'd filenames and frees every element even when one cannot be read.
    internal static unsafe List<NativeFileSelection> ReadFilenames(nint list)
    {
        var selections = new List<NativeFileSelection>();
        Exception? failure = null;
        for (var node = (GSList*)list; node != null; node = node->Next)
        {
            try { if (failure is null) selections.Add(new(ReadExactPath(node->Data), null, true)); }
            catch (Exception error) { failure = error; }
            finally { Free(node->Data); }
        }
        if (list != 0) FreeList(list);
        if (failure is not null) throw failure;
        if (selections.Count == 0) throw new IOException("GTK did not return a local path.");
        return selections;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct GSList { internal nint Data; internal GSList* Next; }
    internal static string ReadExactPath(nint bytes)
    {
        if (bytes == 0) throw new IOException("GTK did not return a local path.");
        int length = 0;
        while (Marshal.ReadByte(bytes, length) != 0)
            if (++length > 1024 * 1024) throw new IOException("The native path exceeds the supported bound.");
        var value = new byte[length];
        Marshal.Copy(bytes, value, 0, length);
        try { return new UTF8Encoding(false, true).GetString(value); }
        catch (DecoderFallbackException error) { throw new IOException("The native path cannot be represented exactly in C#.", error); }
    }
    private sealed record ContextWork(Action Action, TaskCompletionSource<bool> Completion);
    // Completes with true (or the action's exception) after dispatch, or with false
    // when GLib destroys the source without dispatching it.
    private static unsafe Task<bool> OnContextAsync(nint context, Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(new ContextWork(action, completion));
        nint source = NewIdleSource();
        try
        {
            SetSourceCallback(source, (nint)(delegate* unmanaged[Cdecl]<nint, int>)&RunContextWork, GCHandle.ToIntPtr(handle),
                (nint)(delegate* unmanaged[Cdecl]<nint, void>)&ReleaseContextWork);
            if (AttachSource(source, context) == 0) throw new IOException("GTK could not queue chooser cleanup.");
        }
        finally { UnrefSource(source); }
        return completion.Task;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RunContextWork(nint context)
    {
        var work = (ContextWork)GCHandle.FromIntPtr(context).Target!;
        try { work.Action(); work.Completion.TrySetResult(true); }
        catch (Exception error) { work.Completion.TrySetException(error); }
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ReleaseContextWork(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        ((ContextWork)handle.Target!).Completion.TrySetResult(false);
        handle.Free();
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
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_get_filenames")] private static partial nint Filenames(nint dialog);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_set_select_multiple")] private static partial void SetSelectMultiple(nint dialog, int value);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_set_current_folder", StringMarshalling = StringMarshalling.Utf8)] private static partial int SetCurrentFolder(nint dialog, string folder);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_filter_new")] private static partial nint NewFilter();
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_filter_set_name", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetFilterName(nint filter, string name);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_filter_add_pattern", StringMarshalling = StringMarshalling.Utf8)] private static partial void AddPattern(nint filter, string pattern);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_add_filter")] private static partial void AddFilter(nint dialog, nint filter);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_set_filter")] private static partial void SetFilter(nint dialog, nint filter);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_slist_free")] private static partial void FreeList(nint list);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)] private static partial ulong Connect(nint instance, string signal, nint callback, nint data, nint destroy, int flags);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static partial void Unref(nint instance);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_ref")] private static partial nint Ref(nint instance);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_disconnect")] private static partial void Disconnect(nint instance, ulong signal);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_handler_is_connected")] private static partial int IsConnected(nint instance, ulong signal);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_main_context_ref_thread_default")] private static partial nint RefThreadDefaultContext();
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_main_context_unref")] private static partial void UnrefContext(nint context);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_idle_source_new")] private static partial nint NewIdleSource();
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_source_set_callback")] private static partial void SetSourceCallback(nint source, nint callback, nint data, nint destroy);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_source_attach")] private static partial uint AttachSource(nint source, nint context);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_source_unref")] private static partial void UnrefSource(nint source);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")] private static partial void Free(nint data);
}
