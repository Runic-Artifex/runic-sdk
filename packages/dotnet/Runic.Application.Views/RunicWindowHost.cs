using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>
/// A host that opens application Windows: Runic Desktop (<c>RunicDesktopHost</c>) or CS-WebUI
/// (<c>RunicCsWebUiHost</c>). Application code opens Windows with
/// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>, which is the same on every host.
/// </summary>
/// <remarks>
/// Host adapters implement this interface; applications do not. The members other than
/// <see cref="Services"/> are called by <see cref="RunicWindowHostExtensions"/>.
/// </remarks>
public interface IRunicWindowHost
{
    /// <summary>The application's service provider. Each Window opens in a new scope of it.</summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Presents a Window whose scope, ViewModel and Bridge <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>
    /// has prepared. From this call the host owns <see cref="RunicWindowOpening{TViewModel}.Scope"/>: it disposes it when
    /// the window closes, and before it throws.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening, CancellationToken cancellationToken)
        where TViewModel : class;

    /// <summary>Checks, without opening anything, whether the host can present a window with <paramref name="options"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options);
}

/// <summary>Opens and validates Windows on any <see cref="IRunicWindowHost"/>.</summary>
public static class RunicWindowHostExtensions
{
    /// <summary>
    /// Creates <typeparamref name="TWindow"/>, resolves its ViewModel in a new DI scope, and opens it on
    /// <paramref name="host"/>. Release the Window with <c>await using</c>.
    /// </summary>
    /// <param name="host">The host, such as the one <c>RunicDesktopHost.Run</c> passes to the application.</param>
    /// <param name="options">The content and size; <see langword="null"/> opens <c>www/index.html</c> next to the application.</param>
    /// <param name="cancellationToken">Cancels opening. A cancelled open releases everything it created.</param>
    /// <returns>The open Window.</returns>
    /// <exception cref="RunicWindowConfigurationException">
    /// A check failed before anything opened, for example no generated Bridge is registered for the ViewModel
    /// (<see cref="RunicWindowConfigurationException.BridgeNotRegisteredCode"/>), or the host rejected the window
    /// configuration.
    /// </exception>
    public static async ValueTask<TWindow> OpenWindowAsync<TWindow>(this IRunicWindowHost host,
        RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
        where TWindow : class, IRunicWindow, new()
    {
        ArgumentNullException.ThrowIfNull(host);
        var window = new TWindow();
        await window.OpenAsync(host, options ?? RunicWindowOptions.Default, cancellationToken).ConfigureAwait(false);
        return window;
    }

    /// <summary>
    /// Checks at startup, before any window opens, that <paramref name="host"/> can open <typeparamref name="TWindow"/>
    /// with <paramref name="options"/>: the generated Bridge registration (an error when missing), the ViewModel
    /// registration (a warning when the container does not confirm it), and the host's own presentation checks.
    /// </summary>
    /// <remarks>
    /// Registrations are checked through <see cref="IServiceProviderIsService"/> without constructing anything;
    /// containers without it are not checked here, and opening still reports a missing Bridge before any window
    /// exists. Errors are logged through the provider's <see cref="ILoggerFactory"/> (event 1050). Call
    /// <see cref="RunicWindowValidationResult.ThrowIfInvalid"/> to stop at startup.
    /// </remarks>
    public static RunicWindowValidationResult ValidateWindow<TWindow>(this IRunicWindowHost host, RunicWindowOptions? options = null)
        where TWindow : class, IRunicWindow, new()
    {
        ArgumentNullException.ThrowIfNull(host);
        return new TWindow().Validate(host, options ?? RunicWindowOptions.Default);
    }
}

/// <summary>Host-neutral options for one Window. Every host reads the same values.</summary>
public sealed record RunicWindowOptions
{
    internal static RunicWindowOptions Default { get; } = new();

    /// <summary>
    /// The directory of the built frontend. <see langword="null"/>, the default, is the web root next to the
    /// application (<see cref="AppContext.BaseDirectory"/>), where the Runic build places it: <c>www</c>, or the
    /// directory that the build property <c>RunicApplicationFrontendWebRoot</c> names. On macOS, an application in a
    /// bundle without that directory uses <c>Contents/Resources/www</c> (or the web root), where a bundle keeps it.
    /// </summary>
    public string? ContentRoot { get; init; }

    /// <summary>The page to open, relative to <see cref="ContentRoot"/>. The default is <c>index.html</c>.</summary>
    public string StartPage { get; init; } = "index.html";

    /// <summary>The initial width in pixels, or <see langword="null"/> for the host's default.</summary>
    public int? Width { get; init; }

