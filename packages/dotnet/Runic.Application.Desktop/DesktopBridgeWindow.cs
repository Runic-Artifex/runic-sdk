using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Desktop;

namespace Runic.Application.Views.Desktop;

/// <summary>Owns one Desktop surface, presentation, ViewModel scope, and Views session.</summary>
public sealed class DesktopBridgeWindow<TViewModel> : IBridgeWindow where TViewModel : class
{
    private readonly object _closeGate = new();
    private readonly AsyncServiceScope _scope;
    private readonly DesktopSurface _surface;
    private readonly DesktopBridgeTransport _transport;
    private readonly WindowContentSession _content;
    private readonly IDisposable _connectionBinding;
    private IDisposable? _attachment;
    private DesktopWindow? _presentation;
    private DesktopNativeOwner? _nativeOwner;
    private Task<BridgeWindowCloseResult>? _close;
    private Task? _completion;
    private bool _finalized;

    internal DesktopBridgeWindow(AsyncServiceScope scope, DesktopSurface surface, DesktopBridgeTransport transport,
        WindowContentSession content, IDisposable connectionBinding, TViewModel viewModel)
    {
        _scope = scope;
        _surface = surface;
        _transport = transport;
        _content = content;
        _connectionBinding = connectionBinding;
        ViewModel = viewModel;
    }

    /// <summary>The window's scoped root ViewModel.</summary>
    public TViewModel ViewModel { get; }
    /// <summary>The Desktop surface that hosts the window's web content.</summary>
    public DesktopSurface Surface => _surface;
    /// <summary>The opened Desktop window.</summary>
    /// <exception cref="InvalidOperationException">The presentation has not opened yet.</exception>
    public DesktopWindow Presentation => Volatile.Read(ref _presentation) ??
        throw new InvalidOperationException("The Desktop presentation has not opened.");
    /// <summary>The native platform-service owner of the opened presentation.</summary>
    /// <remarks>
    /// Pass it to the platform provider for the window's backend for file dialogs, file launchers and clipboard
    /// access. It is available while the embedded window is open. A presentation without native dispatch, such
    /// as an installed browser after an embedded-window fallback, still has an owner, but its
    /// <see cref="DesktopNativeOwner.IsAvailable"/> is <see langword="false"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The presentation has not opened yet.</exception>
    public DesktopNativeOwner NativeOwner => Volatile.Read(ref _nativeOwner) ??
        throw new InvalidOperationException("The Desktop presentation has not opened.");

    internal void Attach(IDisposable attachment)
    {
        lock (_closeGate)
        {
            if (_attachment is not null || _close is not null)
                throw new InvalidOperationException("The Window Bridge is already attached or closing.");
            _attachment = attachment;
        }
    }

    internal void SetPresentation(DesktopWindow presentation)
    {
        lock (_closeGate)
        {
            if (_presentation is not null || _close is not null)
                throw new InvalidOperationException("The Desktop presentation is already open or closing.");
            // Lock-free readers see the owner no later than the presentation.
            Volatile.Write(ref _nativeOwner, new DesktopNativeOwner(presentation));
            Volatile.Write(ref _presentation, presentation);
        }
    }

    /// <inheritdoc />
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_closeGate) return new(_close ??= BeginCloseAsync(timeout));
    }

    /// <summary>Closes the window immediately and waits until its resources are released.</summary>
    public async ValueTask DisposeAsync()
    {
        var result = await CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        await result.Completion.ConfigureAwait(false);
    }

    private async Task<BridgeWindowCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        var errors = new List<Exception>();
        Capture(_connectionBinding.Dispose, errors);
        if (_attachment is not null) Capture(_attachment.Dispose, errors);

        BridgeWindowCloseResult result;
        try { result = await _content.BeginCloseAsync(timeout).ConfigureAwait(false); }
        catch (Exception error)
        {
            errors.Add(error);
            await FinalizeAsync(errors).ConfigureAwait(false);
            throw new AggregateException(errors);
        }

        if (result.Drained)
        {
            await FinalizeAsync(errors).ConfigureAwait(false);
            return new(true, 0, CompletionFor(errors));
        }

        // A visible closed window must not leave command routes active. The
        // surface stays open solely so accepted operations can finish.
        Capture(_transport.Dispose, errors);
        // The visible presentation closes promptly, while the scope remains
        // alive until operations accepted before close reach a terminal result.
        if (_presentation is not null)
            await CaptureAsync(_presentation.CloseAsync, errors).ConfigureAwait(false);
        Task completion;
        lock (_closeGate) completion = _completion ??= DrainThenFinalizeAsync(errors);
        return new(false, result.RemainingOperations, completion);
    }

    private async Task DrainThenFinalizeAsync(List<Exception> errors)
    {
        try { _ = await _content.BeginCloseAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        await FinalizeAsync(errors).ConfigureAwait(false);
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private async Task FinalizeAsync(List<Exception> errors)
    {
        lock (_closeGate)
        {
            if (_finalized) return;
            _finalized = true;
        }
        Capture(_content.Dispose, errors);
        Capture(_transport.Dispose, errors);
        if (_presentation is not null)
            await CaptureAsync(_presentation.DisposeAsync, errors).ConfigureAwait(false);
        await CaptureAsync(_surface.DisposeAsync, errors).ConfigureAwait(false);
        await CaptureAsync(_scope.DisposeAsync, errors).ConfigureAwait(false);
    }

    private static Task CompletionFor(List<Exception> errors) => errors.Count switch
    {
        0 => Task.CompletedTask,
        1 => Task.FromException(errors[0]),
        _ => Task.FromException(new AggregateException(errors)),
    };

    private static void Capture(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }

    private static async Task CaptureAsync(Func<ValueTask> action, List<Exception> errors)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }
}

