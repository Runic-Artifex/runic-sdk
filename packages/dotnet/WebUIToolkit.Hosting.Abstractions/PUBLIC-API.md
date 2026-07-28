# WebUIToolkit.Hosting.Abstractions public API

Wave B declared source surface. All types are in the `WebUIToolkit.Hosting`
namespace. Compiler-synthesized record members are omitted.

```csharp
public enum LaunchKind
{
    UserInterface = 0,
    Command = 1,
    Help = 2,
    Version = 3,
    Invalid = 4,
}

public sealed record LaunchDecision
{
    public LaunchDecision(
        LaunchKind kind,
        IReadOnlyList<string> arguments,
        string? commandName = null,
        string? diagnostic = null);

    public LaunchKind Kind { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? CommandName { get; }
    public string? Diagnostic { get; }
}

public interface ILaunchIntentResolver
{
    LaunchDecision Resolve(IReadOnlyList<string> arguments);
}

public interface IApplicationModeRunner
{
    LaunchKind Kind { get; }
    Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken);
}

public sealed class ApplicationModeRouteError
{
    public ApplicationModeRouteError(
        LaunchKind kind,
        IReadOnlyList<int> matchingRegistrationIndexes);

    public LaunchKind Kind { get; }
    public int MatchCount { get; }
    public IReadOnlyList<int> MatchingRegistrationIndexes { get; }
    public string Code { get; }
    public string SafeMessage { get; }
}

public sealed class ApplicationModeRouteSelection
{
    public IApplicationModeRunner? Runner { get; }
    public ApplicationModeRouteError? Error { get; }
    public bool IsSuccess { get; }

    public static ApplicationModeRouteSelection Selected(IApplicationModeRunner runner);
    public static ApplicationModeRouteSelection Failed(ApplicationModeRouteError error);
}

public interface IApplicationModeRouteTable
{
    ApplicationModeRouteSelection SelectRunner(LaunchKind kind);
}

public sealed class ApplicationValidationContext
{
    public ApplicationValidationContext(LaunchDecision decision);
    public LaunchDecision Decision { get; }
}

public sealed record ApplicationValidationError(string Code, string SafeMessage);

public interface IApplicationValidator
{
    ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken);
}

public enum ApplicationState
{
    Created = 0,
    Validating = 1,
    Starting = 2,
    Running = 3,
    Stopping = 4,
    Stopped = 5,
    Faulted = 6,
    Disposed = 7,
}

public enum ApplicationStartPhase
{
    Infrastructure = 0,
    Integrations = 1,
    UserInterface = 2,
}

public interface IApplicationStartupParticipant
{
    ApplicationStartPhase Phase { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public interface IApplicationHost : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public enum StopReason
{
    Unspecified = 0,
    ExternalCancellation = 1,
    ApplicationRequested = 2,
    ModeCompleted = 3,
    WindowClosed = 4,
    HostStopping = 5,
    FatalFailure = 6,
    Disposal = 7,
}

public interface IApplicationStopController
{
    CancellationToken Stopping { get; }
    bool RequestStop(StopReason reason);
    Task Completion { get; }
}

public sealed class ApplicationTimeoutOptions
{
    public ApplicationTimeoutOptions();

    public TimeSpan StartupTimeout { get; set; }
    public TimeSpan ParticipantStopTimeout { get; set; }
    public TimeSpan SessionCloseTimeout { get; set; }
    public TimeSpan WindowCloseTimeout { get; set; }
    public TimeSpan HostStopTimeout { get; set; }
    public TimeSpan TotalShutdownTimeout { get; set; }
}

public enum ApplicationFailureCategory
{
    Usage = 0,
    Configuration = 1,
    HostStartup = 2,
    FrontendAssets = 3,
    NativeRuntime = 4,
    Command = 5,
    UserInterface = 6,
    Shutdown = 7,
    Cancelled = 8,
    Unhandled = 9,
}

public sealed record ApplicationFailure(
    ApplicationFailureCategory Category,
    string Code,
    string SafeMessage,
    Exception? Exception = null,
    bool IsExpected = true);

public sealed record ApplicationRunResult
{
    public ApplicationRunResult(int? exitCode, ApplicationFailure? failure = null);
    public int? ExitCode { get; }
    public ApplicationFailure? Failure { get; }
    public bool IsSuccess { get; }

    public static ApplicationRunResult FromExitCode(int exitCode);
    public static ApplicationRunResult FromFailure(ApplicationFailure failure);
}

public interface IExitCodePolicy
{
    int GetExitCode(ApplicationFailure failure);
}

public static class ApplicationFailureCodes
{
    public const string Validation = "WUTHOST1001";
    public const string HostStart = "WUTHOST1101";
    public const string ParticipantStart = "WUTHOST1102";
    public const string StartupTimeout = "WUTHOST1103";
    public const string RunnerSelection = "WUTHOST1201";
    public const string RunnerFailure = "WUTHOST1202";
    public const string Cancellation = "WUTHOST1301";
    public const string ParticipantStop = "WUTHOST1401";
    public const string StopTimeout = "WUTHOST1402";
    public const string HostStop = "WUTHOST1403";
    public const string Dispose = "WUTHOST1404";
    public const string TotalShutdownTimeout = "WUTHOST1405";
}

public sealed record FrontendAsset
{
    public FrontendAsset(
        string relativePath,
        string mediaType,
        long length,
        string sha256,
        bool isEntryPoint = false,
        string? brotliPath = null,
        string? gzipPath = null);

    public string RelativePath { get; }
    public string MediaType { get; }
    public long Length { get; }
    public string Sha256 { get; }
    public bool IsEntryPoint { get; }
    public string? BrotliPath { get; }
    public string? GzipPath { get; }
}

public interface IFrontendAssetManifest
{
    string ManifestVersion { get; }
    IReadOnlyList<FrontendAsset> Assets { get; }
}

public interface IFrontendAssetProvider
{
    IFrontendAssetManifest Manifest { get; }
    ValueTask ValidateAsync(CancellationToken cancellationToken);
    ValueTask<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken);
}

public sealed record BrowserHostOptions
{
    public BrowserHostOptions(string applicationId);
    public string ApplicationId { get; }
}

public sealed record BrowserWindowOptions
{
    public BrowserWindowOptions(
        string windowId,
        string title,
        int width = 1024,
        int height = 768,
        bool isResizable = true,
        WebUIToolkit.Desktop.DesktopBrowserProfile? browserProfile = null);

    public string WindowId { get; }
    public string Title { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsResizable { get; }
    public WebUIToolkit.Desktop.DesktopBrowserProfile? BrowserProfile { get; }
}

public interface IBrowserHostFactory
{
    ValueTask<IBrowserHost> CreateAsync(
        BrowserHostOptions options,
        CancellationToken cancellationToken);
}

public interface IBrowserHost : IAsyncDisposable
{
    IUiDispatcher Dispatcher { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken);
    ValueTask<IBrowserWindow> CreateWindowAsync(
        BrowserWindowOptions options,
        CancellationToken cancellationToken);
}

public interface IBrowserWindow : IAsyncDisposable
{
    event EventHandler? CloseRequested;
    ValueTask NavigateAsync(Uri entryPoint, CancellationToken cancellationToken);
    ValueTask ShowAsync(CancellationToken cancellationToken);
    Task WaitForCloseAsync(CancellationToken cancellationToken);
    ValueTask CloseAsync(CancellationToken cancellationToken);
}

public interface IUiDispatcher
{
    bool CheckAccess();
    ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken);
    ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken);
}

public sealed class BrowserDesktopEventArgs : EventArgs
{
    public BrowserDesktopEventArgs(string name, string id, string payloadJson);
    public string Name { get; }
    public string Id { get; }
    public string PayloadJson { get; }
}

public interface IBrowserWindowDesktopAdapter
{
    event EventHandler<BrowserDesktopEventArgs>? DesktopEventReceived;
    WebUIToolkit.Desktop.DesktopCapabilityReport Capabilities { get; }
    ValueTask FocusWindowAsync(CancellationToken cancellationToken);
    ValueTask FocusElementAsync(string elementId, CancellationToken cancellationToken);
    ValueTask SetSizeAsync(
        WebUIToolkit.Desktop.DesktopSize size,
        CancellationToken cancellationToken);
    ValueTask SetPositionAsync(
        WebUIToolkit.Desktop.DesktopPosition position,
        CancellationToken cancellationToken);
    ValueTask CenterAsync(CancellationToken cancellationToken);
    ValueTask SetStateAsync(
        WebUIToolkit.Desktop.DesktopWindowState state,
        CancellationToken cancellationToken);
    ValueTask<string> InvokeBrowserAsync(
        string operation,
        string payloadJson,
        CancellationToken cancellationToken);
}

public interface IApplicationLifecycleEventSink
{
    void Publish(ApplicationLifecycleEvent lifecycleEvent);
}

public static class ApplicationLifecycleEventIds
{
    public const int StateTransition = 11000;
    public const int LaunchSelected = 11001;
    public const int StopRequested = 11002;
    public const int PrimaryFailure = 11003;
    public const int SecondaryFailure = 11004;
    public const int Timeout = 11005;
    public const int Completion = 11006;
}

public enum ApplicationLifecycleTimeoutKind
{
    Startup = 0,
    ModeStop = 1,
    ParticipantStop = 2,
    HostStop = 3,
    HostDispose = 4,
    TotalShutdown = 5,
}

public abstract record ApplicationLifecycleEvent
{
    protected ApplicationLifecycleEvent(
        int eventId,
        long sequence,
        DateTimeOffset timestamp);

    public int EventId { get; }
    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }
}

public sealed record ApplicationStateTransitionEvent : ApplicationLifecycleEvent
{
    public ApplicationStateTransitionEvent(
        long sequence,
        DateTimeOffset timestamp,
        ApplicationState previousState,
        ApplicationState currentState);

    public ApplicationState PreviousState { get; }
    public ApplicationState CurrentState { get; }
}

public sealed record ApplicationLaunchEvent : ApplicationLifecycleEvent
{
    public ApplicationLaunchEvent(
        long sequence,
        DateTimeOffset timestamp,
        LaunchKind launchKind);

    public LaunchKind LaunchKind { get; }
}

public sealed record ApplicationStopRequestedEvent : ApplicationLifecycleEvent
{
    public ApplicationStopRequestedEvent(
        long sequence,
        DateTimeOffset timestamp,
        StopReason reason);

    public StopReason Reason { get; }
}

public sealed record ApplicationFailureEvent : ApplicationLifecycleEvent
{
    public ApplicationFailureEvent(
        long sequence,
        DateTimeOffset timestamp,
        bool isPrimary,
        ApplicationFailureCategory category,
        string? failureCode,
        bool isExpected);

    public bool IsPrimary { get; }
    public ApplicationFailureCategory Category { get; }
    public string? FailureCode { get; }
    public bool IsExpected { get; }
}

public sealed record ApplicationTimeoutEvent : ApplicationLifecycleEvent
{
    public ApplicationTimeoutEvent(
        long sequence,
        DateTimeOffset timestamp,
        ApplicationLifecycleTimeoutKind timeoutKind,
        bool totalShutdownDeadlineExpired);

    public ApplicationLifecycleTimeoutKind TimeoutKind { get; }
    public bool TotalShutdownDeadlineExpired { get; }
}

public sealed record ApplicationCompletionEvent : ApplicationLifecycleEvent
{
    public ApplicationCompletionEvent(
        long sequence,
        DateTimeOffset timestamp,
        int? exitCode,
        bool isSuccess,
        int secondaryFailureCount);

    public int? ExitCode { get; }
    public bool IsSuccess { get; }
    public int SecondaryFailureCount { get; }
}
```
