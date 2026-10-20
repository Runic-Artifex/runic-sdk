using Runic.Desktop;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.Desktop;

/// <summary>Binds native platform services to one open embedded Desktop window.</summary>
/// <remarks>
/// <see cref="DesktopPlatformServices.ForWindow"/> and <c>services.AddRunicPlatformServices()</c> select the provider for the
/// running backend and create the window services with this owner. Pass it where an API asks for a window, such as
/// <c>WindowsElevationOptions.Owner</c> for the UAC prompt.
/// The owner belongs to the <see cref="DesktopWindow"/> it was created for: it becomes unavailable when that window
/// closes or the surface opens a replacement, and a replacement window needs a new owner. The native handle is
/// passed only to callbacks running on the window's native thread and must not outlive them.
/// <see cref="IsAvailable"/> is <see langword="false"/> for any window without native dispatch, for example an
/// installed-browser presentation or a custom window host without a native handle.
/// Create and dispose owner-bound services while the window is open.
/// </remarks>
public sealed class DesktopNativeOwner : INativeWindowOwner
{
    /// <summary>Creates an owner for <paramref name="window"/>.</summary>
    /// <param name="window">The Desktop window whose native thread runs provider callbacks.</param>
    public DesktopNativeOwner(DesktopWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Window = window;
    }

    /// <summary>Gets the Desktop window this owner belongs to.</summary>
    public DesktopWindow Window { get; }

    /// <inheritdoc />
    /// <remarks>Each owner has its own generation; an owner never follows a replacement window.</remarks>
    public Guid Generation { get; } = Guid.NewGuid();

    /// <inheritdoc />
    /// <remarks>
    /// <see langword="true"/> while the window is the surface's current embedded window, is open, and supports
    /// native dispatch.
    /// </remarks>
    public bool IsAvailable => Window.SupportsNativeDispatch && Window.NativeHandle != 0;

    /// <summary>Gets whether the caller is running on the window's native thread.</summary>
    public bool CheckAccess() => IsAvailable && Window.CheckNativeAccess();

    /// <inheritdoc />
    /// <exception cref="OwnerClosedException">The window closed or was replaced before the callback started.</exception>
    public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) throw new OwnerClosedException();
        int started = 0;
        try
        {
            await Window.InvokeNativeAsync(handle =>
            {
                // Check again on the native thread: the window may have closed while the work was queued.
                if (!IsAvailable || handle == 0) throw new OwnerClosedException();
                Volatile.Write(ref started, 1);
                action(handle);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (Volatile.Read(ref started) == 0
            && !cancellationToken.IsCancellationRequested
            && error is ObjectDisposedException or NotSupportedException or OperationCanceledException
            && !IsAvailable)
        {
            // The window closed between admission and dispatch; report it as an owner closure.
            throw new OwnerClosedException();
        }
    }
}
