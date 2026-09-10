using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Gtk4;

/// <summary>Exports XDG portal parents from a verified GTK 4 window owner.</summary>
/// <remarks>
/// The supplied owner must dispatch GTK 4 window handles. This class keeps every GTK and
/// GDK operation inside that owner's callback, so callers never observe a raw native handle.
/// </remarks>
public sealed partial class Gtk4PortalWindowOwner(INativePickerOwner owner) : IPortalWindowOwner
{
    /// <inheritdoc />
    public Guid Generation => owner.Generation;

    /// <inheritdoc />
    public bool IsAvailable => owner.IsAvailable;

    /// <inheritdoc />
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) =>
        owner.InvokeAsync(action, cancellationToken);

    /// <summary>Requests presentation on this verified owner using the desktop's activation context.</summary>
    /// <remarks>The compositor decides whether to grant focus. Never sets process-wide environment variables.</remarks>
    public ValueTask PresentAsync(DesktopActivationContext? context = null, CancellationToken cancellationToken = default) =>
        owner.InvokeAsync(window =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (window == 0 || !owner.IsAvailable) throw new OwnerClosedException();
            if ((context?.ActivationToken ?? context?.StartupId) is { } startupId) SetStartupId(window, startupId);
            Present(window);
        }, cancellationToken);

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_set_startup_id", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void SetStartupId(nint window, string startupId);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_present")]
    private static partial void Present(nint window);

    /// <inheritdoc />
    public async ValueTask<PortalParentLease> ExportParentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("GTK 4 portal parents are available only on Linux.");
        }
        if (!owner.IsAvailable)
        {
            throw new OwnerClosedException();
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var exportTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        WaylandExport? wayland = null;
        PortalParentLease? exported = null;
        await owner.InvokeAsync(window =>
        {
            if (window == 0 || !owner.IsAvailable)
            {
                throw new OwnerClosedException();
            }

            var surface = NativeGetSurface(window);
            if (surface == 0)
            {
                throw new NativeBackendUnavailableException();
            }
            var display = SurfaceGetDisplay(surface);
            if (display == 0)
            {
                throw new NativeBackendUnavailableException();
            }

            if (IsInstanceOf(display, X11DisplayType()) != 0)
            {
                var xid = X11SurfaceGetXid(surface);
                if (xid == 0)
                {
                    throw new NativeBackendUnavailableException();
                }
                exported = new X11PortalParentLease($"x11:{xid:x}");
                return;
            }

            if (IsInstanceOf(display, WaylandDisplayType()) == 0)
            {
                throw new NativeBackendUnavailableException();
            }

            wayland = new WaylandExport(owner, exportTimeout.Token);
            var handle = GCHandle.Alloc(wayland);
            wayland.SetHandle(handle);
            try
            {
                ExportWaylandHandle(surface, WaylandExportPointer, GCHandle.ToIntPtr(handle), WaylandExportDestroyedPointer);
            }
            catch
            {
                wayland.DisposeHandle();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (exported is not null)
        {
            return exported;
        }
        if (wayland is null)
        {
            throw new NativeBackendUnavailableException();
        }
        try
        {
            return await wayland.Result.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new NativeBackendUnavailableException();
        }
    }

    private static unsafe nint WaylandExportPointer => (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&WaylandHandleExported;
    private static unsafe nint WaylandExportDestroyedPointer => (nint)(delegate* unmanaged[Cdecl]<nint, void>)&WaylandExportDestroyed;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WaylandHandleExported(nint surface, nint handle, nint context)
    {
        var export = (WaylandExport)GCHandle.FromIntPtr(context).Target!;
        try
        {
            var identifier = Marshal.PtrToStringUTF8(handle);
            if (string.IsNullOrWhiteSpace(identifier))
            {
                export.Fail(new NativeBackendUnavailableException());
                return;
            }
            if (export.IsCancellationRequested)
            {
                DropWaylandHandle(surface, identifier);
                return;
            }
            var lease = new WaylandPortalParentLease(owner: export.Owner, surface, identifier);
            if (!export.Succeed(lease))
            {
                // Cancellation can win immediately after the earlier check. This
                // callback owns the still-valid GDK surface, so release it here.
                DropWaylandHandle(surface, identifier);
            }
        }
        catch (Exception error)
        {
            export.Fail(error);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WaylandExportDestroyed(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        if (handle.Target is WaylandExport export)
        {
            export.DisposeHandle();
        }
        else
        {
            handle.Free();
        }
    }

    private sealed class WaylandExport
    {
        private readonly CancellationTokenRegistration _cancellation;
        private GCHandle _handle;

        internal WaylandExport(INativePickerOwner owner, CancellationToken cancellationToken)
        {
            Owner = owner;
            _cancellation = cancellationToken.Register(static state => ((WaylandExport)state!).Cancel(), this);
        }

        internal TaskCompletionSource<PortalParentLease> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal INativePickerOwner Owner { get; }
        private int _canceled;
        internal bool IsCancellationRequested => Volatile.Read(ref _canceled) != 0;

        internal void SetHandle(GCHandle handle) => _handle = handle;

        internal void Cancel()
        {
            Volatile.Write(ref _canceled, 1);
            Result.TrySetCanceled();
        }

        internal bool Succeed(PortalParentLease lease) => Result.TrySetResult(lease);

        internal void Fail(Exception error) => Result.TrySetException(error);

        internal void DisposeHandle()
        {
            _cancellation.Dispose();
            Result.TrySetException(new NativeBackendUnavailableException());
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }
        }
    }

    private sealed class X11PortalParentLease(string identifier) : PortalParentLease
    {
        public override string Identifier { get; } = identifier;
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaylandPortalParentLease(INativePickerOwner owner, nint surface, string handle) : PortalParentLease
    {
        private int _disposed;

        public override string Identifier => $"wayland:{handle}";

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            if (!owner.IsAvailable)
            {
                return;
            }
            try
            {
                await owner.InvokeAsync(window =>
                {
                    var currentSurface = NativeGetSurface(window);
                    if (currentSurface == surface)
                    {
                        DropWaylandHandle(currentSurface, handle);
                    }
                }).ConfigureAwait(false);
            }
            catch (OwnerClosedException)
            {
                // GTK tears down a destroyed surface. There is no valid native object left to release.
            }
            catch (ObjectDisposedException) when (!owner.IsAvailable)
            {
                // A concurrent presentation shutdown has the same native lifetime.
            }
        }
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_native_get_surface")]
    private static partial nint NativeGetSurface(nint native);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_surface_get_display")]
    private static partial nint SurfaceGetDisplay(nint surface);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")]
    private static partial int IsInstanceOf(nint instance, nuint type);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_x11_display_get_type")]
    private static partial nuint X11DisplayType();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_x11_surface_get_xid")]
    private static partial ulong X11SurfaceGetXid(nint surface);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_wayland_display_get_type")]
    private static partial nuint WaylandDisplayType();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_wayland_toplevel_export_handle")]
    private static partial void ExportWaylandHandle(nint surface, nint callback, nint context, nint destroy);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gdk_wayland_toplevel_drop_exported_handle", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void DropWaylandHandle(nint surface, string handle);
}
