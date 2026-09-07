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
public sealed class DesktopApplicationHost : IApplicationHost, IApplicationMainThreadHost
{
    private readonly DesktopApplicationHostOptions _options;
    private DesktopHost? _host;
    private DesktopSurface? _surface;
    private DesktopWindow? _window;
    private DesktopApplicationBridge? _bridge;
    private int _started;
    private TaskCompletionSource? _stopCompletion;

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
    public void Run(Func<Task> application) => DesktopEventLoop.Run(application);

    /// <inheritdoc />
    public async ValueTask StartAsync(
        ApplicationCompositionManifest manifest,
        ReadOnlyMemory<string> arguments,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A Desktop application host can start exactly once.");

        try
        {
            _host = await DesktopHost.StartAsync(_options.Host, cancellationToken).ConfigureAwait(false);
            _surface = await _host.CreateSurfaceAsync(_options.Surface, cancellationToken).ConfigureAwait(false);
            object? composition = _options.CreateBridgeSession?.Invoke() ?? RunicApplicationBridgeCompositionRegistry.CreateSession(services);
            if (composition is not null)
            {
                ApplicationBridgeSession session = composition as ApplicationBridgeSession
                    ?? throw new InvalidOperationException("The generated Application Bridge composition returned an invalid session.");
                _bridge = DesktopApplicationBridge.Attach(_surface, session, _options.Bridge);
            }
            if (_options.OpenWindow)
            {
                var windowOptions = _options.Window;
                // A native user close must drain presentation services before the
                // platform destroys its owner. Browser presentations have no owned
                // native services and cannot provide close interception.
                if (_bridge?.HasPresentationLifetimes == true && windowOptions.Browser == BrowserKind.Embedded
                    && windowOptions.PresentationPolicy != DesktopPresentationPolicy.EmbeddedThenBrowser)
                {
                    var confirm = windowOptions.ConfirmCloseAsync;
                    windowOptions = windowOptions with
                    {
                        ConfirmCloseAsync = async token =>
                        {
                            if (confirm is not null && !await confirm(token).ConfigureAwait(false)) return false;
                            if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
                            return true;
                        },
                    };
                }
                _window = await _surface.OpenWindowAsync(windowOptions, cancellationToken).ConfigureAwait(false);
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
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _stopCompletion, completion, null) is { } existing)
            return new(existing.Task);
        _ = StopCoreAsync(completion);
        return new(completion.Task);
    }

    private async Task StopCoreAsync(TaskCompletionSource completion)
    {
        System.Collections.Generic.List<Exception> failures = [];
        async Task Clean(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
        }
        if (_bridge is not null) await Clean(() => _bridge.DisposeAsync().AsTask()).ConfigureAwait(false);
        _bridge = null;
        if (_window is not null) await Clean(() => _window.CloseAsync().AsTask()).ConfigureAwait(false);
        _window = null;
        if (_surface is not null) await Clean(() => _surface.CloseAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
        _surface = null;
        if (_host is not null) await Clean(() => _host.DisposeAsync().AsTask()).ConfigureAwait(false);
        _host = null;
        if (failures.Count == 0) completion.SetResult();
        else completion.SetException(failures.Count == 1 ? failures[0] : new AggregateException(failures));
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
