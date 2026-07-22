using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

/// <summary>
/// Executes one deterministic application lifecycle against explicitly supplied collaborators.
/// </summary>
public sealed class ApplicationLifecycleKernel : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly object _secondarySync = new();
    private readonly ApplicationLifecycleDescriptor _descriptor;
    private readonly TimeProvider _timeProvider;
    private readonly ApplicationLifecycleStateMachine _stateMachine = new();
    private readonly TaskCompletionSource<ApplicationRunResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly LifecycleStopController _stopController;
    private readonly List<IApplicationStartupParticipant> _startedParticipants = [];
    private readonly List<ApplicationFailure> _secondaryFailures = [];
    private ShutdownDeadline? _shutdownDeadline;
    private Outcome? _outcome;
    private Task? _disposeTask;
    private Task? _hostDisposeTask;
    private int _runStatus;
    private bool _hostStartAttempted;

    /// <summary>Initializes a lifecycle kernel.</summary>
    /// <param name="descriptor">The immutable lifecycle collaborators.</param>
    /// <param name="timeProvider">The clock used for every bounded wait.</param>
    public ApplicationLifecycleKernel(
        ApplicationLifecycleDescriptor descriptor,
        TimeProvider? timeProvider = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _stopController = new LifecycleStopController(_completion.Task, StartShutdownDeadline);
    }

    /// <summary>Gets the current serialized lifecycle state.</summary>
    public ApplicationState State => _stateMachine.State;

    /// <summary>Gets the task that completes with the stable terminal result after teardown.</summary>
    public Task<ApplicationRunResult> Completion => _completion.Task;

    /// <summary>Gets the controller through which competing stop sources converge.</summary>
    public IApplicationStopController StopController => _stopController;

    /// <summary>Gets safe cleanup failures that did not replace an earlier non-success result.</summary>
    public IReadOnlyList<ApplicationFailure> SecondaryFailures
    {
        get
        {
            lock (_secondarySync)
            {
                return _secondaryFailures.ToArray();
            }
        }
    }

    /// <summary>Runs the single-use lifecycle.</summary>
    /// <param name="decision">The side-effect-free launch decision.</param>
    /// <param name="cancellationToken">External process cancellation.</param>
    /// <returns>The primary result after all bounded cleanup has completed.</returns>
    /// <exception cref="InvalidOperationException">The kernel has already been run or disposed.</exception>
    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (Interlocked.CompareExchange(ref _runStatus, 1, 0) != 0)
        {
            throw new InvalidOperationException("A lifecycle kernel can be run exactly once.");
        }

        return RunCoreAsync(decision, cancellationToken);
    }

    /// <summary>Requests stop when necessary and disposes the lifecycle exactly once.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            if (Interlocked.CompareExchange(ref _runStatus, 2, 0) == 0)
            {
                _disposeTask = DisposeWithoutRunAsync();
            }
            else
            {
                _stopController.RequestStop(StopReason.Disposal);
                _disposeTask = AwaitCompletionAndMarkDisposedAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task<ApplicationRunResult> RunCoreAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((LifecycleStopController)state!).RequestStop(StopReason.ExternalCancellation),
            _stopController);
        using CancellationTokenSource lifecycleCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_stopController.Stopping);

        try
        {
            SetState(ApplicationState.Validating);
            await ValidateAsync(decision, lifecycleCancellation.Token).ConfigureAwait(false);

            if (_outcome is null && !_stopController.Stopping.IsCancellationRequested)
            {
                SetState(ApplicationState.Starting);
                await StartAsync(lifecycleCancellation.Token).ConfigureAwait(false);
            }

            if (_outcome is null && !_stopController.Stopping.IsCancellationRequested)
            {
                SetState(ApplicationState.Running);
                await ExecuteModeAsync(decision, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Cancelled,
                ApplicationFailureCodes.Cancellation,
                "Application execution was cancelled.")));
        }
        catch (OperationCanceledException) when (_stopController.Stopping.IsCancellationRequested)
        {
            CapturePrimary(ApplicationRunResult.FromExitCode(0));
        }
        catch (Exception exception)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Unhandled,
                ApplicationFailureCodes.RunnerFailure,
                "The application lifecycle failed unexpectedly.",
                exception,
                isExpected: false)));
        }
        finally
        {
            CaptureStopOutcomeIfMissing(cancellationToken);
            StopReason terminalReason = cancellationToken.IsCancellationRequested
                ? StopReason.ExternalCancellation
                : _outcome!.Result.IsSuccess
                    ? StopReason.ModeCompleted
                    : StopReason.FatalFailure;
            _stopController.RequestStop(terminalReason);
            SetState(ApplicationState.Stopping);
            try
            {
                await TeardownAsync(GetOrStartShutdownDeadline()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordCleanupFailure(CreateFailure(
                    ApplicationFailureCategory.Shutdown,
                    ApplicationFailureCodes.Dispose,
                    "Application teardown failed unexpectedly.",
                    exception,
                    isExpected: false));
            }

            ApplicationRunResult result = _outcome!.Result;
            SetState(result.IsSuccess ? ApplicationState.Stopped : ApplicationState.Faulted);
            _completion.TrySetResult(result);
        }

        return await _completion.Task.ConfigureAwait(false);
    }

    private async Task ValidateAsync(LaunchDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Kind == LaunchKind.Invalid)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Usage,
                ApplicationFailureCodes.Validation,
                "The launch intent is invalid.")));
            return;
        }

        List<ApplicationValidationError> errors = [];
        ApplicationValidationContext context = new(decision);
        foreach (IApplicationValidator validator in _descriptor.Validators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Task validationTask = validator.ValidateAsync(context, errors, cancellationToken).AsTask();
                Task winner = await Task.WhenAny(validationTask, _stopController.Requested).ConfigureAwait(false);
                if (winner != validationTask)
                {
                    Observe(validationTask);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await validationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CapturePrimary(CreateFailureResult(CreateFailure(
                    ApplicationFailureCategory.Configuration,
                    ApplicationFailureCodes.Validation,
                    "Application validation failed unexpectedly.",
                    exception,
                    isExpected: false)));
            }
        }

        if (errors.Count > 0)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Configuration,
                ApplicationFailureCodes.Validation,
                $"Application validation reported {errors.Count} error(s).")));
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        StartupDeadline deadline = new(_timeProvider, _descriptor.TimeoutSnapshot.StartupTimeout);
        _hostStartAttempted = true;
        try
        {
            await ExecuteBoundedAsync(
                token => _descriptor.Host.StartAsync(token),
                deadline.Remaining,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.HostStartup,
                ApplicationFailureCodes.StartupTimeout,
                "The application host exceeded the startup timeout.",
                exception)));
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.HostStartup,
                ApplicationFailureCodes.HostStart,
                "The application host failed to start.",
                exception)));
            return;
        }

        IEnumerable<IApplicationStartupParticipant> orderedParticipants =
            _descriptor.Participants.OrderBy(static participant => participant.Phase);

        foreach (IApplicationStartupParticipant participant in orderedParticipants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ExecuteBoundedAsync(
                    participant.StartAsync,
                    deadline.Remaining,
                    cancellationToken).ConfigureAwait(false);
                _startedParticipants.Add(participant);
            }
            catch (TimeoutException exception)
            {
                CapturePrimary(CreateFailureResult(CreateFailure(
                    ApplicationFailureCategory.HostStartup,
                    ApplicationFailureCodes.StartupTimeout,
                    "An application startup participant exceeded the startup timeout.",
                    exception)));
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CapturePrimary(CreateFailureResult(CreateFailure(
                    ApplicationFailureCategory.HostStartup,
                    ApplicationFailureCodes.ParticipantStart,
                    "An application startup participant failed.",
                    exception)));
                return;
            }
        }
    }

    private async Task ExecuteModeAsync(LaunchDecision decision, CancellationToken externalCancellation)
    {
        IApplicationModeRunner[] runners = _descriptor.ModeRunners
            .Where(runner => runner.Kind == decision.Kind)
            .ToArray();

        if (runners.Length != 1)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Configuration,
                ApplicationFailureCodes.RunnerSelection,
                "The selected launch kind must have exactly one mode runner.")));
            return;
        }

        using CancellationTokenSource activeMode =
            CancellationTokenSource.CreateLinkedTokenSource(_stopController.Stopping);

        Task<ApplicationRunResult> runnerTask;
        try
        {
            runnerTask = runners[0].RunAsync(decision, activeMode.Token);
            if (runnerTask is null)
            {
                throw new InvalidOperationException("A mode runner returned a null task.");
            }
        }
        catch (OperationCanceledException) when (externalCancellation.IsCancellationRequested)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Cancelled,
                ApplicationFailureCodes.Cancellation,
                "Application execution was cancelled.")));
            return;
        }
        catch (OperationCanceledException) when (_stopController.Stopping.IsCancellationRequested)
        {
            CapturePrimary(ApplicationRunResult.FromExitCode(0));
            return;
        }
        catch (Exception exception)
        {
            CaptureRunnerException(decision.Kind, exception);
            return;
        }

        Task winner = await Task.WhenAny(runnerTask, _stopController.Requested).ConfigureAwait(false);
        if (winner == runnerTask)
        {
            await CaptureRunnerResultAsync(decision.Kind, runnerTask, externalCancellation).ConfigureAwait(false);
            _stopController.RequestStop(StopReason.ModeCompleted);
            return;
        }

        CaptureStopOutcomeIfMissing(externalCancellation);
        CancelWithoutThrowing(activeMode);
        ShutdownDeadline deadline = GetOrStartShutdownDeadline();
        TimeSpan closeTimeout = decision.Kind == LaunchKind.UserInterface
            ? _descriptor.TimeoutSnapshot.WindowCloseTimeout
            : _descriptor.TimeoutSnapshot.ParticipantStopTimeout;
        TimeSpan remaining = deadline.Remaining;
        bool limitedByTotal = IsShorter(remaining, closeTimeout);
        TimeSpan boundedCloseTimeout = MinTimeout(remaining, closeTimeout);

        try
        {
            Task runnerCompletion = runnerTask.ContinueWith(
                static _ => { },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await WaitBoundedAsync(runnerCompletion, boundedCloseTimeout).ConfigureAwait(false);
            await CaptureRunnerResultAsync(decision.Kind, runnerTask, externalCancellation).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                limitedByTotal
                    ? ApplicationFailureCodes.TotalShutdownTimeout
                    : ApplicationFailureCodes.StopTimeout,
                limitedByTotal
                    ? "Application teardown exceeded the total shutdown timeout."
                    : "The active application mode did not stop within its timeout.",
                exception));
            Observe(runnerTask);
        }
    }

    private async Task CaptureRunnerResultAsync(
        LaunchKind kind,
        Task<ApplicationRunResult> runnerTask,
        CancellationToken externalCancellation)
    {
        try
        {
            ApplicationRunResult result = await runnerTask.ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException("A mode runner returned a null result.");
            }

            CaptureRunnerOutcome(Normalize(result));
        }
        catch (OperationCanceledException) when (externalCancellation.IsCancellationRequested)
        {
            CapturePrimary(CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Cancelled,
                ApplicationFailureCodes.Cancellation,
                "Application execution was cancelled.")));
        }
        catch (OperationCanceledException) when (_stopController.Stopping.IsCancellationRequested)
        {
            CapturePrimary(ApplicationRunResult.FromExitCode(0));
        }
        catch (Exception exception)
        {
            CaptureRunnerException(kind, exception);
        }
    }

    private void CaptureRunnerException(LaunchKind kind, Exception exception)
    {
        ApplicationFailureCategory category = kind switch
        {
            LaunchKind.Command => ApplicationFailureCategory.Command,
            LaunchKind.UserInterface => ApplicationFailureCategory.UserInterface,
            _ => ApplicationFailureCategory.Unhandled,
        };
        ApplicationFailure failure = CreateFailure(
            category,
            ApplicationFailureCodes.RunnerFailure,
            "The selected application mode failed.",
            exception,
            isExpected: false);
        if (_outcome is null)
        {
            CapturePrimary(CreateFailureResult(failure));
        }
        else
        {
            RecordSecondary(failure);
        }
    }

    private async Task TeardownAsync(ShutdownDeadline deadline)
    {
        for (int index = _startedParticipants.Count - 1; index >= 0; index--)
        {
            IApplicationStartupParticipant participant = _startedParticipants[index];
            await RunCleanupAsync(
                participant.StopAsync,
                _descriptor.TimeoutSnapshot.ParticipantStopTimeout,
                ApplicationFailureCodes.ParticipantStop,
                "An application startup participant failed to stop.",
                deadline).ConfigureAwait(false);
        }

        if (_hostStartAttempted)
        {
            await RunCleanupAsync(
                _descriptor.Host.StopAsync,
                _descriptor.TimeoutSnapshot.HostStopTimeout,
                ApplicationFailureCodes.HostStop,
                "The application host failed to stop.",
                deadline).ConfigureAwait(false);
        }

        await DisposeHostAsync(deadline).ConfigureAwait(false);
    }

    private async Task RunCleanupAsync(
        Func<CancellationToken, ValueTask> operation,
        TimeSpan operationTimeout,
        string failureCode,
        string safeMessage,
        ShutdownDeadline deadline)
    {
        TimeSpan remaining = deadline.Remaining;
        bool limitedByTotal = IsShorter(remaining, operationTimeout);
        TimeSpan timeout = MinTimeout(remaining, operationTimeout);
        using CancellationTokenSource operationCancellation = new();
        Task operationTask;

        try
        {
            if (timeout == TimeSpan.Zero)
            {
                CancelWithoutThrowing(operationCancellation);
            }

            operationTask = operation(operationCancellation.Token).AsTask();
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                failureCode,
                safeMessage,
                exception));
            return;
        }

        if (timeout == TimeSpan.Zero && !operationTask.IsCompletedSuccessfully)
        {
            Observe(operationTask);
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                ApplicationFailureCodes.TotalShutdownTimeout,
                "Application teardown exceeded the total shutdown timeout.",
                new TimeoutException()));
            return;
        }

        try
        {
            await WaitBoundedAsync(operationTask, timeout).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            CancelWithoutThrowing(operationCancellation);
            Observe(operationTask);
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                limitedByTotal ? ApplicationFailureCodes.TotalShutdownTimeout : ApplicationFailureCodes.StopTimeout,
                limitedByTotal
                    ? "Application teardown exceeded the total shutdown timeout."
                    : "An application teardown operation exceeded its timeout.",
                exception));
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                failureCode,
                safeMessage,
                exception));
        }
    }

    private Task DisposeHostOnceAsync()
    {
        lock (_sync)
        {
            return _hostDisposeTask ??= _descriptor.Host.DisposeAsync().AsTask();
        }
    }

    private async Task DisposeHostAsync(ShutdownDeadline deadline)
    {
        Task disposeTask;
        try
        {
            disposeTask = DisposeHostOnceAsync();
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                ApplicationFailureCodes.Dispose,
                "The application host failed to dispose.",
                exception));
            return;
        }

        try
        {
            await WaitBoundedAsync(disposeTask, deadline.Remaining).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            Observe(disposeTask);
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                ApplicationFailureCodes.TotalShutdownTimeout,
                "Application teardown exceeded the total shutdown timeout while disposing the host.",
                exception));
        }
        catch (Exception exception)
        {
            RecordCleanupFailure(CreateFailure(
                ApplicationFailureCategory.Shutdown,
                ApplicationFailureCodes.Dispose,
                "The application host failed to dispose.",
                exception));
        }
    }

    private async Task ExecuteBoundedAsync(
        Func<CancellationToken, ValueTask> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task operationTask = operation(operationCancellation.Token).AsTask();
        try
        {
            await operationTask.WaitAsync(timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            CancelWithoutThrowing(operationCancellation);
            Observe(operationTask);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(operationTask);
            throw;
        }
    }

    private async Task WaitBoundedAsync(Task task, TimeSpan timeout)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        if (timeout == TimeSpan.Zero)
        {
            throw new TimeoutException();
        }

        await task.WaitAsync(timeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DisposeWithoutRunAsync()
    {
        CapturePrimary(ApplicationRunResult.FromExitCode(0));
        _stopController.RequestStop(StopReason.Disposal);
        SetState(ApplicationState.Stopping);
        await TeardownAsync(GetOrStartShutdownDeadline()).ConfigureAwait(false);
        ApplicationRunResult result = _outcome!.Result;
        SetState(result.IsSuccess ? ApplicationState.Stopped : ApplicationState.Faulted);
        _completion.TrySetResult(result);
        SetState(ApplicationState.Disposed);
        _stopController.Dispose();
    }

    private async Task AwaitCompletionAndMarkDisposedAsync()
    {
        await _completion.Task.ConfigureAwait(false);
        SetState(ApplicationState.Disposed);
        _stopController.Dispose();
    }

    private void CaptureStopOutcomeIfMissing(CancellationToken externalCancellation)
    {
        if (_outcome is not null)
        {
            return;
        }

        CapturePrimary(externalCancellation.IsCancellationRequested
            ? CreateFailureResult(CreateFailure(
                ApplicationFailureCategory.Cancelled,
                ApplicationFailureCodes.Cancellation,
                "Application execution was cancelled."))
            : ApplicationRunResult.FromExitCode(0));
    }

    private void CapturePrimary(ApplicationRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Interlocked.CompareExchange(ref _outcome, new Outcome(result), null);
    }

    private void CaptureRunnerOutcome(ApplicationRunResult result)
    {
        Outcome? existing = Volatile.Read(ref _outcome);
        if (existing is null)
        {
            CapturePrimary(result);
            return;
        }

        if (result.Failure is not null)
        {
            RecordSecondary(result.Failure);
        }
    }

    private void RecordCleanupFailure(ApplicationFailure failure)
    {
        while (true)
        {
            Outcome? current = Volatile.Read(ref _outcome);
            if (current is null)
            {
                if (Interlocked.CompareExchange(
                    ref _outcome,
                    new Outcome(CreateFailureResult(failure)),
                    null) is null)
                {
                    return;
                }

                continue;
            }

            if (current.Result.IsSuccess)
            {
                if (Interlocked.CompareExchange(
                    ref _outcome,
                    new Outcome(CreateFailureResult(failure)),
                    current) == current)
                {
                    return;
                }

                continue;
            }

            RecordSecondary(failure);
            return;
        }
    }

    private void RecordSecondary(ApplicationFailure failure)
    {
        lock (_secondarySync)
        {
            _secondaryFailures.Add(failure);
        }
    }

    private ApplicationRunResult Normalize(ApplicationRunResult result)
    {
        return result.Failure is not null && result.ExitCode is null
            ? CreateFailureResult(result.Failure)
            : result;
    }

    private ApplicationRunResult CreateFailureResult(ApplicationFailure failure)
    {
        int exitCode;
        try
        {
            exitCode = _descriptor.ExitCodePolicy.GetExitCode(failure);
            if (exitCode < 0)
            {
                throw new InvalidOperationException("An exit-code policy returned a negative exit code.");
            }
        }
        catch (Exception exception)
        {
            exitCode = 1;
            RecordSecondary(CreateFailure(
                ApplicationFailureCategory.Unhandled,
                ApplicationFailureCodes.RunnerFailure,
                "The configured exit-code policy failed.",
                exception,
                isExpected: false));
        }

        return new ApplicationRunResult(exitCode, failure);
    }

    private static ApplicationFailure CreateFailure(
        ApplicationFailureCategory category,
        string code,
        string safeMessage,
        Exception? exception = null,
        bool isExpected = true)
    {
        return new ApplicationFailure(category, code, safeMessage, exception, isExpected);
    }

    private void SetState(ApplicationState next)
    {
        _stateMachine.Transition(next);
    }

    private static TimeSpan MinTimeout(TimeSpan first, TimeSpan second)
    {
        if (first == Timeout.InfiniteTimeSpan)
        {
            return second;
        }

        if (second == Timeout.InfiniteTimeSpan)
        {
            return first;
        }

        return first <= second ? first : second;
    }

    private static bool IsShorter(TimeSpan first, TimeSpan second)
    {
        return first != Timeout.InfiniteTimeSpan
            && (second == Timeout.InfiniteTimeSpan || first < second);
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void CancelWithoutThrowing(CancellationTokenSource cancellationSource)
    {
        try
        {
            cancellationSource.Cancel();
        }
        catch (AggregateException)
        {
            // Consumer cancellation callbacks cannot be allowed to interrupt the
            // kernel's bounded cleanup progression.
        }
    }

    private ShutdownDeadline GetOrStartShutdownDeadline()
    {
        ShutdownDeadline? existing = Volatile.Read(ref _shutdownDeadline);
        if (existing is not null)
        {
            return existing;
        }

        ShutdownDeadline created = new(_timeProvider, _descriptor.TimeoutSnapshot.TotalShutdownTimeout);
        return Interlocked.CompareExchange(ref _shutdownDeadline, created, null) ?? created;
    }

    private void StartShutdownDeadline()
    {
        _ = GetOrStartShutdownDeadline();
    }

    private sealed class Outcome(ApplicationRunResult result)
    {
        internal ApplicationRunResult Result { get; } = result;
    }

    private readonly struct StartupDeadline
    {
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _timeout;
        private readonly long _started;

        internal StartupDeadline(TimeProvider timeProvider, TimeSpan timeout)
        {
            _timeProvider = timeProvider;
            _timeout = timeout;
            _started = timeProvider.GetTimestamp();
        }

        internal TimeSpan Remaining
        {
            get
            {
                if (_timeout == Timeout.InfiniteTimeSpan)
                {
                    return Timeout.InfiniteTimeSpan;
                }

                TimeSpan remaining = _timeout - _timeProvider.GetElapsedTime(_started);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    private sealed class ShutdownDeadline
    {
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _timeout;
        private readonly long _started;

        internal ShutdownDeadline(TimeProvider timeProvider, TimeSpan timeout)
        {
            _timeProvider = timeProvider;
            _timeout = timeout;
            _started = timeProvider.GetTimestamp();
        }

        internal TimeSpan Remaining
        {
            get
            {
                if (_timeout == Timeout.InfiniteTimeSpan)
                {
                    return Timeout.InfiniteTimeSpan;
                }

                TimeSpan remaining = _timeout - _timeProvider.GetElapsedTime(_started);
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}
