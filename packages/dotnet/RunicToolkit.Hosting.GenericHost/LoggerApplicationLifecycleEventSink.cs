using System;
using Microsoft.Extensions.Logging;

namespace RunicToolkit.Hosting;

/// <summary>
/// Writes lifecycle events to <see cref="ILogger"/> using only bounded enum values,
/// numeric duration/count values, and pre-sanitized stable codes.
/// </summary>
public sealed class LoggerApplicationLifecycleEventSink : IApplicationLifecycleEventSink
{
    private static readonly Action<ILogger, ApplicationState, ApplicationState, long, Exception?>
        StateChanged = LoggerMessage.Define<ApplicationState, ApplicationState, long>(
            LogLevel.Information,
            new EventId(ApplicationLifecycleEventIds.StateTransition, nameof(ApplicationStateTransitionEvent)),
            "Hosting phase changed from {PreviousPhase} to {CurrentPhase} after {DurationMilliseconds} ms.");
    private static readonly Action<ILogger, LaunchKind, Exception?> LaunchSelected =
        LoggerMessage.Define<LaunchKind>(
            LogLevel.Information,
            new EventId(ApplicationLifecycleEventIds.LaunchSelected, nameof(ApplicationLaunchEvent)),
            "Hosting selected launch kind {LaunchKind}.");
    private static readonly Action<ILogger, StopReason, Exception?> StopRequested =
        LoggerMessage.Define<StopReason>(
            LogLevel.Information,
            new EventId(ApplicationLifecycleEventIds.StopRequested, nameof(ApplicationStopRequestedEvent)),
            "Hosting accepted stop reason {StopReason}.");
    private static readonly Action<ILogger, ApplicationFailureCategory, bool, bool, string, Exception?>
        PrimaryFailure = LoggerMessage.Define<ApplicationFailureCategory, bool, bool, string>(
            LogLevel.Error,
            new EventId(ApplicationLifecycleEventIds.PrimaryFailure, nameof(ApplicationFailureEvent)),
            "Hosting recorded failure category {FailureCategory}, primary {IsPrimary}, expected {IsExpected}, code {FailureCode}.");
    private static readonly Action<ILogger, ApplicationFailureCategory, bool, bool, string, Exception?>
        SecondaryFailure = LoggerMessage.Define<ApplicationFailureCategory, bool, bool, string>(
            LogLevel.Warning,
            new EventId(ApplicationLifecycleEventIds.SecondaryFailure, nameof(ApplicationFailureEvent)),
            "Hosting recorded failure category {FailureCategory}, primary {IsPrimary}, expected {IsExpected}, code {FailureCode}.");
    private static readonly Action<ILogger, ApplicationLifecycleTimeoutKind, bool, Exception?>
        TimedOut = LoggerMessage.Define<ApplicationLifecycleTimeoutKind, bool>(
            LogLevel.Warning,
            new EventId(ApplicationLifecycleEventIds.Timeout, nameof(ApplicationTimeoutEvent)),
            "Hosting timeout kind {TimeoutKind}, total deadline {TotalDeadlineExpired}.");
    private static readonly Action<ILogger, bool, int?, int, Exception?> Completed =
        LoggerMessage.Define<bool, int?, int>(
            LogLevel.Information,
            new EventId(ApplicationLifecycleEventIds.Completion, nameof(ApplicationCompletionEvent)),
            "Hosting completed with success {IsSuccess}, exit code {ExitCode}, secondary failures {SecondaryFailureCount}.");
    private static readonly Action<ILogger, bool, int?, int, Exception?> CompletedWithFailure =
        LoggerMessage.Define<bool, int?, int>(
            LogLevel.Error,
            new EventId(ApplicationLifecycleEventIds.Completion, nameof(ApplicationCompletionEvent)),
            "Hosting completed with success {IsSuccess}, exit code {ExitCode}, secondary failures {SecondaryFailureCount}.");

    private readonly object _sync = new();
    private readonly ILogger _logger;
    private DateTimeOffset? _phaseStarted;

    /// <summary>Initializes the structured lifecycle sink.</summary>
    public LoggerApplicationLifecycleEventSink(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Publish(ApplicationLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        switch (lifecycleEvent)
        {
            case ApplicationStateTransitionEvent transition:
                long durationMilliseconds;
                lock (_sync)
                {
                    durationMilliseconds = _phaseStarted is null
                        ? 0
                        : Math.Max(
                            0,
                            (long)(transition.Timestamp - _phaseStarted.Value).TotalMilliseconds);
                    _phaseStarted = transition.Timestamp;
                }

                StateChanged(
                    _logger,
                    transition.PreviousState,
                    transition.CurrentState,
                    durationMilliseconds,
                    null);
                break;

            case ApplicationLaunchEvent launch:
                LaunchSelected(_logger, launch.LaunchKind, null);
                break;

            case ApplicationStopRequestedEvent stop:
                StopRequested(_logger, stop.Reason, null);
                break;

            case ApplicationFailureEvent failure:
                Action<ILogger, ApplicationFailureCategory, bool, bool, string, Exception?> writeFailure =
                    failure.IsPrimary ? PrimaryFailure : SecondaryFailure;
                writeFailure(
                    _logger,
                    failure.Category,
                    failure.IsPrimary,
                    failure.IsExpected,
                    failure.FailureCode ?? "none",
                    null);
                break;

            case ApplicationTimeoutEvent timeout:
                TimedOut(
                    _logger,
                    timeout.TimeoutKind,
                    timeout.TotalShutdownDeadlineExpired,
                    null);
                break;

            case ApplicationCompletionEvent completion:
                Action<ILogger, bool, int?, int, Exception?> writeCompletion =
                    completion.IsSuccess ? Completed : CompletedWithFailure;
                writeCompletion(
                    _logger,
                    completion.IsSuccess,
                    completion.ExitCode,
                    completion.SecondaryFailureCount,
                    null);
                break;
        }
    }
}
