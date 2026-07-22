using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

/// <summary>Identifies a serialized application lifecycle state.</summary>
public enum ApplicationState
{
    /// <summary>The immutable application has been created but not run.</summary>
    Created,
    /// <summary>Common and selected-mode validation is running.</summary>
    Validating,
    /// <summary>The host and startup participants are starting.</summary>
    Starting,
    /// <summary>The selected application mode is running.</summary>
    Running,
    /// <summary>Bounded teardown is running.</summary>
    Stopping,
    /// <summary>The lifecycle completed successfully.</summary>
    Stopped,
    /// <summary>The lifecycle completed with a primary failure or non-zero result.</summary>
    Faulted,
    /// <summary>The lifecycle and its host have been disposed.</summary>
    Disposed,
}

/// <summary>Defines deterministic startup ordering.</summary>
public enum ApplicationStartPhase
{
    /// <summary>Process infrastructure.</summary>
    Infrastructure,
    /// <summary>External integration seams.</summary>
    Integrations,
    /// <summary>User-interface infrastructure.</summary>
    UserInterface,
}

/// <summary>Participates in ordered startup and reverse-completion-order teardown.</summary>
public interface IApplicationStartupParticipant
{
    /// <summary>Gets the participant's startup phase.</summary>
    ApplicationStartPhase Phase { get; }

    /// <summary>Starts the participant.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the participant. Implementations must be idempotent.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Bridges a composition host into the lifecycle kernel without requiring a concrete
/// dependency-injection or Generic Host implementation.
/// </summary>
public interface IApplicationHost : IAsyncDisposable
{
    /// <summary>Starts the host.</summary>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the host.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Identifies the source that first requested application stop.</summary>
public enum StopReason
{
    /// <summary>No more specific source was supplied.</summary>
    Unspecified,
    /// <summary>External process cancellation requested stop.</summary>
    ExternalCancellation,
    /// <summary>Application code requested stop.</summary>
    ApplicationRequested,
    /// <summary>The selected mode completed.</summary>
    ModeCompleted,
    /// <summary>An interactive window closed.</summary>
    WindowClosed,
    /// <summary>The composition host requested stop.</summary>
    HostStopping,
    /// <summary>A fatal lifecycle failure requested stop.</summary>
    FatalFailure,
    /// <summary>Disposal requested stop.</summary>
    Disposal,
}

/// <summary>Converges competing stop sources onto one exact-once request.</summary>
public interface IApplicationStopController
{
    /// <summary>Gets a token cancelled by the first stop request.</summary>
    CancellationToken Stopping { get; }

    /// <summary>Requests stop and reports whether this request won.</summary>
    bool RequestStop(StopReason reason);

    /// <summary>Gets the lifecycle completion task shared by all stop callers.</summary>
    Task Completion { get; }
}

/// <summary>Defines bounded lifecycle operation durations.</summary>
public sealed class ApplicationTimeoutOptions
{
    /// <summary>Gets or sets the total host and participant startup timeout.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the timeout for one participant stop.</summary>
    public TimeSpan ParticipantStopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the timeout reserved for closing a root session.</summary>
    public TimeSpan SessionCloseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the timeout for closing an interactive mode.</summary>
    public TimeSpan WindowCloseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the timeout for stopping the composition host.</summary>
    public TimeSpan HostStopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the total shutdown timeout.</summary>
    public TimeSpan TotalShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
