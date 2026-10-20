using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

public static partial class DesktopPlatformServices
{
    /// <summary>
    /// Gets the bounds, maximized and full-screen state of an open Runic Window, or of a closed one as it last was, to
    /// save its <see cref="DesktopWindowState.Placement"/> for the next start.
    /// </summary>
    /// <param name="window">The Window, on any host. It must have opened; it may have closed since.</param>
    /// <returns>
    /// The state of the Window's embedded Desktop window: <see cref="DesktopWindow.State"/> of
    /// <see cref="DesktopBridgeWindow{TViewModel}.Window"/>. After the window has closed, until the Window is
    /// disposed, it is the last state before closing, so save it after <c>WaitForCloseAsync</c> returns.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A Window without a native owner reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, as
    /// <see cref="PresentAsync(IRunicWindow, DesktopActivationContext?, CancellationToken)"/> does: one on CS-WebUI or
    /// another host, one served under <c>RUNIC_APPLICATION_SERVE_ONLY=1</c>, and an installed browser opened after an
    /// embedded-window fallback. An embedded window whose host reports no state, a custom window host without
    /// <see cref="IDesktopWindowStateHost"/>, reports <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </para>
    /// <para>
    /// To restore it, return the saved placement from <see cref="RunicDesktopHost.Placement"/>. For every change while
    /// the window is open, subscribe to <see cref="DesktopWindow.StateChanged"/> of the presentation.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The Window has not opened.</exception>
    public static PlatformResult<DesktopWindowState> GetWindowState(IRunicWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var presentation = window.Host is IDesktopNativeOwnerSource { NativeOwnerOrNull: { } owner } ? owner.Window : null;
        return GetWindowState(presentation);
    }

    internal static PlatformResult<DesktopWindowState> GetWindowState(DesktopWindow? window)
    {
        if (window is null || window.Browser != BrowserKind.Embedded)
            return new PlatformResult<DesktopWindowState>.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        return window.State is { } state
            ? new PlatformResult<DesktopWindowState>.Success(state)
            : new PlatformResult<DesktopWindowState>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }
}
