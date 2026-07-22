using System;

namespace WebUIToolkit.Hosting;

/// <summary>Receives ordered, sanitized lifecycle events.</summary>
/// <remarks>
/// The lifecycle kernel queues serialized, best-effort calls on the thread pool and
/// isolates sink latency and exceptions so telemetry cannot change lifecycle completion.
/// Delivery can finish after lifecycle completion.
/// </remarks>
public interface IApplicationLifecycleEventSink
{
    /// <summary>Publishes one immutable lifecycle event.</summary>
    /// <param name="lifecycleEvent">The event to publish.</param>
    void Publish(ApplicationLifecycleEvent lifecycleEvent);
}

/// <summary>Defines stable identifiers reserved for Hosting lifecycle events.</summary>
public static class ApplicationLifecycleEventIds
{
    /// <summary>A legal lifecycle state transition completed.</summary>
    public const int StateTransition = 11000;

    /// <summary>A launch kind was selected for execution.</summary>
    public const int LaunchSelected = 11001;

    /// <summary>A stop request won exact-once stop selection.</summary>
    public const int StopRequested = 11002;

    /// <summary>A failure became the primary lifecycle outcome.</summary>
    public const int PrimaryFailure = 11003;

    /// <summary>A failure was retained as a secondary lifecycle failure.</summary>
    public const int SecondaryFailure = 11004;

    /// <summary>A bounded lifecycle operation exceeded its timeout.</summary>
    public const int Timeout = 11005;

    /// <summary>The lifecycle selected and published its stable terminal result.</summary>
    public const int Completion = 11006;
}

/// <summary>Identifies the bounded operation represented by a timeout event.</summary>
public enum ApplicationLifecycleTimeoutKind
{
    /// <summary>Host or participant startup exceeded the shared startup deadline.</summary>
    Startup,

    /// <summary>The selected application mode did not stop within its bound.</summary>
    ModeStop,

    /// <summary>A startup participant did not stop within its bound.</summary>
    ParticipantStop,

    /// <summary>The neutral application host did not stop within its bound.</summary>
    HostStop,

    /// <summary>The neutral application host did not dispose within the total bound.</summary>
    HostDispose,

    /// <summary>The total shutdown deadline expired.</summary>
    TotalShutdown,
}

/// <summary>Provides common ordered lifecycle-event metadata.</summary>
public abstract record ApplicationLifecycleEvent
{
    /// <summary>Initializes common lifecycle-event metadata.</summary>
    /// <param name="eventId">The stable event identifier.</param>
    /// <param name="sequence">The process-local publication sequence.</param>
    /// <param name="timestamp">The timestamp supplied by the lifecycle clock.</param>
    protected ApplicationLifecycleEvent(int eventId, long sequence, DateTimeOffset timestamp)
    {
        EventId = eventId;
        Sequence = sequence;
        Timestamp = timestamp;
    }

    /// <summary>Gets the stable identifier in the Hosting event range 11000-11999.</summary>
    public int EventId { get; }

    /// <summary>Gets the monotonically increasing sequence assigned by one kernel.</summary>
    public long Sequence { get; }

    /// <summary>Gets the timestamp supplied by the kernel's <see cref="TimeProvider"/>.</summary>
    public DateTimeOffset Timestamp { get; }
}

/// <summary>Describes one completed legal lifecycle-state transition.</summary>
public sealed record ApplicationStateTransitionEvent : ApplicationLifecycleEvent
{
    /// <summary>Initializes a state-transition event.</summary>
    public ApplicationStateTransitionEvent(
        long sequence,
        DateTimeOffset timestamp,
        ApplicationState previousState,
        ApplicationState currentState)
        : base(ApplicationLifecycleEventIds.StateTransition, sequence, timestamp)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    /// <summary>Gets the state before the transition.</summary>
    public ApplicationState PreviousState { get; }

    /// <summary>Gets the state after the transition.</summary>
    public ApplicationState CurrentState { get; }
}