    /// <summary>The initial height in pixels, or <see langword="null"/> for the host's default.</summary>
    public int? Height { get; init; }

    /// <summary>
    /// Whether the window's Bridge error replies carry the exception type, message and stack
    /// (<see cref="WindowContentSessionOptions.IncludeFailureDetail"/>). <see langword="null"/>, the default,
    /// includes them only in a <c>Development</c> environment. Hosts read it when the window opens.
    /// </summary>
    public bool? IncludeFailureDetail { get; init; }

    /// <summary>
    /// The largest Bridge input of the window, in UTF-8 bytes
    /// (<see cref="WindowContentSessionOptions.MaximumInputBytes"/>), between 1 KiB and 64 MiB.
    /// <see langword="null"/>, the default, uses the host's default, or else 1 MiB. Hosts read it when the window opens.
    /// </summary>
    public int? MaximumInputBytes { get; init; }

    /// <summary>
    /// Gets <see cref="ContentRoot"/>, or the web root (<c>www</c> unless the build names another) next to the
    /// application. On macOS, when the application runs from a bundle's <c>Contents/MacOS</c> and that directory does
    /// not exist, gets the bundle's <c>Contents/Resources/www</c>: Apple expects only code in <c>Contents/MacOS</c>.
    /// </summary>
    public string ResolveContentRoot() =>
        ContentRoot ?? DefaultContentRoot(AppContext.BaseDirectory, OperatingSystem.IsMacOS(), Directory.Exists, DefaultWebRoot);

    /// <summary>
    /// The runtime configuration option that the Runic build sets when <c>RunicApplicationFrontendWebRoot</c> names
    /// another directory than <c>www</c>, so the application serves the directory the build copied the frontend to.
    /// Runic.Application.Views.Wpf reads the same option.
    /// </summary>
    internal const string WebRootOption = "Runic.Application.FrontendWebRoot";

    /// <summary>The web root that the build copied the frontend to, relative to the application.</summary>
    internal static string DefaultWebRoot =>
        AppContext.GetData(WebRootOption) is string { Length: > 0 } webRoot ? webRoot : "www";

    internal static string DefaultContentRoot(string baseDirectory, bool macOS, Func<string, bool> directoryExists, string webRoot = "www")
    {
        string beside = Path.Combine(baseDirectory, webRoot);
        if (!macOS || directoryExists(beside)) return beside;
        // AppContext.BaseDirectory ends with a separator: …/Name.app/Contents/MacOS/
        var macOSDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        if (macOSDirectory.Name != "MacOS" || macOSDirectory.Parent is not { Name: "Contents" } contents
            || contents.Parent is not { } bundle || !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return beside;
        string resources = Path.Combine(contents.FullName, "Resources", webRoot);
        return directoryExists(resources) ? resources : beside;
    }

    internal void ThrowIfInvalid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StartPage, nameof(StartPage));
        if (Width is <= 0) throw new ArgumentOutOfRangeException(nameof(Width), Width, "The width must be positive.");
        if (Height is <= 0) throw new ArgumentOutOfRangeException(nameof(Height), Height, "The height must be positive.");
        BridgeInputLimit.ThrowIfInvalid(MaximumInputBytes, nameof(MaximumInputBytes));
    }
}