/// <summary>Opens Desktop Views windows from a service provider.</summary>
public static class DesktopBridgeWindowExtensions
{
    /// <summary>Creates an application Window in a new scope, then opens its Desktop presentation.</summary>
    /// <remarks>
    /// When the <c>RUNIC_APPLICATION_CLOSE_AFTER_OPEN</c> environment variable is <c>1</c>, the presentation is
    /// closed as soon as it has opened (and, with <see cref="DesktopHostOptions.WaitForConnection"/>, its bridge has
    /// connected), after <c>RUNIC_APPLICATION_OPENED=&lt;presentation&gt;</c>, such as <c>Embedded</c> or
    /// <c>Chrome</c>, is written to standard output. <see cref="DesktopWindow.WaitForClose"/> then returns, so an
    /// unchanged application exits normally. Test harnesses use this to start an application without a user.
    /// </remarks>
    public static async ValueTask<TWindow> OpenDesktopWindowAsync<TWindow, TViewModel>(
        this IServiceProvider services,
        DesktopHost desktop,
        DesktopSurfaceOptions surfaceOptions,
        Func<DesktopBridgeWindow<TViewModel>, TWindow> createWindow,
        DesktopWindowOptions? windowOptions = null,
        CancellationToken cancellationToken = default)
        where TWindow : RunicWindow<TViewModel>, IAsyncDisposable
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(surfaceOptions);
        ArgumentNullException.ThrowIfNull(createWindow);
        // Fail before a surface or presentation exists, naming the missing registration.
        var registrationErrors = GetRegistrationDiagnostics<TViewModel>(services)
            .Where(static diagnostic => diagnostic.Severity == DesktopDiagnosticSeverity.Error)
            .ToList();
        if (registrationErrors.Count > 0)
        {
            LogDiagnostics(services, registrationErrors);
            throw new DesktopConfigurationException(registrationErrors);
        }

