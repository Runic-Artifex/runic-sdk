using System;
using System.Collections.Generic;

namespace WebUIToolkit.Hosting;

/// <summary>
/// Explicitly composes one framework-neutral application without runtime discovery.
/// </summary>
public sealed class WebUIToolkitApplicationBuilder
{
    private readonly List<IApplicationValidator> _commonValidators = [];
    private readonly Dictionary<LaunchKind, List<IApplicationValidator>> _modeValidators = [];
    private readonly List<IApplicationStartupParticipant> _participants = [];
    private readonly List<IApplicationModeRunner> _modeRunners = [];
    private ApplicationTimeoutOptions _timeouts = new();
    private IApplicationHost? _host;
    private ILaunchIntentResolver? _launchIntentResolver;
    private IExitCodePolicy? _exitCodePolicy;
    private TimeProvider? _timeProvider;
    private IApplicationLifecycleEventSink? _lifecycleEventSink;
    private bool _hostConfigured;
    private bool _launchResolverConfigured;
    private bool _exitCodePolicyConfigured;
    private bool _timeProviderConfigured;
    private bool _lifecycleEventSinkConfigured;
    private bool _built;

    /// <summary>Creates an empty application builder with deterministic defaults.</summary>
    public WebUIToolkitApplicationBuilder()
    {
    }

    /// <summary>Registers the single neutral application host.</summary>
    public WebUIToolkitApplicationBuilder UseHost(IApplicationHost host)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(host);
        EnsureNotConfigured(_hostConfigured, nameof(UseHost));
        _host = host;
        _hostConfigured = true;
        return this;
    }

    /// <summary>Replaces the side-effect-free default launch classifier.</summary>
    public WebUIToolkitApplicationBuilder UseLaunchIntentResolver(
        ILaunchIntentResolver launchIntentResolver)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(launchIntentResolver);
        EnsureNotConfigured(_launchResolverConfigured, nameof(UseLaunchIntentResolver));
        _launchIntentResolver = launchIntentResolver;
        _launchResolverConfigured = true;
        return this;
    }

    /// <summary>Adds a validator that runs for every valid launch.</summary>
    public WebUIToolkitApplicationBuilder AddValidator(IApplicationValidator validator)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(validator);
        _commonValidators.Add(validator);
        return this;
    }

    /// <summary>Adds a validator that runs only for the selected launch kind.</summary>
    public WebUIToolkitApplicationBuilder AddValidator(
        LaunchKind kind,
        IApplicationValidator validator)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(validator);
        if (!_modeValidators.TryGetValue(kind, out List<IApplicationValidator>? validators))
        {
            validators = [];
            _modeValidators.Add(kind, validators);
        }

        validators.Add(validator);
        return this;
    }

    /// <summary>Adds an ordered application startup participant.</summary>
    public WebUIToolkitApplicationBuilder AddStartupParticipant(
        IApplicationStartupParticipant participant)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(participant);
        _participants.Add(participant);
        return this;
    }

    /// <summary>Adds an explicitly known application-mode runner.</summary>
    public WebUIToolkitApplicationBuilder AddModeRunner(IApplicationModeRunner modeRunner)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(modeRunner);
        _modeRunners.Add(modeRunner);
        return this;
    }

    /// <summary>Replaces the default failure-to-exit-code policy.</summary>
    public WebUIToolkitApplicationBuilder UseExitCodePolicy(IExitCodePolicy exitCodePolicy)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(exitCodePolicy);
        EnsureNotConfigured(_exitCodePolicyConfigured, nameof(UseExitCodePolicy));
        _exitCodePolicy = exitCodePolicy;
        _exitCodePolicyConfigured = true;
        return this;
    }

    /// <summary>Replaces all bounded lifecycle options with a defensive copy.</summary>
    public WebUIToolkitApplicationBuilder UseTimeouts(ApplicationTimeoutOptions timeouts)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(timeouts);
        _timeouts = CopyTimeouts(timeouts);
        return this;
    }

    /// <summary>Configures bounded lifecycle options immediately in call order.</summary>
    public WebUIToolkitApplicationBuilder ConfigureTimeouts(
        Action<ApplicationTimeoutOptions> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        configure(_timeouts);
        return this;
    }

    /// <summary>Replaces the system clock for lifecycle deadlines and event timestamps.</summary>
    public WebUIToolkitApplicationBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(timeProvider);
        EnsureNotConfigured(_timeProviderConfigured, nameof(UseTimeProvider));
        _timeProvider = timeProvider;
        _timeProviderConfigured = true;
        return this;
    }

    /// <summary>Registers the single structured lifecycle-event sink.</summary>
    public WebUIToolkitApplicationBuilder UseLifecycleEventSink(
        IApplicationLifecycleEventSink lifecycleEventSink)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(lifecycleEventSink);
        EnsureNotConfigured(_lifecycleEventSinkConfigured, nameof(UseLifecycleEventSink));
        _lifecycleEventSink = lifecycleEventSink;
        _lifecycleEventSinkConfigured = true;
        return this;
    }

    /// <summary>Freezes registrations and creates a single-use application.</summary>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, or no neutral host was registered.
    /// </exception>
    public WebUIToolkitApplication Build()
    {
        EnsureMutable();
        if (_host is null)
        {
            throw new InvalidOperationException(
                "Exactly one neutral application host must be configured before Build().");
        }

        Dictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> modeValidators = [];
        foreach ((LaunchKind kind, List<IApplicationValidator> validators) in _modeValidators)
        {
            modeValidators.Add(kind, validators.ToArray());
        }

        ApplicationCompositionDescriptor descriptor = new(
            _host,
            _launchIntentResolver ?? new DefaultLaunchIntentResolver(),
            _commonValidators,
            modeValidators,
            _participants,
            _modeRunners,
            _exitCodePolicy,
            _timeouts,
            _timeProvider,
            _lifecycleEventSink);

        _built = true;
        return new WebUIToolkitApplication(descriptor);
    }

    private void EnsureMutable()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "An application builder cannot be changed or built again after Build().");
        }
    }

    private static void EnsureNotConfigured(bool configured, string methodName)
    {
        if (configured)
        {
            throw new InvalidOperationException(
                $"{methodName} can be called at most once for one application builder.");
        }
    }

    private static ApplicationTimeoutOptions CopyTimeouts(ApplicationTimeoutOptions source) => new()
    {
        StartupTimeout = source.StartupTimeout,
        ParticipantStopTimeout = source.ParticipantStopTimeout,
        SessionCloseTimeout = source.SessionCloseTimeout,
        WindowCloseTimeout = source.WindowCloseTimeout,
        HostStopTimeout = source.HostStopTimeout,
        TotalShutdownTimeout = source.TotalShutdownTimeout,
    };
}
