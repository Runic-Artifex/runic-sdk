using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Runic.Application.Views.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Wpf;

/// <summary>Native platform services for the application's own WPF windows.</summary>
[Experimental(WpfDiagnostics.Id, UrlFormat = WpfDiagnostics.UrlFormat)]
public static class WpfPlatformServices
{
    /// <summary>Brings a WPF window to the front, for example for a launch that another copy of the application forwarded.</summary>
    /// <param name="window">The window, on any thread; the work runs on its dispatcher.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window's dispatcher.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> when the window took the foreground;
    /// <see cref="PlatformFailureCode.PermissionDenied"/> when Windows kept another window in front and the taskbar
    /// button flashes instead; <see cref="PlatformUnavailableReason.OwnerClosed"/> for a closed window or a dispatcher
    /// that shut down.
    /// </returns>
    /// <remarks>
    /// The WPF counterpart of <see cref="PlatformServices.PresentAsync(DesktopNativeOwner, DesktopActivationContext?, CancellationToken)"/>
    /// for Runic Desktop windows. A hidden window, or one not shown yet, is shown with <see cref="Window.Show"/>, so WPF's
    /// <see cref="UIElement.Visibility"/> stays current; a minimized one is restored to its previous size. Windows lets a
    /// process take the foreground only when the user just interacted with it or the process in the foreground allows
    /// it: a launch forwarded by <c>ApplicationInstance.ClaimAsync</c> allows the running instance
    /// (<c>AllowSetForegroundWindow</c>), so call this promptly when the launch arrives.
    /// </remarks>
    public static async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(Window window, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = window.Dispatcher;
        if (dispatcher.HasShutdownStarted) return Closed;
        try
        {
            return await dispatcher.InvokeAsync(() => Present(window), DispatcherPriority.Normal, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && dispatcher.HasShutdownStarted)
        {
            return Closed;
        }
    }

    private static readonly PlatformResult<PlatformUnit> Closed =
        new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);

    private static PlatformResult<PlatformUnit> Present(Window window)
    {
        // .NET 10 runs on Windows 10 and later; the check satisfies the platform analyzer.
        if (!OperatingSystem.IsWindowsVersionAtLeast(8))
            return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        if (window.Visibility != Visibility.Visible || PresentationSource.FromVisual(window) is null)
        {
            // WPF refuses to show a window again after it closed.
            try { window.Show(); }
            catch (InvalidOperationException) { return Closed; }
        }
        var handle = new WindowInteropHelper(window).Handle;
        return handle == 0 ? Closed : PlatformServices.PresentWindowsHandle(handle);
    }
}
