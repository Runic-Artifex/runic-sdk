# WebUIToolkit.Hosting public API

Wave B declared source surface. All types are in the `WebUIToolkit.Hosting`
namespace.

```csharp
public sealed class DefaultLaunchIntentResolver : ILaunchIntentResolver
{
    public DefaultLaunchIntentResolver();
    public LaunchDecision Resolve(IReadOnlyList<string> arguments);
}

public sealed class ApplicationModeRouteTable : IApplicationModeRouteTable
{
    public ApplicationModeRouteTable(IEnumerable<IApplicationModeRunner> runners);
    public IReadOnlyList<IApplicationModeRunner> Runners { get; }
    public ApplicationModeRouteSelection SelectRunner(LaunchKind kind);
}

public sealed class ApplicationCompositionValidator : IApplicationValidator
{
    public ApplicationCompositionValidator(
        IApplicationModeRouteTable routeTable,
        IEnumerable<IApplicationValidator> commonValidators,
        IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> modeValidators);

    public IReadOnlyList<IApplicationValidator> CommonValidators { get; }
    public IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> ModeValidators { get; }

    public ValueTask ValidateAsync(
        ApplicationValidationContext context,
        ICollection<ApplicationValidationError> errors,
        CancellationToken cancellationToken);
}

public sealed class ApplicationCompositionDescriptor
{
    public ApplicationCompositionDescriptor(
        IApplicationHost host,
        ILaunchIntentResolver launchIntentResolver,
        IEnumerable<IApplicationValidator> commonValidators,
        IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> modeValidators,
        IEnumerable<IApplicationStartupParticipant> participants,
        IEnumerable<IApplicationModeRunner> modeRunners,
        IExitCodePolicy? exitCodePolicy = null,
        ApplicationTimeoutOptions? timeouts = null,
        TimeProvider? timeProvider = null,
        IApplicationLifecycleEventSink? lifecycleEventSink = null);

    public IApplicationHost Host { get; }
    public ILaunchIntentResolver LaunchIntentResolver { get; }
    public IReadOnlyList<IApplicationValidator> CommonValidators { get; }
    public IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> ModeValidators { get; }
    public IReadOnlyList<IApplicationStartupParticipant> Participants { get; }
    public IReadOnlyList<IApplicationModeRunner> ModeRunners { get; }
    public IApplicationModeRouteTable RouteTable { get; }
    public ApplicationCompositionValidator CompositionValidator { get; }
    public IExitCodePolicy ExitCodePolicy { get; }
    public TimeProvider TimeProvider { get; }
    public IApplicationLifecycleEventSink? LifecycleEventSink { get; }
    public ApplicationTimeoutOptions Timeouts { get; }
}

public sealed class WebUIToolkitApplicationBuilder
{
    public WebUIToolkitApplicationBuilder();

    public WebUIToolkitApplicationBuilder UseHost(IApplicationHost host);
    public WebUIToolkitApplicationBuilder UseLaunchIntentResolver(
        ILaunchIntentResolver launchIntentResolver);
    public WebUIToolkitApplicationBuilder AddValidator(IApplicationValidator validator);
    public WebUIToolkitApplicationBuilder AddValidator(
        LaunchKind kind,
        IApplicationValidator validator);
    public WebUIToolkitApplicationBuilder AddStartupParticipant(
        IApplicationStartupParticipant participant);
    public WebUIToolkitApplicationBuilder AddModeRunner(IApplicationModeRunner modeRunner);
    public WebUIToolkitApplicationBuilder UseExitCodePolicy(IExitCodePolicy exitCodePolicy);
    public WebUIToolkitApplicationBuilder UseTimeouts(ApplicationTimeoutOptions timeouts);
    public WebUIToolkitApplicationBuilder ConfigureTimeouts(
        Action<ApplicationTimeoutOptions> configure);
    public WebUIToolkitApplicationBuilder UseTimeProvider(TimeProvider timeProvider);
    public WebUIToolkitApplicationBuilder UseLifecycleEventSink(
        IApplicationLifecycleEventSink lifecycleEventSink);
    public bool TryUseLifecycleEventSink(
        IApplicationLifecycleEventSink lifecycleEventSink);
    public WebUIToolkitApplication Build();
}

public sealed class WebUIToolkitApplication : IAsyncDisposable
{
    public WebUIToolkitApplication(ApplicationCompositionDescriptor descriptor);

    public ApplicationCompositionDescriptor Descriptor { get; }
    public ApplicationState State { get; }
    public Task<ApplicationRunResult> Completion { get; }
    public IApplicationStopController StopController { get; }
    public IReadOnlyList<ApplicationFailure> SecondaryFailures { get; }

    public Task<ApplicationRunResult> RunAsync(
        CancellationToken cancellationToken = default);
    public Task<ApplicationRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
    public ValueTask DisposeAsync();
}

public sealed class ApplicationLifecycleDescriptor
{
    public ApplicationLifecycleDescriptor(
        IApplicationHost host,
        IEnumerable<IApplicationValidator> validators,
        IEnumerable<IApplicationStartupParticipant> participants,
        IEnumerable<IApplicationModeRunner> modeRunners,
        IExitCodePolicy? exitCodePolicy = null,
        ApplicationTimeoutOptions? timeouts = null);

    public IApplicationHost Host { get; }
    public IReadOnlyList<IApplicationValidator> Validators { get; }
    public IReadOnlyList<IApplicationStartupParticipant> Participants { get; }
    public IReadOnlyList<IApplicationModeRunner> ModeRunners { get; }
    public IExitCodePolicy ExitCodePolicy { get; }
    public ApplicationTimeoutOptions Timeouts { get; }
}

public sealed class ApplicationLifecycleKernel : IAsyncDisposable
{
    public ApplicationLifecycleKernel(
        ApplicationLifecycleDescriptor descriptor,
        TimeProvider? timeProvider = null);
    public ApplicationLifecycleKernel(
        ApplicationLifecycleDescriptor descriptor,
        TimeProvider? timeProvider,
        IApplicationLifecycleEventSink? lifecycleEventSink);

    public ApplicationState State { get; }
    public Task<ApplicationRunResult> Completion { get; }
    public IApplicationStopController StopController { get; }
    public IReadOnlyList<ApplicationFailure> SecondaryFailures { get; }

    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken = default);
    public ValueTask DisposeAsync();
}

public sealed class ApplicationLifecycleStateMachine
{
    public ApplicationLifecycleStateMachine();
    public ApplicationState State { get; }
    public bool TryTransition(ApplicationState next);
    public void Transition(ApplicationState next);
}

public sealed class DefaultExitCodePolicy : IExitCodePolicy
{
    public DefaultExitCodePolicy();
    public int GetExitCode(ApplicationFailure failure);
}

public sealed class ApplicationStopControllerBinding : IApplicationStopController
{
    public ApplicationStopControllerBinding();
    public CancellationToken Stopping { get; }
    public Task Completion { get; }
    public void Bind(IApplicationStopController controller);
    public bool RequestStop(StopReason reason);
}

```
