using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Platform;

namespace Runic.Application.Views.Desktop;

public static partial class DesktopPlatformServices
{
    // ForWindowPresentation's presentation of each window, which every call with that window returns.
    private static readonly ConditionalWeakTable<DesktopWindow, RunicWindowPresentation> s_presentations = new();
    private static readonly object s_presentationsGate = new();

    /// <summary>
    /// Gets the <see cref="IRunicWindowPresentation"/> of a <see cref="DesktopWindow"/> the application opened itself,
    /// for ViewModels without a Window scope, as <c>WpfPlatformServices.ForWindowPresentation</c> does for a WPF window.
    /// </summary>
    /// <param name="window">A window the application opened with <see cref="DesktopSurface.OpenWindowAsync"/>.</param>
    /// <returns>
    /// The same object for every call with <paramref name="window"/>. Its requests give the results that the
    /// presentation of a <see cref="RunicDesktopHost"/> Window gives for the same window.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Pass it to the ViewModels of that window, for example through the ViewModel's constructor. A ViewModel that a DI
    /// scope with <c>AddRunicWindowService()</c> resolves takes the scope's own presentation instead, once
    /// <see cref="AttachWindowServices"/> attached the scope to the window.
    /// </para>
    /// <para>
    /// The activity and color-scheme changes are raised on the thread that reports them, one at a time and in order. A
    /// handler that throws is logged and does not stop the others. Once the window closes, requests report
    /// <see cref="PlatformUnavailableReason.OwnerClosed"/> and the activity is <see cref="RunicWindowActivity.Closed"/>.
    /// Any thread may call it.
    /// </para>
    /// </remarks>
    public static IRunicWindowPresentation ForWindowPresentation(DesktopWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (s_presentationsGate)
        {
            if (s_presentations.TryGetValue(window, out var existing)) return existing;
            var owner = new DesktopNativeOwner(window);
            var backend = new DesktopWindowPresentation(window, () => owner);
            var presentation = RunicWindowPresentation.Wrap(backend, static () => null, static () => TraceFallbackLogger.Instance);
            s_presentations.Add(window, presentation);
            void OnClosed(object? sender, EventArgs args)
            {
                window.Closed -= OnClosed;
                backend.Detach();
                presentation.Close();
            }
            window.Closed += OnClosed;
            if (!window.IsOpen) OnClosed(window, EventArgs.Empty);
            return presentation;
        }
    }

    /// <summary>
    /// Gives the per-window services of a DI scope the window and content session the application opened itself, as
    /// <see cref="RunicDesktopHost"/> does for the Windows it opens.
    /// </summary>
    /// <param name="window">The open window, embedded or an installed browser after a fallback.</param>
    /// <param name="windowServices">
    /// The window's services: a scope of a provider with <c>AddRunicWindowService()</c> and, for dialogs and the
    /// clipboard, <c>AddRunicPlatformServices()</c>. The application creates and disposes it.
    /// </param>
    /// <param name="content">
    /// The window's content session on a <see cref="DesktopBridgeTransport"/> of <paramref name="window"/>'s surface, or
    /// <see langword="null"/>. Without it the locale, the page connections and the appearance push are not attached, and
    /// presentation changes are raised on the thread that reports them.
    /// </param>
    /// <returns>The binding, which ends when the window closes or when it is disposed.</returns>
    /// <remarks>
    /// <para>
    /// Call it once the window is open. ViewModels that the scope resolved before keep their services: the scope's
    /// <see cref="IRunicWindowPresentation"/>, <see cref="IRunicWindowLocale"/>, <see cref="IRunicConnections"/> and
    /// platform services are the objects that are attached, so they follow the window from then on. It attaches:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="IRunicWindowPresentation"/>, with the results of a <see cref="RunicDesktopHost"/> Window; its changes arrive in turns of the session's model context.</description></item>
    /// <item><description><see cref="IRunicWindowLocale"/>, which follows and sets the session's locale; the native layout direction and the language of the pages the surface serves itself follow it.</description></item>
    /// <item><description>The <c>appearance/1</c> push of the desktop's appearance (<see cref="IDesktopSettings"/>), when only this computer can reach the window's pages, as for a <see cref="RunicDesktopHost"/> Window: never for a host with <see cref="DesktopNetworkExposure.AllInterfaces"/>.</description></item>
    /// <item><description><see cref="IRunicConnections"/>, which lists the session's page connections.</description></item>
    /// <item><description><see cref="DesktopWindowPlatformServices"/> (<see cref="IWindowPlatformServices"/>: dialogs, the clipboard, file access), bound to the window's <see cref="DesktopNativeOwner"/>.</description></item>
    /// <item><description><see cref="IRunicWindowContext.RequestCloseAsync"/>, which calls <see cref="DesktopWindow.RequestCloseAsync"/>, so the window's <see cref="DesktopWindowOptions.ConfirmCloseAsync"/> decides. <see cref="IRunicWindowContext.Window"/> stays <see langword="null"/>: there is no <see cref="IRunicWindow"/>.</description></item>
    /// </list>
    /// <para>
    /// The layout direction follows the locale unless the window opened with a direction of its own, other than
    /// <see cref="DesktopLayoutDirection.Default"/> and other than the one of the session's locale. The web view's
    /// language stays the one <see cref="DesktopWindowOptions.Language"/> gave it. A scope without
    /// <c>AddRunicWindowService()</c> attaches the services it has; <see cref="DesktopWindowServices.Presentation"/>
    /// still presents the window. It never disposes the scope, the session or the window. Any thread may call it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The window is closed, or the scope is already bound to a Window that <c>OpenWindowAsync</c> opened or attached to
    /// a window.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="content"/> does not serve <paramref name="window"/>'s surface.</exception>
    public static DesktopWindowServices AttachWindowServices(DesktopWindow window, IServiceProvider windowServices,
        WindowContentSession? content = null) => AttachWindowServicesCore(window, windowServices, content, PresentAsync);