/// <summary>A Window being opened, prepared for its host by <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/>.</summary>
/// <remarks>Host adapters use it; applications do not.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RunicWindowOpening<TViewModel> where TViewModel : class
{
    private readonly Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>? _attachWithContent;
    private readonly Func<IBridgeTransport, TViewModel, IDisposable>? _attach;

    internal RunicWindowOpening(AsyncServiceScope scope, TViewModel viewModel, RunicWindow<TViewModel> window,
        RunicWindowOptions options,
        Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>? attachWithContent,
        Func<IBridgeTransport, TViewModel, IDisposable>? attach)
    {
        Scope = scope;
        ViewModel = viewModel;
        Window = window;
        Options = options;
        _attachWithContent = attachWithContent;
        _attach = attach;
    }

    /// <summary>The Window's DI scope. The host owns it from <see cref="IRunicWindowHost.OpenAsync{TViewModel}"/> on.</summary>
    public AsyncServiceScope Scope { get; }

    /// <summary>The Window's scoped root ViewModel, already assigned to the Window.</summary>
    public TViewModel ViewModel { get; }

    /// <summary>The application Window.</summary>
    public RunicWindow<TViewModel> Window { get; }

    /// <summary>The validated options.</summary>
    public RunicWindowOptions Options { get; }

    /// <summary>Attaches the generated Bridge to <paramref name="transport"/>. Dispose the result when the window closes.</summary>
    public IDisposable AttachBridge(IBridgeTransport transport, WindowContentSession content)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(content);
        var bridge = _attachWithContent is not null
            ? _attachWithContent(transport, content, ViewModel)
            : _attach!(transport, ViewModel);
        Window.AttachedBridge(content, bridge);
        return bridge;
    }

    /// <summary>
    /// The Window's close guard, or <see langword="null"/> when <see cref="RunicWindow{TViewModel}.GuardClose"/> is
    /// <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// A host whose window can stay open calls it, after <see cref="AttachBridge"/>, when the user closes the window,
    /// and closes the window only when it returns <see langword="true"/>. It runs
    /// <see cref="RunicWindow{TViewModel}.CanCloseAsync"/> in a model turn and never throws: a failure is logged and
    /// returns <see langword="false"/>, as cancellation does. A host that cannot keep its window open calls
    /// <see cref="ReportCloseGuardUnavailable"/> instead.
    /// </remarks>
    public Func<CancellationToken, ValueTask<bool>>? CloseGuard => Window.GuardClose ? Window.AskCanCloseAsync : null;

    /// <summary>
    /// Makes <see cref="RunicWindow{TViewModel}.RequestCloseAsync"/> call <paramref name="requestClose"/>, the host's own
    /// request to close the window as the user's close button does.
    /// </summary>
    /// <remarks>
    /// A host whose user close asks <see cref="CloseGuard"/> or another confirmation passes its close request, so a
    /// request and a user close share one decision. It returns whether the window closed, and
    /// <see langword="true"/> once the window is closed. Without it, <c>RequestCloseAsync</c> asks
    /// <see cref="CloseGuard"/> itself and then closes the window with <see cref="IBridgeWindow.CloseAsync"/>.
    /// </remarks>
    /// <param name="requestClose">Requests the close; its token stops only the caller's wait.</param>
    public void UseCloseRequest(Func<CancellationToken, ValueTask<bool>> requestClose)
    {
        ArgumentNullException.ThrowIfNull(requestClose);
        Window.UseHostCloseRequest(requestClose);
    }

    /// <summary>
    /// Logs <c>WindowCloseGuardUnavailable</c> (event 1053): the Window guards its close, but
    /// <paramref name="presentation"/>, such as <c>CS-WebUI</c>, cannot keep its window open, so it opens unguarded.
    /// </summary>
    /// <param name="presentation">The presentation that cannot keep the window open, as it reads in a sentence.</param>
    public void ReportCloseGuardUnavailable(string presentation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presentation);
        var logger = Scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(RunicViewsTelemetry.LogCategory)
            ?? TraceFallbackLogger.Instance;
        ViewsLog.WindowCloseGuardUnavailable(logger, Window.GetType().Name, presentation);
    }
}

