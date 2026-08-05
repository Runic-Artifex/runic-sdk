using System;
using System.Collections.Generic;
using System.Threading;

namespace RunicToolkit.Hosting;

/// <summary>Serializes lifecycle-event creation and exception-isolated sink delivery.</summary>
internal sealed class LifecycleEventPublisher
{
    private readonly object _sync = new();
    private readonly Queue<PendingDelivery> _pendingDeliveries = new();
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationLifecycleEventSink? _sink;
    private long _sequence;
    private bool _isDraining;

    internal LifecycleEventPublisher(
        TimeProvider timeProvider,
        IApplicationLifecycleEventSink? sink)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _sink = sink;
    }

    internal void StateTransition(ApplicationState previousState, ApplicationState currentState) =>
        Publish((sequence, timestamp) =>
            new ApplicationStateTransitionEvent(sequence, timestamp, previousState, currentState));

    internal void LaunchSelected(LaunchKind launchKind) =>
        Publish((sequence, timestamp) => new ApplicationLaunchEvent(sequence, timestamp, launchKind));

    internal void StopRequested(StopReason reason) =>
        Publish((sequence, timestamp) => new ApplicationStopRequestedEvent(sequence, timestamp, reason));

    internal void Failure(ApplicationFailure failure, bool isPrimary)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Publish((sequence, timestamp) => new ApplicationFailureEvent(
            sequence,
            timestamp,
            isPrimary,
            failure.Category,
            SanitizeFailureCode(failure.Code),
            failure.IsExpected));
    }

    internal void Timeout(
        ApplicationLifecycleTimeoutKind timeoutKind,
        bool totalShutdownDeadlineExpired = false) =>
        Publish((sequence, timestamp) => new ApplicationTimeoutEvent(
            sequence,
            timestamp,
            timeoutKind,
            totalShutdownDeadlineExpired));

    internal void Completion(ApplicationRunResult result, int secondaryFailureCount)
    {
        ArgumentNullException.ThrowIfNull(result);
        Publish((sequence, timestamp) => new ApplicationCompletionEvent(
            sequence,
            timestamp,
            result.ExitCode,
            result.IsSuccess,
            secondaryFailureCount));
    }

    private void Publish(Func<long, DateTimeOffset, ApplicationLifecycleEvent> eventFactory)
    {
        if (_sink is null)
        {
            return;
        }

        bool shouldScheduleDrain;
        lock (_sync)
        {
            try
            {
                DateTimeOffset timestamp = _timeProvider.GetUtcNow();
                long sequence = checked(++_sequence);
                _pendingDeliveries.Enqueue(
                    new PendingDelivery(eventFactory(sequence, timestamp)));
            }
            catch (Exception)
            {
                return;
            }

            shouldScheduleDrain = !_isDraining;
            if (shouldScheduleDrain)
            {
                _isDraining = true;
            }
        }

        if (shouldScheduleDrain)
        {
            ThreadPool.QueueUserWorkItem(
                static state => ((LifecycleEventPublisher)state!).Drain(),
                this);
        }
    }

    private void Drain()
    {
        while (true)
        {
            PendingDelivery pendingDelivery;
            lock (_sync)
            {
                if (!_pendingDeliveries.TryDequeue(out pendingDelivery!))
                {
                    _isDraining = false;
                    return;
                }
            }

            try
            {
                _sink!.Publish(pendingDelivery.LifecycleEvent);
            }
            catch (Exception)
            {
                // Observability is never allowed to change lifecycle progress or outcome.
            }
        }
    }

    private static string? SanitizeFailureCode(string? failureCode)
    {
        const string hostingDiagnosticPrefix = "RTKHOST";
        const int diagnosticSuffixLength = 4;
        if (failureCode is null
            || failureCode.Length != hostingDiagnosticPrefix.Length + diagnosticSuffixLength
            || !failureCode.StartsWith(hostingDiagnosticPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int suffixStart = failureCode.Length - diagnosticSuffixLength;
        for (int index = suffixStart; index < failureCode.Length; index++)
        {
            if (failureCode[index] is < '0' or > '9')
            {
                return null;
            }
        }

        return failureCode;
    }

    private sealed class PendingDelivery(ApplicationLifecycleEvent lifecycleEvent)
    {
        internal ApplicationLifecycleEvent LifecycleEvent { get; } = lifecycleEvent;
    }
}