    // Tests replace the platform's presentation.
    internal static DesktopWindowServices AttachWindowServicesCore(DesktopWindow window, IServiceProvider windowServices,
        WindowContentSession? content,
        Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> present)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windowServices);
        if (!window.IsOpen) throw new InvalidOperationException("The window is closed.");
        if (content is not null &&
            (content.Dispatcher.Transport is not DesktopBridgeTransport transport || !ReferenceEquals(transport.Surface, window.Surface)))
            throw new ArgumentException("The content session does not serve the window's surface: create it on a DesktopBridgeTransport of that surface.",
                nameof(content));
        return new DesktopWindowServices(window, windowServices, content, present);
    }
}

/// <summary>
/// The per-window services <see cref="DesktopPlatformServices.AttachWindowServices"/> attached to a
/// <see cref="DesktopWindow"/> the application opened itself.
/// </summary>
/// <remarks>
/// The binding ends when the window closes or when it is disposed: the presentation then reports
/// <see cref="PlatformUnavailableReason.OwnerClosed"/> and <see cref="RunicWindowActivity.Closed"/>, the locale and the
/// appearance no longer reach the window, the platform services stop, and <see cref="IRunicWindowContext.RequestCloseAsync"/>
/// throws. A scope cannot be attached again; a new window takes a new scope. Disposing it never closes the window and
/// never disposes the scope or the content session.
/// </remarks>
public sealed class DesktopWindowServices : IAsyncDisposable
{
    private readonly RunicWindowContext? _context;
    private readonly DesktopWindowPresentation _backend;
    private readonly RunicWindowPresentation _presentation;
    private readonly DesktopWindowPlatformServices? _platformServices;
    private readonly List<IDisposable> _follows = [];
    private readonly object _gate = new();
    private Task? _ended;

    internal DesktopWindowServices(DesktopWindow window, IServiceProvider services, WindowContentSession? content,
        Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> present)
    {
        Window = window;
        NativeOwner = new DesktopNativeOwner(window);
        var owner = NativeOwner;
        _backend = new DesktopWindowPresentation(window, () => owner) { Present = present };
        _context = services.GetService(typeof(RunicWindowContext)) as RunicWindowContext;
        var attached = false;
        try
        {
            _presentation = _context is not null
                ? _context.AttachExternal(_backend, content, window.RequestCloseAsync)
                : RunicWindowPresentation.Wrap(_backend, () => content?.ModelContext, () => content?.Logger ?? TraceFallbackLogger.Instance);
            attached = true;
            _platformServices = services.GetService(typeof(DesktopWindowPlatformServices)) as DesktopWindowPlatformServices;
            _platformServices?.Bind(owner);
        }
        catch
        {
            // Only a scope this call attached is detached again; one attached before keeps its window.
            _backend.Detach();
            if (attached) _context?.DetachExternal();
            throw;
        }
        if (content is not null)
        {
            var logger = (services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger("Runic.Application.Views.Desktop")
                ?? (ILogger)NullLogger.Instance;
            (services.GetService(typeof(RunicConnections)) as RunicConnections)?.Bind(content);
            var surface = window.Surface;
            _follows.Add(DesktopWindowLocaleFollow.FollowPageLanguage(content, surface));
            // A direction the application gave the window for another reason than its locale stays, as WindowOptions'
            // own direction does under RunicDesktopHost.
            if (window.LayoutDirection == DesktopLayoutDirection.Default ||
                window.LayoutDirection == DesktopWindowLocaleFollow.DirectionOf(content.Locale))
                _follows.Add(DesktopWindowLocaleFollow.FollowDirection(content, window, logger));
            // appearance/1, only when nothing but this computer can reach the window's pages (DesktopWindowLocality).
            if (DesktopWindowLocality.IsLocal(surface.Host, surface, serveOnly: false))
                _follows.Add(DesktopAppearanceFeed.For(services).Add(content, logger));
        }
        window.Closed += OnClosed;
        // A window that closed meanwhile raised Closed before the handler was added.
        if (!window.IsOpen) _ = EndAsync();
    }

    /// <summary>Gets the window the services were attached to.</summary>
    public DesktopWindow Window { get; }

    /// <summary>
    /// Gets the window's presentation: the scope's own <see cref="IRunicWindowPresentation"/>, which its ViewModels
    /// hold, or, for a scope without <c>AddRunicWindowService()</c>, one of this binding.
    /// </summary>
    public IRunicWindowPresentation Presentation => _presentation;

    /// <summary>Gets the window's native owner, which the platform services are bound to.</summary>
    public DesktopNativeOwner NativeOwner { get; }

    private void OnClosed(object? sender, EventArgs args) => _ = EndAsync();

    private Task EndAsync()
    {
        lock (_gate)
        {
            if (_ended is not null) return _ended;
            _ended = EndCoreAsync();
            return _ended;
        }
    }

    private async Task EndCoreAsync()
    {
        Window.Closed -= OnClosed;
        foreach (var follow in _follows) follow.Dispose();
        _backend.Detach();
        if (_context is not null) _context.DetachExternal();
        else _presentation.Close();
        if (_platformServices is not null) await _platformServices.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Detaches the services from the window early, as its close does.</summary>
    /// <returns>A task that completes when the platform services stopped.</returns>
    public ValueTask DisposeAsync() => new(EndAsync());
}
