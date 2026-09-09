using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux;

/// <summary>Exports portal parents from the explicitly selected GTK3 presentation.</summary>
public sealed partial class Gtk3PortalWindowOwner(INativePickerOwner owner) : IPortalWindowOwner
{
    // GTK3 supports one exported Wayland handle per window at a time.
    private static readonly ConditionalWeakTable<INativePickerOwner, SemaphoreSlim> ExportGates = new();
    private SemaphoreSlim ExportGate => ExportGates.GetValue(owner, static _ => new(1, 1));
    /// <inheritdoc />
    public Guid Generation => owner.Generation;
    /// <inheritdoc />
    public bool IsAvailable => owner.IsAvailable;
    /// <inheritdoc />
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => owner.InvokeAsync(action, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<PortalParentLease> ExportParentAsync(CancellationToken cancellationToken = default)
    {
        await ExportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var state = new ExportState { Generation = owner.Generation };
        bool transferred = false;
        try
        {
            await owner.InvokeAsync(window =>
            {
                nint native = GetWindow(window);
                if (native == 0) throw new NativeBackendUnavailableException();
                state.NativeWindow = native;
                if (IsType(native, X11Type()) != 0)
                {
                    state.Result.TrySetResult("x11:" + Xid(native).ToString("x", System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
                if (IsType(native, WaylandType()) == 0) throw new NativeBackendUnavailableException();
                var handle = GCHandle.Alloc(state);
                if (Export(native, ExportPointer, GCHandle.ToIntPtr(handle), DestroyPointer) == 0)
                {
                    handle.Free();
                    throw new NativeBackendUnavailableException();
                }
                state.Exported = true;
            }, cancellationToken).ConfigureAwait(false);
            string identifier = await state.Result.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!owner.IsAvailable || owner.Generation != state.Generation) throw new OwnerClosedException();
            transferred = true;
            return new Lease(this, state, identifier);
        }
        finally { if (!transferred) await ReleaseAsync(state).ConfigureAwait(false); }
    }

    private async ValueTask ReleaseAsync(ExportState state)
    {
        try
        {
            if (state.Exported)
                await owner.InvokeAsync(window =>
                {
                    nint native = GetWindow(window);
                    if (native != 0 && native == state.NativeWindow && owner.Generation == state.Generation) Unexport(native);
                }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException)
        { /* The destroyed GdkWindow releases its exported handle and callback. */ }
        finally { ExportGate.Release(); }
    }
    private sealed class Lease(Gtk3PortalWindowOwner owner, ExportState state, string identifier) : PortalParentLease
    {
        private int _disposed;
        public override string Identifier => identifier;
        public override ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.ReleaseAsync(state) : ValueTask.CompletedTask;
    }
    private sealed class ExportState
    {
        internal TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exported;
        internal nint NativeWindow;
        internal Guid Generation;
    }
    private static unsafe nint ExportPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Exported;
    private static unsafe nint DestroyPointer => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Destroyed;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Exported(nint window, nint value, nint context)
    {
        try
        {
            var state = (ExportState)GCHandle.FromIntPtr(context).Target!;
            var handle = Marshal.PtrToStringUTF8(value);
            if (string.IsNullOrWhiteSpace(handle)) state.Result.TrySetException(new NativeBackendUnavailableException());
            else state.Result.TrySetResult("wayland:" + handle);
        }
        catch { /* Native callbacks cannot unwind through GDK. */ }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroyed(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        ((ExportState)handle.Target!).Result.TrySetException(new OwnerClosedException());
        handle.Free();
    }
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_window")] private static partial nint GetWindow(nint widget);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")] private static partial int IsType(nint instance, nuint type);
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_x11_window_get_type")] private static partial nuint X11Type();
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_x11_window_get_xid")] private static partial nuint Xid(nint window);
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_wayland_window_get_type")] private static partial nuint WaylandType();
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_wayland_window_export_handle")] private static partial int Export(nint window, nint callback, nint data, nint destroy);
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_wayland_window_unexport_handle")] private static partial void Unexport(nint window);
}
