using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Navigation;

namespace Runic.Application.Views.Desktop;

/// <summary>
/// Opens Runic Windows in native Runic Desktop windows with the embedded WebView, falling back to an installed
/// browser. Start it with <see cref="Run(IServiceProvider, DesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
/// and open Windows with <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>:
/// <code>
/// return RunicDesktopHost.Run(provider, async host =&gt;
/// {
///     await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
///     await window.WaitForCloseAsync();
///     return 0;
/// });
/// </code>
/// Replacing <c>RunicDesktopHost</c> with <c>RunicCsWebUiHost</c> (package <c>Runic.Application.Views.CsWebUi</c>) runs the
/// same application on CS-WebUI.
/// </summary>
/// <remarks>
/// When the <c>RUNIC_APPLICATION_CLOSE_AFTER_OPEN</c> environment variable is <c>1</c>, every window opened through
/// this host is closed as soon as it has opened (and, with <see cref="DesktopHostOptions.WaitForConnection"/>, its
/// bridge has connected). Before closing, a warning is written to standard error (and logged as event 2002 when an
/// <c>ILoggerFactory</c> is registered) and <c>RUNIC_APPLICATION_OPENED=&lt;presentation&gt;</c>, such as
/// <c>Embedded</c> or <c>Chrome</c>, is written to standard output. It is an automation switch for applications
/// shaped like the <c>runic-app</c> template, which exit when their one window's
/// <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> returns; other applications may keep running or reopen windows.
/// </remarks>
public sealed class RunicDesktopHost : IRunicWindowHost
{
    /// <summary>
    /// The window options <see cref="WindowOptions"/> starts from: the embedded WebView, falling back to an installed
    /// browser (<see cref="DesktopPresentationPolicy.EmbeddedThenBrowser"/>).
    /// </summary>
    public static DesktopWindowOptions DefaultWindowOptions { get; } = new()
    {
        Browser = BrowserKind.Embedded,
        PresentationPolicy = DesktopPresentationPolicy.EmbeddedThenBrowser,
    };

