using System;
using System.Threading;
using System.Threading.Tasks;
using Runic.Desktop;
using Runic.Application.Bridge;

namespace Runic.Application.Desktop;

/// <summary>Configures one Runic Application presentation through Runic Desktop.</summary>
public sealed record DesktopApplicationHostOptions
{
    /// <summary>Gets host-wide listener, security, service, and browser-discovery policy.</summary>
    public DesktopHostOptions Host { get; init; } = new();

    /// <summary>Gets content, namespace, request, and surface-security policy.</summary>
    public DesktopSurfaceOptions Surface { get; init; } = new();

    /// <summary>Gets browser or embedded-WebView presentation policy.</summary>
    public DesktopWindowOptions Window { get; init; } = new();

    /// <summary>Gets whether application start opens a platform presentation.</summary>
    public bool OpenWindow { get; init; } = true;

    /// <summary>Gets an optional presentation title applied after authentication.</summary>
    public string? Title { get; init; }

    /// <summary>Gets an optional explicit bridge-session factory; generated composition is used otherwise.</summary>
    public Func<ApplicationBridgeSession>? CreateBridgeSession { get; init; }

    /// <summary>Gets Application Bridge transport limits.</summary>
    public DesktopApplicationBridgeOptions Bridge { get; init; } = new();

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Host);
        ArgumentNullException.ThrowIfNull(Surface);
        ArgumentNullException.ThrowIfNull(Window);
        ArgumentNullException.ThrowIfNull(Bridge);
        if (Title is not null && string.IsNullOrWhiteSpace(Title))
            throw new ArgumentException("A configured Desktop title cannot be empty.", nameof(Title));
        Bridge.Validate();
    }
}

/// <summary>Composes Application-owned lifecycle and bridge state with Runic Desktop presentation hosting.</summary>
public sealed class DesktopApplicationHost : IApplicationHost
{
    private readonly DesktopApplicationHostOptions _options;
    private DesktopHost? _host;
    private DesktopSurface? _surface;
    private DesktopWindow? _window;
    private DesktopApplicationBridge? _bridge;
    private int _started;
    private int _stopped;

    /// <summary>Creates one host from immutable typed composition.</summary>
    public DesktopApplicationHost(DesktopApplicationHostOptions? options = null)
    {
        _options = options ?? new();
        _options.Validate();
    }

    /// <summary>Gets the active presentation surface after successful start.</summary>
    public DesktopSurface? Surface => _surface;

    /// <summary>Gets the active Desktop host for Application-owned additional surfaces and windows.</summary>
    public DesktopHost? Host => _host;

    /// <summary>Gets the active optional browser or WebView after successful start.</summary>
    public DesktopWindow? Window => _window;

    /// <inheritdoc />
    public async ValueTask StartAsync(
        ApplicationCompositionManifest manifest,
        ReadOnlyMemory<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A Desktop application host can start exactly once.");

        try
        {
            _host = await DesktopHost.StartAsync(_options.Host, cancellationToken).ConfigureAwait(false);
            _surface = await _host.CreateSurfaceAsync(_options.Surface, cancellationToken).ConfigureAwait(false);
            object? composition = _options.CreateBridgeSession?.Invoke() ?? RunicApplicationBridgeCompositionRegistry.CreateSession();
            if (composition is not null)
            {
                ApplicationBridgeSession session = composition as ApplicationBridgeSession
                    ?? throw new InvalidOperationException("The generated Application Bridge composition returned an invalid session.");
                _bridge = DesktopApplicationBridge.Attach(_surface, session, _options.Bridge);
            }
            if (_options.OpenWindow)
            {
                _window = await _surface.OpenWindowAsync(_options.Window, cancellationToken).ConfigureAwait(false);
                if (_options.Title is { } title)
                {
                    await _surface.RunJavaScriptAsync(
                        $"document.title = {EncodeJavaScriptString(title)};",
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        if (_window is { } window)
        {
            window.WaitForClose(cancellationToken);
            return ValueTask.CompletedTask;
        }
        return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (_window is not null) await _window.CloseAsync().ConfigureAwait(false);
        _window = null;
        if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        _bridge = null;
        if (_surface is not null) await _surface.CloseAsync(cancellationToken).ConfigureAwait(false);
        _surface = null;
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
        _host = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static string EncodeJavaScriptString(string value) =>
        $"\"{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(value)}\"";
}

/// <summary>Selects Runic Desktop as the presentation host for a Runic Application.</summary>
public static class DesktopApplicationBuilderExtensions
{
    /// <summary>Uses one typed Desktop composition.</summary>
    public static RunicApplicationBuilder UseDesktop(
        this RunicApplicationBuilder builder,
        DesktopApplicationHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseHost(new DesktopApplicationHost(options));
    }
}