internal static class RunicWindowOpening
{
    public static async ValueTask<IBridgeWindow> OpenAsync<TViewModel>(IRunicWindowHost host, RunicWindow<TViewModel> window,
        RunicWindowOptions options, CancellationToken cancellationToken)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfInvalid();
        var services = host.Services ?? throw new InvalidOperationException("The host has no service provider.");
        // Fail before a scope or native window exists, naming the missing registration.
        var errors = RegistrationDiagnostics<TViewModel>(services)
            .Where(static diagnostic => diagnostic.Severity == RunicWindowDiagnosticSeverity.Error)
            .ToList();
        if (errors.Count > 0)
        {
            Log(services, errors);
            throw new RunicWindowConfigurationException(errors);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // Before the ViewModel creates its commands: for example, the ReactiveUI adapter logs through the
        // application's logger factory from here on, and reports a main-thread scheduler that is not Runic's.
        RunicWindowOpeningObservers.Notify(services, services.GetService<ILoggerFactory>());
        // IRunicWindowService opens further Windows on this host, also from the ViewModel resolved below.
        (services.GetService(typeof(RunicWindowService)) as RunicWindowService)?.Bind(host);

        var scope = services.CreateAsyncScope();
        RunicWindowOpening<TViewModel> opening;
        try
        {
            window.UseServices(scope.ServiceProvider);
            // IRunicWindowContext in this scope closes this Window, also from the ViewModel resolved below.
            (scope.ServiceProvider.GetService(typeof(RunicWindowContext)) as RunicWindowContext)?.Bind(window);
            // The Bridge factories come first, so a missing Bridge constructs no ViewModel. Containers without
            // IServiceProviderIsService are checked here, before the ViewModel and native window.
            var attachWithContent = scope.ServiceProvider.GetService<
                Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>();
            var attach = attachWithContent is null
                ? scope.ServiceProvider.GetService<Func<IBridgeTransport, TViewModel, IDisposable>>()
                : null;
            if (attachWithContent is null && attach is null)
            {
                var missing = new[] { BridgeNotRegistered<TViewModel>() };
                Log(services, missing);
                throw new RunicWindowConfigurationException(missing);
            }
            var viewModel = scope.ServiceProvider.GetRequiredService<TViewModel>();
            window.DataContext = viewModel;
            opening = new RunicWindowOpening<TViewModel>(scope, viewModel, window, options, attachWithContent, attach);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // The host owns the scope from here, also when it throws.
        return await host.OpenAsync(opening, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The host returned no window.");
    }

    public static RunicWindowValidationResult Validate<TViewModel>(IRunicWindowHost host, RunicWindowOptions options)
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(options);
        var services = host.Services ?? throw new InvalidOperationException("The host has no service provider.");
        var registrations = RegistrationDiagnostics<TViewModel>(services);
        Log(services, registrations.Where(static item => item.Severity == RunicWindowDiagnosticSeverity.Error).ToList());
        List<RunicWindowDiagnostic> diagnostics = [.. registrations];
        try
        {
            options.ThrowIfInvalid();
            diagnostics.AddRange(host.Validate(options));
        }
        catch (ArgumentException error)
        {
            diagnostics.Add(new RunicWindowDiagnostic(RunicWindowConfigurationException.OptionInvalidCode, error.Message,
                "Correct the RunicWindowOptions value.") { Option = error.ParamName is { } name ? $"RunicWindowOptions.{name}" : null });
        }
        return new RunicWindowValidationResult(diagnostics);
    }

    // IServiceProviderIsService answers from registrations without constructing anything. A container without it
    // cannot be inspected, so opening reports a missing Bridge when it resolves the factories. Only a missing
    // Bridge fails: a container can resolve a ViewModel it does not report, so an unconfirmed ViewModel is a warning.
    private static List<RunicWindowDiagnostic> RegistrationDiagnostics<TViewModel>(IServiceProvider services)
        where TViewModel : class
    {
        List<RunicWindowDiagnostic> diagnostics = [];
        if (services.GetService(typeof(IServiceProviderIsService)) is not IServiceProviderIsService registered)
            return diagnostics;
        if (!registered.IsService(typeof(TViewModel)))
        {
            diagnostics.Add(new RunicWindowDiagnostic(
                RunicWindowConfigurationException.ViewModelNotRegisteredCode,
                $"The container does not report the ViewModel {Name<TViewModel>()} as registered; resolving it may fail.",
                $"Register it before building the service provider, for example services.AddScoped<{typeof(TViewModel).Name}>().")
            {
                Severity = RunicWindowDiagnosticSeverity.Warning,
            });
        }
        if (!registered.IsService(typeof(Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>)) &&
            !registered.IsService(typeof(Func<IBridgeTransport, TViewModel, IDisposable>)))
            diagnostics.Add(BridgeNotRegistered<TViewModel>());
        return diagnostics;
    }

    private static RunicWindowDiagnostic BridgeNotRegistered<TViewModel>() => new(
        RunicWindowConfigurationException.BridgeNotRegisteredCode,
        $"No generated Bridge is registered for {Name<TViewModel>()}.",
        "Call services.AddRunicViews() (or AddRunicBridges()) from the generated " +
            "<Project>.RunicViewsComposition class. If it is missing or has no Bridge for this ViewModel, " +
            "check that the project references a host package (Runic.Application.Views.Desktop or Runic.Application.Views.CsWebUi) " +
            "and that the ViewModel is a public, top-level class implementing INotifyPropertyChanged.");

    private static string Name<T>() => typeof(T).FullName ?? typeof(T).Name;

    private static void Log(IServiceProvider services, IReadOnlyList<RunicWindowDiagnostic> errors)
    {
        if (errors.Count == 0 || services.GetService(typeof(ILoggerFactory)) is not ILoggerFactory loggers) return;
        var logger = loggers.CreateLogger(RunicViewsTelemetry.LogCategory);
        foreach (var error in errors)
            ViewsLog.WindowRegistrationMissing(logger, error.Code, error.Message, error.Remediation ?? string.Empty);
    }
}

/// <summary>Whether a Window diagnostic prevents the window from opening or only reports a limitation.</summary>
/// <remarks>
/// Values grow with severity, as in <c>Microsoft.Extensions.Logging.LogLevel</c>, so
/// <c>severity &gt;= RunicWindowDiagnosticSeverity.Warning</c> selects warnings and errors. The set is closed.
/// </remarks>
public enum RunicWindowDiagnosticSeverity
{
    /// <summary>The window opens as requested; the diagnostic only informs.</summary>
    Information = 0,