        var scope = services.CreateAsyncScope();
        DesktopSurface? surface = null;
        DesktopBridgeTransport? transport = null;
        WindowContentSession? content = null;
        IDisposable? connectionBinding = null;
        DesktopBridgeWindow<TViewModel>? owner = null;
        TWindow? applicationWindow = null;
        try
        {
            var viewModel = scope.ServiceProvider.GetRequiredService<TViewModel>();
            var attachWithContent = scope.ServiceProvider.GetService<
                Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>();
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
            surface = await desktop.CreateSurfaceAsync(surfaceOptions, cancellationToken).ConfigureAwait(false);
            transport = new DesktopBridgeTransport(surface, loggerFactory?.CreateLogger(DesktopBridgeTransport.LogCategory));
            content = new WindowContentSession(transport,
                scope.ServiceProvider.GetService<IRunicViewLocator>(), rootModel: viewModel,
                modelContext: scope.ServiceProvider.GetService<IRunicModelContext>(),
                loggerFactory: loggerFactory);
            connectionBinding = surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind == PresentationEventKind.Disconnected)
                    content.ReleaseConnection(invocation.Session.Id.ToString(CultureInfo.InvariantCulture));
            });
            owner = new DesktopBridgeWindow<TViewModel>(scope, surface, transport, content, connectionBinding, viewModel);
            applicationWindow = createWindow(owner)
                ?? throw new InvalidOperationException("The Window factory returned null.");
            if (!ReferenceEquals(applicationWindow.DataContext, viewModel))
                throw new InvalidOperationException("The application Window must use its scoped ViewModel as DataContext.");
            var attachment = attachWithContent is not null
                ? attachWithContent(transport, content, viewModel)
                : scope.ServiceProvider.GetRequiredService<
                    Func<IBridgeTransport, TViewModel, IDisposable>>()(transport, viewModel);
            try { owner.Attach(attachment); }
            catch { attachment.Dispose(); throw; }
            var presentation = await surface.OpenWindowAsync(windowOptions, cancellationToken).ConfigureAwait(false);
            owner.SetPresentation(presentation);
            await CloseAfterOpenIfRequestedAsync(presentation).ConfigureAwait(false);
            return applicationWindow;
        }
        catch
        {
            if (owner is not null)
            {
                try { if (applicationWindow is not null) await applicationWindow.DisposeAsync().ConfigureAwait(false); }
                finally { await owner.DisposeAsync().ConfigureAwait(false); }
            }
            else
            {
                connectionBinding?.Dispose();
                content?.Dispose();
                transport?.Dispose();
                if (surface is not null) await surface.DisposeAsync().ConfigureAwait(false);
                await scope.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    internal const string CloseAfterOpenEnvironmentVariable = "RUNIC_APPLICATION_CLOSE_AFTER_OPEN";

    // An automation contract like CS-WebUI's RUNIC_APPLICATION_SERVE_ONLY: report the presentation that opened,
    // then close it so the application's own WaitForClose returns.
    private static async ValueTask CloseAfterOpenIfRequestedAsync(DesktopWindow presentation)
    {
        if (Environment.GetEnvironmentVariable(CloseAfterOpenEnvironmentVariable) != "1") return;
        Console.Out.WriteLine($"RUNIC_APPLICATION_OPENED={presentation.Browser}");
        Console.Out.Flush();
        await presentation.CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Checks at startup, before any window opens, that <paramref name="services"/> can open a Window for
    /// <typeparamref name="TViewModel"/> and that <paramref name="desktop"/> can present it with
    /// <paramref name="windowOptions"/>.
    /// </summary>
    /// <remarks>
    /// The result combines the generated Bridge registration (an error when missing) and the ViewModel
    /// registration (a warning when the container does not confirm it through
    /// <see cref="IServiceProviderIsService"/>; containers without that service are not checked) with
    /// <see cref="DesktopHost.Validate"/>. Registration failures are logged through the service provider's
    /// <see cref="ILoggerFactory"/>. Call <see cref="DesktopValidationResult.ThrowIfInvalid"/> to stop at startup.
    /// </remarks>
    public static DesktopValidationResult ValidateDesktopWindow<TViewModel>(
        this IServiceProvider services,
        DesktopHost desktop,
        DesktopWindowOptions? windowOptions = null)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(desktop);
        var registrations = GetRegistrationDiagnostics<TViewModel>(services);
        LogDiagnostics(services, registrations);
        var presentation = desktop.Validate(windowOptions);
        return new DesktopValidationResult([.. registrations, .. presentation.Diagnostics]);
    }

    // IServiceProviderIsService answers from registrations without constructing anything. A container
    // without it cannot be inspected, so resolution reports a missing registration as before. Only a
    // missing Bridge fails: a container can resolve a ViewModel it does not report, so an unconfirmed
    // ViewModel is a warning.
    private static List<DesktopDiagnostic> GetRegistrationDiagnostics<TViewModel>(IServiceProvider services)
        where TViewModel : class
    {
        List<DesktopDiagnostic> diagnostics = [];
        if (services.GetService(typeof(IServiceProviderIsService)) is not IServiceProviderIsService registered)
            return diagnostics;
        var viewModel = typeof(TViewModel).FullName ?? typeof(TViewModel).Name;
        if (!registered.IsService(typeof(TViewModel)))
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.NotFound,
                ViewModelNotRegisteredCode,
                $"The container does not report the ViewModel {viewModel} as registered; resolving it may fail.",
                Retryable: false,
                Remediation: $"Register it before building the service provider, for example services.AddScoped<{typeof(TViewModel).Name}>().")
            {
                Severity = DesktopDiagnosticSeverity.Warning,
            });
        }
        if (!registered.IsService(typeof(Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>)) &&
            !registered.IsService(typeof(Func<IBridgeTransport, TViewModel, IDisposable>)))
        {
            diagnostics.Add(new DesktopDiagnostic(
                DesktopErrorCategory.NotFound,
                BridgeNotRegisteredCode,
                $"No generated Bridge is registered for {viewModel}.",
                Retryable: false,
                Remediation: "Call services.AddRunicViews() (or AddRunicBridges()) from the generated " +
                    "<Project>.RunicBridgeComposition class. If it is missing or has no Bridge for this ViewModel, " +
                    "check that the project references Runic.Application.Desktop and that the ViewModel is a public, " +
                    "top-level class implementing INotifyPropertyChanged."));
        }
        return diagnostics;
    }

    private static void LogDiagnostics(IServiceProvider services, List<DesktopDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0 || services.GetService(typeof(ILoggerFactory)) is not ILoggerFactory loggers)
            return;
        var logger = loggers.CreateLogger(DesktopBridgeTransport.LogCategory);
        foreach (var diagnostic in diagnostics.Where(static item => item.Severity == DesktopDiagnosticSeverity.Error))
            DesktopLog.RegistrationMissing(logger, diagnostic.Code, diagnostic.Message, diagnostic.Remediation ?? string.Empty);
    }

    private const string ViewModelNotRegisteredCode = "viewmodel-not-registered";
    private const string BridgeNotRegisteredCode = "bridge-not-registered";
}