/// <summary>Describes the selected launch kind without retaining launch arguments.</summary>
public sealed record ApplicationLaunchEvent : ApplicationLifecycleEvent
{
    /// <summary>Initializes a launch-selection event.</summary>
    public ApplicationLaunchEvent(long sequence, DateTimeOffset timestamp, LaunchKind launchKind)
        : base(ApplicationLifecycleEventIds.LaunchSelected, sequence, timestamp)
    {
        LaunchKind = launchKind;
    }

    /// <summary>Gets the selected launch kind.</summary>
    public LaunchKind LaunchKind { get; }
}

/// <summary>Describes the stop request that won exact-once selection.</summary>
public sealed record ApplicationStopRequestedEvent : ApplicationLifecycleEvent
{
    /// <summary>Initializes a stop-request event.</summary>
    public ApplicationStopRequestedEvent(long sequence, DateTimeOffset timestamp, StopReason reason)
        : base(ApplicationLifecycleEventIds.StopRequested, sequence, timestamp)
    {
        Reason = reason;
    }

    /// <summary>Gets the winning stop reason.</summary>
    public StopReason Reason { get; }
}

/// <summary>Describes a primary or secondary failure without exception or message data.</summary>
public sealed record ApplicationFailureEvent : ApplicationLifecycleEvent
{
    /// <summary>Initializes a sanitized failure event.</summary>
    public ApplicationFailureEvent(
        long sequence,
        DateTimeOffset timestamp,
        bool isPrimary,
        ApplicationFailureCategory category,
        string? failureCode,
        bool isExpected)
        : base(
            isPrimary
                ? ApplicationLifecycleEventIds.PrimaryFailure
                : ApplicationLifecycleEventIds.SecondaryFailure,
            sequence,
            timestamp)
    {
        IsPrimary = isPrimary;
        Category = category;
        FailureCode = failureCode;
        IsExpected = isExpected;
    }

    /// <summary>Gets whether the failure became the primary lifecycle outcome.</summary>
    public bool IsPrimary { get; }

    /// <summary>Gets the stable failure category.</summary>
    public ApplicationFailureCategory Category { get; }

    /// <summary>
    /// Gets the safe stable diagnostic code, or <see langword="null"/> when the supplied
    /// code did not satisfy the event publisher's conservative diagnostic-code format.
    /// Failure messages are never included.
    /// </summary>
    public string? FailureCode { get; }

    /// <summary>Gets whether the failure is expected under normal application operation.</summary>
    public bool IsExpected { get; }
}

/// <summary>Describes a bounded lifecycle-operation timeout.</summary>
public sealed record ApplicationTimeoutEvent : ApplicationLifecycleEvent
{
    /// <summary>Initializes a timeout event.</summary>
    public ApplicationTimeoutEvent(
        long sequence,
        DateTimeOffset timestamp,
        ApplicationLifecycleTimeoutKind timeoutKind,
        bool totalShutdownDeadlineExpired)
        : base(ApplicationLifecycleEventIds.Timeout, sequence, timestamp)
    {
        TimeoutKind = timeoutKind;
        TotalShutdownDeadlineExpired = totalShutdownDeadlineExpired;
    }

    /// <summary>Gets the bounded operation that timed out.</summary>
    public ApplicationLifecycleTimeoutKind TimeoutKind { get; }

    /// <summary>Gets whether the total shutdown deadline supplied the effective bound.</summary>
    public bool TotalShutdownDeadlineExpired { get; }
}

/// <summary>Describes the stable terminal lifecycle result.</summary>
public sealed record ApplicationCompletionEvent : ApplicationLifecycleEvent
{
    /// <summary>Initializes a completion event.</summary>
    public ApplicationCompletionEvent(
        long sequence,
        DateTimeOffset timestamp,
        int? exitCode,
        bool isSuccess,
        int secondaryFailureCount)
        : base(ApplicationLifecycleEventIds.Completion, sequence, timestamp)
    {
        ExitCode = exitCode;
        IsSuccess = isSuccess;
        SecondaryFailureCount = secondaryFailureCount;
    }

    /// <summary>Gets the selected process exit code, if mapped.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets whether the result represents a normal successful completion.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the number of secondary failures retained before completion.</summary>
    public int SecondaryFailureCount { get; }
}
