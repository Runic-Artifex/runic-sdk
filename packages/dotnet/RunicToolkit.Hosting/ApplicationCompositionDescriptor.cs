using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting;

/// <summary>
/// Contains the immutable, framework-neutral registrations used by one built application.
/// </summary>
public sealed class ApplicationCompositionDescriptor
{
    private readonly ApplicationTimeoutOptions _timeouts;

    /// <summary>Initializes a descriptor and freezes every registration sequence.</summary>
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
        IApplicationLifecycleEventSink? lifecycleEventSink = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        LaunchIntentResolver = launchIntentResolver
            ?? throw new ArgumentNullException(nameof(launchIntentResolver));

        ReadOnlyCollection<IApplicationModeRunner> runnerSnapshot =
            SnapshotModeRunners(modeRunners, nameof(modeRunners));
        RouteTable = new ApplicationModeRouteTable(runnerSnapshot);
        CompositionValidator = new ApplicationCompositionValidator(
            RouteTable,
            commonValidators,
            modeValidators);
        CommonValidators = CompositionValidator.CommonValidators;
        ModeValidators = CompositionValidator.ModeValidators;
        Participants = SnapshotParticipants(participants, nameof(participants));
        ModeRunners = runnerSnapshot;
        ExitCodePolicy = exitCodePolicy ?? new DefaultExitCodePolicy();
        TimeProvider = timeProvider ?? TimeProvider.System;
        LifecycleEventSink = lifecycleEventSink;

        ApplicationTimeoutOptions sourceTimeouts = timeouts ?? new ApplicationTimeoutOptions();
        _timeouts = CopyTimeouts(sourceTimeouts);

        LifecycleDescriptor = new ApplicationLifecycleDescriptor(
            Host,
            [CompositionValidator],
            Participants,
            ModeRunners,
            ExitCodePolicy,
            _timeouts);
    }

    /// <summary>Gets the neutral host bridge.</summary>
    public IApplicationHost Host { get; }

    /// <summary>Gets the side-effect-free launch classifier.</summary>
    public ILaunchIntentResolver LaunchIntentResolver { get; }

    /// <summary>Gets common validators in registration order.</summary>
    public IReadOnlyList<IApplicationValidator> CommonValidators { get; }

    /// <summary>Gets selected-mode validators in registration order.</summary>
    public IReadOnlyDictionary<LaunchKind, IReadOnlyList<IApplicationValidator>> ModeValidators { get; }

    /// <summary>Gets startup participants in registration order.</summary>
    public IReadOnlyList<IApplicationStartupParticipant> Participants { get; }

    /// <summary>Gets mode runners in registration order.</summary>
    public IReadOnlyList<IApplicationModeRunner> ModeRunners { get; }

    /// <summary>Gets the immutable runner route table.</summary>
    public IApplicationModeRouteTable RouteTable { get; }

    /// <summary>Gets the deterministic composition-validation pipeline.</summary>
    public ApplicationCompositionValidator CompositionValidator { get; }

    /// <summary>Gets the stable failure-to-exit-code policy.</summary>
    public IExitCodePolicy ExitCodePolicy { get; }

    /// <summary>Gets the clock used by lifecycle deadlines and events.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the optional structured lifecycle-event sink.</summary>
    public IApplicationLifecycleEventSink? LifecycleEventSink { get; }

    /// <summary>Gets an isolated copy of the bounded lifecycle options.</summary>
    public ApplicationTimeoutOptions Timeouts => CopyTimeouts(_timeouts);

    internal ApplicationLifecycleDescriptor LifecycleDescriptor { get; }

    private static ReadOnlyCollection<IApplicationModeRunner> SnapshotModeRunners(
        IEnumerable<IApplicationModeRunner> registrations,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(registrations, parameterName);
        List<IApplicationModeRunner> snapshot = [];
        foreach (IApplicationModeRunner registration in registrations)
        {
            snapshot.Add(new FrozenModeRunner(
                registration ?? throw new ArgumentException(
                    "Registration sequences cannot contain null entries.",
                    parameterName)));
        }

        return Array.AsReadOnly(snapshot.ToArray());
    }

    private static ReadOnlyCollection<IApplicationStartupParticipant> SnapshotParticipants(
        IEnumerable<IApplicationStartupParticipant> registrations,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(registrations, parameterName);
        List<IApplicationStartupParticipant> snapshot = [];
        foreach (IApplicationStartupParticipant registration in registrations)
        {
            snapshot.Add(new FrozenStartupParticipant(
                registration ?? throw new ArgumentException(
                    "Registration sequences cannot contain null entries.",
                    parameterName)));
        }

        return Array.AsReadOnly(snapshot.ToArray());
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

    private sealed class FrozenModeRunner : IApplicationModeRunner
    {
        private readonly IApplicationModeRunner _inner;

        internal FrozenModeRunner(IApplicationModeRunner inner)
        {
            _inner = inner;
            Kind = inner.Kind;
        }

        public LaunchKind Kind { get; }

        public Task<ApplicationRunResult> RunAsync(
            LaunchDecision decision,
            CancellationToken cancellationToken) =>
            _inner.RunAsync(decision, cancellationToken);
    }

    private sealed class FrozenStartupParticipant : IApplicationStartupParticipant
    {
        private readonly IApplicationStartupParticipant _inner;

        internal FrozenStartupParticipant(IApplicationStartupParticipant inner)
        {
            _inner = inner;
            Phase = inner.Phase;
        }

        public ApplicationStartPhase Phase { get; }

        public ValueTask StartAsync(CancellationToken cancellationToken) =>
            _inner.StartAsync(cancellationToken);

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            _inner.StopAsync(cancellationToken);
    }
}