    /// <summary>Creates a host that opens Windows from <paramref name="services"/> on <paramref name="desktop"/>.</summary>
    /// <remarks>
    /// <see cref="Run(IServiceProvider, DesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/> creates the host for
    /// you. Use this constructor inside <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/>
    /// or with a <see cref="DesktopHost"/> you started yourself, for example to set <see cref="WindowOptions"/>.
    /// </remarks>
    public RunicDesktopHost(IServiceProvider services, DesktopHost desktop)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(desktop);
        Services = services;
        Desktop = desktop;
    }

    /// <inheritdoc />
    public IServiceProvider Services { get; }

    /// <summary>The Runic Desktop host that presents the windows.</summary>
    public DesktopHost Desktop { get; }

    /// <summary>
    /// The Desktop options of every window. <see cref="RunicWindowOptions.Width"/> and
    /// <see cref="RunicWindowOptions.Height"/> replace their width and height when set.
    /// </summary>
    public DesktopWindowOptions WindowOptions { get; init; } = DefaultWindowOptions;

    /// <summary>
    /// The surface options of every window, or <see langword="null"/>, the default, to serve
    /// <see cref="RunicWindowOptions.ContentRoot"/> and open <see cref="RunicWindowOptions.StartPage"/>.
    /// </summary>
    public DesktopSurfaceOptions? SurfaceOptions { get; init; }

    /// <summary>Runs <paramref name="application"/> with the default <see cref="DesktopHostOptions"/>.</summary>
    /// <inheritdoc cref="Run(IServiceProvider, DesktopHostOptions, Func{RunicDesktopHost, Task{int}})"/>
    public static int Run(IServiceProvider services, Func<RunicDesktopHost, Task<int>> application) =>
        Run(services, new DesktopHostOptions(), application);

    /// <summary>
    /// Starts a Desktop host with <paramref name="options"/> and runs <paramref name="application"/> on the event
    /// loop this platform and backend need.
    /// </summary>
    /// <remarks>
    /// Call it from the process entry point before any <see langword="await"/> and return its result as the exit code;
    /// see <see cref="DesktopEventLoop.Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/>. Ctrl+C and SIGTERM close
    /// the open windows, so <see cref="RunicWindow{TViewModel}.WaitForCloseAsync"/> returns, and cancel
    /// <see cref="DesktopHost.ShutdownRequested"/>. Dispose <paramref name="services"/> after this method returns.
    /// </remarks>
    /// <param name="services">The application's service provider.</param>
    /// <param name="options">The Desktop host options, for example <c>new DesktopHostOptions().WithGtk4()</c>.</param>
    /// <param name="application">The application. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>.</returns>
    public static int Run(IServiceProvider services, DesktopHostOptions options, Func<RunicDesktopHost, Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(application);
        return DesktopEventLoop.Run(options, desktop => application(new RunicDesktopHost(services, desktop)));
    }

    IReadOnlyList<RunicWindowDiagnostic> IRunicWindowHost.Validate(RunicWindowOptions options) =>
        [.. Desktop.Validate(WindowOptionsFor(options)).Diagnostics.Select(ToWindowDiagnostic)];

    async ValueTask<IBridgeWindow> IRunicWindowHost.OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opening);
        var scope = opening.Scope;
        DesktopSurface? surface = null;
        DesktopBridgeTransport? transport = null;
        WindowContentSession? content = null;
        IDisposable? connectionBinding = null;
        DesktopBridgeWindow<TViewModel>? owner = null;
        try
        {
            var viewModel = opening.ViewModel;
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
            var surfaceOptions = SurfaceOptions ?? new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Directory(opening.Options.ResolveContentRoot(), opening.Options.StartPage),
            };
            surface = await Desktop.CreateSurfaceAsync(surfaceOptions, cancellationToken).ConfigureAwait(false);
            transport = new DesktopBridgeTransport(surface, loggerFactory?.CreateLogger(DesktopBridgeTransport.LogCategory));
            content = new WindowContentSession(transport,
                scope.ServiceProvider.GetService<IRunicViewLocator>(), rootModel: viewModel,
                modelContext: scope.ServiceProvider.GetService<IRunicModelContext>(),
                loggerFactory: loggerFactory);
            var session = content;
            connectionBinding = surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind == PresentationEventKind.Disconnected)
                    session.ReleaseConnection(invocation.Session.Id.ToString(CultureInfo.InvariantCulture));
            });
            owner = new DesktopBridgeWindow<TViewModel>(scope, surface, transport, content, connectionBinding, viewModel);
            var attachment = opening.AttachBridge(transport, content);
            try { owner.Attach(attachment); }
            catch { attachment.Dispose(); throw; }
            var presentation = await surface.OpenWindowAsync(WindowOptionsFor(opening.Options), cancellationToken)
                .ConfigureAwait(false);
            owner.SetPresentation(presentation);
            await CloseAfterOpenIfRequestedAsync(presentation, loggerFactory).ConfigureAwait(false);
            return owner;
        }
        catch (Exception error)
        {
            if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false);
            else
            {
                connectionBinding?.Dispose();
                content?.Dispose();
                transport?.Dispose();
                if (surface is not null) await surface.DisposeAsync().ConfigureAwait(false);
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            // One configuration exception on every host; the Desktop one stays as the inner exception.
            if (error is DesktopConfigurationException configuration)
                throw new RunicWindowConfigurationException([.. configuration.Diagnostics.Select(ToWindowDiagnostic)], configuration);
            throw;
        }
    }

    private DesktopWindowOptions WindowOptionsFor(RunicWindowOptions options) => WindowOptions with
    {
        Width = options.Width is { } width ? (uint)width : WindowOptions.Width,
        Height = options.Height is { } height ? (uint)height : WindowOptions.Height,
    };

    private static RunicWindowDiagnostic ToWindowDiagnostic(DesktopDiagnostic diagnostic) =>
        new(diagnostic.Code, diagnostic.Message, diagnostic.Remediation)
        {
            Severity = diagnostic.Severity == DesktopDiagnosticSeverity.Warning
                ? RunicWindowDiagnosticSeverity.Warning
                : RunicWindowDiagnosticSeverity.Error,
            Option = diagnostic.Option,
        };

    internal const string CloseAfterOpenEnvironmentVariable = "RUNIC_APPLICATION_CLOSE_AFTER_OPEN";

    // An automation contract like CS-WebUI's RUNIC_APPLICATION_SERVE_ONLY: report the presentation that opened,
    // then close it so the application's own WaitForCloseAsync returns.
    // The variable is inherited by child processes, so an active switch is always announced on standard error
    // and, when the application registered an ILoggerFactory, logged as event 2002.
    private static async ValueTask CloseAfterOpenIfRequestedAsync(DesktopWindow presentation, ILoggerFactory? loggerFactory)
    {
        if (Environment.GetEnvironmentVariable(CloseAfterOpenEnvironmentVariable) != "1") return;
        Console.Error.WriteLine(
            $"warning: {CloseAfterOpenEnvironmentVariable}=1 is set; Runic is closing the Desktop window it just opened.");
        Console.Error.Flush();
        if (loggerFactory is not null)
            DesktopLog.CloseAfterOpen(loggerFactory.CreateLogger(DesktopBridgeTransport.LogCategory), CloseAfterOpenEnvironmentVariable);
        Console.Out.WriteLine($"RUNIC_APPLICATION_OPENED={presentation.Browser}");
        Console.Out.Flush();
        await presentation.CloseAsync().ConfigureAwait(false);
    }
}
