namespace Runic.Desktop;

/// <summary>
/// The window a new Desktop window belongs to (<see cref="DesktopWindowOptions.Owner"/>): another
/// <see cref="DesktopWindow"/>, or a native window of the same toolkit, such as a WPF window.
/// </summary>
/// <remarks>
/// Create it with <see cref="FromWindow"/> for a Runic Desktop window, or with <see cref="FromNativeHandle"/> for a window
/// of another framework in the same process. The handle is read when the owned window opens.
/// </remarks>
public sealed class DesktopWindowOwner
{
    private readonly nint _handle;

    private DesktopWindowOwner(DesktopWindow? window, nint handle)
    {
        Window = window;
        _handle = handle;
    }

    /// <summary>An owner that is a Runic Desktop window.</summary>
    /// <param name="window">The owner. It must be an open embedded window when the owned window opens.</param>
    public static DesktopWindowOwner FromWindow(DesktopWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new DesktopWindowOwner(window, 0);
    }

    /// <summary>
    /// An owner that is a native window of another framework in this process: an <c>HWND</c> on Windows (for WPF,
    /// <c>new WindowInteropHelper(window).Handle</c>), an <c>NSWindow*</c> on macOS, and a <c>GtkWindow*</c> of the
    /// GTK version the Linux backend uses.
    /// </summary>
    /// <remarks>
    /// Runic Desktop does not track a native owner: closing or hiding it does not close or hide the owned window, as it
    /// does for a <see cref="DesktopWindow"/> owner.
    /// </remarks>
    /// <param name="handle">The native window handle. It must stay valid while the owned window is open.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is zero.</exception>
    public static DesktopWindowOwner FromNativeHandle(nint handle)
    {
        if (handle == 0) throw new ArgumentException("The owner's native handle must not be zero.", nameof(handle));
        return new DesktopWindowOwner(null, handle);
    }

    /// <summary>The Runic Desktop window that owns, or <see langword="null"/> for a native owner.</summary>
    public DesktopWindow? Window { get; }

    // The handle the owned window's host receives, or zero when the owner window is not open or has no native handle.
    internal nint ResolveHandle() => Window is { } window ? window.OwnerHandle : _handle;
}

/// <summary>Whether a window blocks its owner while it is open (<see cref="DesktopWindowOptions.Modality"/>).</summary>
/// <remarks>The set may grow in a minor release.</remarks>
public enum DesktopWindowModality
{
    /// <summary>The owner stays usable.</summary>
    None = 0,

    /// <summary>The owner takes no input until the window closes; the application's other windows stay usable.</summary>
    Window = 1,
}