    /// <summary>The window opens, but ignores or narrows part of the request.</summary>
    Warning = 1,

    /// <summary>The window cannot open as configured.</summary>
    Error = 2,
}

/// <summary>One finding of a Window check, on any host.</summary>
/// <param name="Code">The stable code, such as <c>bridge-not-registered</c>.</param>
/// <param name="Message">What the check found.</param>
/// <param name="Remediation">How to fix it, if known.</param>
public sealed record RunicWindowDiagnostic(string Code, string Message, string? Remediation = null)
{
    /// <summary>Whether the diagnostic prevents the window from opening. The default is <see cref="RunicWindowDiagnosticSeverity.Error"/>.</summary>
    public RunicWindowDiagnosticSeverity Severity { get; init; } = RunicWindowDiagnosticSeverity.Error;

    /// <summary>The option the diagnostic concerns, such as <c>DesktopWindowOptions.Transparent</c>, if any.</summary>
    public string? Option { get; init; }
}

/// <summary>Every check <see cref="RunicWindowHostExtensions.ValidateWindow{TWindow}"/> ran, on any host.</summary>
public sealed record RunicWindowValidationResult(IReadOnlyList<RunicWindowDiagnostic> Diagnostics)
{
    /// <summary>Gets whether no check failed. Warnings do not make a configuration invalid.</summary>
    public bool IsValid => !Diagnostics.Any(static diagnostic => diagnostic.Severity == RunicWindowDiagnosticSeverity.Error);

    /// <summary>Gets the checks that fail the configuration.</summary>
    public IReadOnlyList<RunicWindowDiagnostic> Errors =>
        [.. Diagnostics.Where(static diagnostic => diagnostic.Severity == RunicWindowDiagnosticSeverity.Error)];

    /// <summary>Gets the requests the host will ignore or narrow.</summary>
    public IReadOnlyList<RunicWindowDiagnostic> Warnings =>
        [.. Diagnostics.Where(static diagnostic => diagnostic.Severity == RunicWindowDiagnosticSeverity.Warning)];

    /// <summary>Throws a <see cref="RunicWindowConfigurationException"/> listing every error, if any check failed.</summary>
    public void ThrowIfInvalid()
    {
        if (!IsValid) throw new RunicWindowConfigurationException(Errors);
    }
}

/// <summary>Reports a Window configuration that failed a check before the window opened, on any host.</summary>
/// <remarks>
/// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> and
/// <see cref="RunicWindowValidationResult.ThrowIfInvalid"/> throw it. A host that rejects its own configuration,
/// such as Runic Desktop's window options, reports its diagnostics here too, with its own exception as the inner exception.
/// </remarks>
public sealed class RunicWindowConfigurationException : InvalidOperationException
{
    /// <summary>The code reported when no generated Bridge is registered for the Window's ViewModel.</summary>
    public const string BridgeNotRegisteredCode = "bridge-not-registered";

    /// <summary>The code of the warning reported when the container does not confirm the ViewModel registration.</summary>
    public const string ViewModelNotRegisteredCode = "viewmodel-not-registered";

    /// <summary>The code reported when a <see cref="RunicWindowOptions"/> value is invalid.</summary>
    public const string OptionInvalidCode = "window-option-invalid";

    /// <summary>Creates an exception that lists every failed check in its message.</summary>
    public RunicWindowConfigurationException(IReadOnlyList<RunicWindowDiagnostic> diagnostics)
        : this(diagnostics, innerException: null)
    {
    }

    /// <summary>Creates an exception that lists every failed check in its message and keeps the host's own exception.</summary>
    public RunicWindowConfigurationException(IReadOnlyList<RunicWindowDiagnostic> diagnostics, Exception? innerException)
        : base(FormatMessage(diagnostics), innerException)
    {
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the failed checks.</summary>
    public IReadOnlyList<RunicWindowDiagnostic> Diagnostics { get; }

    private static string FormatMessage(IReadOnlyList<RunicWindowDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var message = new StringBuilder("The Window configuration is invalid:");
        foreach (var diagnostic in diagnostics)
        {
            message.Append(Environment.NewLine).Append("- ").Append(diagnostic.Code).Append(": ").Append(diagnostic.Message);
            if (!string.IsNullOrWhiteSpace(diagnostic.Remediation)) message.Append(' ').Append(diagnostic.Remediation);
        }
        return message.ToString();
    }
}
