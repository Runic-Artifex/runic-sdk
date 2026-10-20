using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Runic.Application.Views.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.Wpf;

/// <summary>Native platform services for the application's own WPF windows.</summary>
public static class WpfPlatformServices
{
    /// <summary>Creates the platform services of a WPF window: file dialogs, clipboard and launcher owned by it.</summary>
    /// <param name="window">The window, on its own thread.</param>
    /// <returns>
    /// Services for a ViewModel that takes <see cref="IWindowPlatformServices"/>, as on Runic Desktop, where
    /// <c>AddRunicPlatformServices()</c> binds them to each Runic window.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Dialogs are modal to <paramref name="window"/>, and every call runs on its dispatcher, so the services can be
    /// used from any thread. Until the window has a native handle (after <see cref="Window.Show"/> or
    /// <see cref="WindowInteropHelper.EnsureHandle"/>) every service reports
    /// <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, as does a window that closed before this call. Once
    /// the window closes the services wait for the operations in progress, release the file access they acquired and
    /// report <see cref="PlatformUnavailableReason.OwnerClosed"/>; they need no disposal.
    /// </para>
    /// <para>
    /// Register them per window, for example <c>services.AddSingleton(WpfPlatformServices.ForWindow(mainWindow))</c>
    /// on the UI thread; each call creates services with their own lifetime. A <c>RunicViewHost</c> in the window
    /// presents a ViewModel that uses them like any other.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The call is not on the window's thread.</exception>
    public static IWindowPlatformServices ForWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        var services = new DesktopWindowPlatformServices();
        var owner = new WpfWindowOwner(window);
        void Bind()
        {
            if (owner.Attach()) services.BindWindows(owner);
        }
        void OnSourceInitialized(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            Bind();
        }
        void OnClosed(object? sender, EventArgs args)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.Closed -= OnClosed;
            owner.Close();
            // Waits for the operations in progress; a failure to release a provider resource has no caller to reach.
            _ = services.DisposeAsync().AsTask().ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        window.Closed += OnClosed;
        if (new WindowInteropHelper(window).Handle != 0) Bind();
        else window.SourceInitialized += OnSourceInitialized;
        return services;
    }

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
    /// The WPF counterpart of <see cref="DesktopPlatformServices.PresentAsync(DesktopNativeOwner, DesktopActivationContext?, CancellationToken)"/>
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
        return handle == 0 ? Closed : DesktopPlatformServices.PresentWindowsHandle(handle);
    }
}

// The native owner of a WPF window: its handle, used on the window's dispatcher until the window closes.
internal sealed class WpfWindowOwner(Window window) : INativeWindowOwner
{
    private nint _handle;
    private volatile bool _closed;

    public Guid Generation { get; } = Guid.NewGuid();

    public bool IsAvailable => !_closed && Volatile.Read(ref _handle) != 0 && !window.Dispatcher.HasShutdownStarted;

    // On the window's thread, once it has a handle.
    internal bool Attach()
    {
        Volatile.Write(ref _handle, new WindowInteropHelper(window).Handle);
        return IsAvailable;
    }

    internal void Close() => _closed = true;

    public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) throw new OwnerClosedException();
        var dispatcher = window.Dispatcher;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                // Check again on the window's thread: the window may have closed while the work was queued.
                var handle = Volatile.Read(ref _handle);
                if (!IsAvailable || handle == 0) throw new OwnerClosedException();
                action(handle);
            }, DispatcherPriority.Normal, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The dispatcher shut down before the work ran.
            throw new OwnerClosedException();
        }
    }
}
