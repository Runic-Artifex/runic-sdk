using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RunicToolkit.MVVM;

/// <summary>A reflection-free registry of closed session activators.</summary>
public sealed class MvvmSessionRegistry
{
    private readonly Dictionary<MvvmContract, MvvmSessionActivator> _activators = [];
    private readonly object _gate = new();

    /// <summary>Registers one logical contract using ordinal, case-sensitive identity.</summary>
    /// <exception cref="InvalidOperationException">The contract is already registered.</exception>
    public MvvmSessionRegistry Map(MvvmContract contract, MvvmSessionActivator activator)
    {
        if (string.IsNullOrEmpty(contract.Value))
        {
            throw new ArgumentException("A contract cannot be empty.", nameof(contract));
        }

        ArgumentNullException.ThrowIfNull(activator);
        lock (_gate)
        {
            if (!_activators.TryAdd(contract, activator))
            {
                throw new InvalidOperationException($"The MVVM contract '{contract}' is already registered.");
            }
        }

        return this;
    }

    /// <summary>Builds an independently owned session factory.</summary>
    public IMvvmSessionFactory Build(MvvmLimits? limits = null)
    {
        MvvmLimits selectedLimits = limits ?? MvvmLimits.Default;
        selectedLimits.Validate();
        lock (_gate)
        {
            return new MvvmSessionFactory(new Dictionary<MvvmContract, MvvmSessionActivator>(_activators), selectedLimits);
        }
    }
}

internal sealed class MvvmSessionFactory : IMvvmSessionFactory
{
    private readonly IReadOnlyDictionary<MvvmContract, MvvmSessionActivator> _activators;
    private readonly MvvmLimits _limits;
    private readonly ConcurrentDictionary<MvvmSessionId, MvvmSession> _sessions = new();
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _opensDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private int _admittedSessions;
    private int _openOperations;
    private int _disposeState;

    internal MvvmSessionFactory(IReadOnlyDictionary<MvvmContract, MvvmSessionActivator> activators, MvvmLimits limits)
    {
        _activators = activators;
        _limits = limits;
    }

    public async ValueTask<IMvvmSession> OpenAsync(MvvmContract contract, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(contract.Value))
        {
            throw new ArgumentException("A contract cannot be empty.", nameof(contract));
        }

        long startedTimestamp = Stopwatch.GetTimestamp();
        using MvvmActivity activity = MvvmTelemetry.StartSessionOpen();
        if (!_activators.TryGetValue(contract, out MvvmSessionActivator? activator))
        {
            MvvmTelemetry.SessionOpenFailed(activity, startedTimestamp, "contract_unknown");
            throw new KeyNotFoundException($"The MVVM contract '{contract}' is not registered.");
        }

        bool outcomeRecorded = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool sessionLimitExceeded;
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposeState != 0, this);
                sessionLimitExceeded = _admittedSessions >= _limits.MaxSessions;
                if (!sessionLimitExceeded)
                {
                    _admittedSessions++;
                    _openOperations++;
                }
            }

            if (sessionLimitExceeded)
            {
                MvvmTelemetry.BackpressureRejected("sessions");
                MvvmTelemetry.SessionOpenFailed(activity, startedTimestamp, "limit_exceeded");
                outcomeRecorded = true;
                throw new InvalidOperationException("The configured MVVM session limit was exceeded.");
            }

            MvvmSessionActivation? activation = null;
            bool admissionOwnedByOpen = true;
            try
            {
                using var activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                Task<MvvmSessionActivation> activationTask = Task.Run(
                    async () => await activator(activationCancellation.Token).ConfigureAwait(false));
                try
                {
                    activation = await activationTask.WaitAsync(activationCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (activationCancellation.IsCancellationRequested)
                {
                    if (activationTask.IsCompleted)
                    {
                        // Observe the terminal task even if cancellation won WaitAsync's race.
                        activation = await activationTask.ConfigureAwait(false);
                    }
                    else if (_shutdown.IsCancellationRequested)
                    {
                        try
                        {
                            activation = await activationTask
                                .WaitAsync(_limits.MaxShutdownDuration, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (TimeoutException)
                        {
                            admissionOwnedByOpen = false;
                            _ = ObserveLateActivationAndReleaseAdmissionAsync(
                                activationTask,
                                _limits.MaxShutdownDuration);
                            throw new ObjectDisposedException(nameof(MvvmSessionFactory));
                        }
                    }
                    else
                    {
                        admissionOwnedByOpen = false;
                        _ = ObserveLateActivationAndReleaseAdmissionAsync(
                            activationTask,
                            _limits.MaxShutdownDuration);
                        throw;
                    }
                }

                if (activation is null)
                {
                    throw new InvalidOperationException("The MVVM session activator returned no activation.");
                }
                MvvmSession session;
                lock (_lifetimeGate)
                {
                    ObjectDisposedException.ThrowIf(_disposeState != 0, this);
                    cancellationToken.ThrowIfCancellationRequested();
                    MvvmSessionId sessionId;
                    do
                    {
                        sessionId = new MvvmSessionId(Guid.NewGuid());
                    }
                    while (_sessions.ContainsKey(sessionId));

                    session = new MvvmSession(
                        sessionId,
                        contract,
                        CreateCapabilityToken(),
                        activation,
                        _limits,
                        OnSessionClosedAsync);
                    if (!_sessions.TryAdd(session.Id, session))
                    {
                        throw new InvalidOperationException("A unique MVVM session identifier could not be allocated.");
                    }

                    activation = null;
                    admissionOwnedByOpen = false;
                }

                MvvmTelemetry.SessionOpenSucceeded(activity, startedTimestamp);
                outcomeRecorded = true;
                return session;
            }
            finally
            {
                try
                {
                    if (activation is not null)
                    {
                        bool disposalFinished = await DisposeActivationBoundedAsync(
                            activation,
                            _limits.MaxShutdownDuration,
                            releaseAdmissionWhenLate: true).ConfigureAwait(false);
                        if (!disposalFinished)
                        {
                            admissionOwnedByOpen = false;
                        }
                    }
                }
                finally
                {
                    if (admissionOwnedByOpen)
                    {
                        lock (_lifetimeGate)
                        {
                            _admittedSessions--;
                        }
                    }

                    lock (_lifetimeGate)
                    {
                        _openOperations--;
                        if (_disposeState != 0 && _openOperations == 0)
                        {
                            _opensDrained.TrySetResult();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!outcomeRecorded)
            {
                MvvmTelemetry.SessionOpenFailed(activity, startedTimestamp, "cancelled");
            }

            throw;
        }
        catch (ObjectDisposedException)
        {
            if (!outcomeRecorded)
            {
                MvvmTelemetry.SessionOpenFailed(activity, startedTimestamp, "disposed");
            }

            throw;
        }
        catch
        {
            if (!outcomeRecorded)
            {
                MvvmTelemetry.SessionOpenFailed(activity, startedTimestamp, "activation_failed");
            }

            throw;
        }
    }

    public async ValueTask<bool> CloseAsync(MvvmSessionId sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out MvvmSession? session))
        {
            return false;
        }

        await session.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        MvvmSession[] sessions;
        bool ownsDisposal;
        lock (_lifetimeGate)
        {
            ownsDisposal = _disposeState == 0;
            if (ownsDisposal)
            {
                _disposeState = 1;
                sessions = _sessions.Values.ToArray();
                if (_openOperations == 0)
                {
                    _opensDrained.TrySetResult();
                }
            }
            else
            {
                sessions = [];
            }
        }

        if (!ownsDisposal)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? firstFailure = null;
        try
        {
            Task cancellation = Task.Run(async () =>
            {
                try
                {
                    await _shutdown.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Activation cancellation callbacks are consumer code and cannot break teardown.
                }
            });
            Task[] sessionDisposals = sessions
                .Select(static session => Task.Run(
                    async () => await session.DisposeAsync().ConfigureAwait(false)))
                .ToArray();
            Task teardown = Task.WhenAll([cancellation, _opensDrained.Task, .. sessionDisposals]);
            try
            {
                await teardown.WaitAsync(_limits.MaxShutdownDuration).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = ObserveFailureAsync(teardown);
                // Remaining consumer work is quarantined; disposing it concurrently is unsafe.
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        }
        finally
        {
            if (firstFailure is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(firstFailure);
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private static string CreateCapabilityToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async ValueTask<bool> DisposeActivationBoundedAsync(
        MvvmSessionActivation activation,
        TimeSpan shutdownDuration,
        bool releaseAdmissionWhenLate = false)
    {
        Task disposal = Task.Run(async () => await DisposeActivationCoreAsync(activation).ConfigureAwait(false));
        try
        {
            await disposal.WaitAsync(shutdownDuration).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            if (releaseAdmissionWhenLate)
            {
                _ = ObserveDisposalAndReleaseAdmissionAsync(disposal);
            }
            else
            {
                _ = ObserveFailureAsync(disposal);
            }

            // Do not dispose dependent resources concurrently with a stuck adapter callback.
            return false;
        }
    }

    private static async Task DisposeActivationCoreAsync(MvvmSessionActivation activation)
    {
        Exception? firstFailure = null;
        try
        {
            await activation.Adapter.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        object[] resources = activation.OwnedResources;
        for (int index = resources.Length - 1; index >= 0; index--)
        {
            try
            {
                switch (resources[index])
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            throw firstFailure;
        }
    }

    private async Task ObserveLateActivationAndReleaseAdmissionAsync(
        Task<MvvmSessionActivation> activationTask,
        TimeSpan shutdownDuration)
    {
        bool releaseAdmission = false;
        try
        {
            MvvmSessionActivation activation = await activationTask.ConfigureAwait(false);
            if (activation is not null)
            {
                try
                {
                    releaseAdmission = await DisposeActivationBoundedAsync(
                        activation,
                        shutdownDuration,
                        releaseAdmissionWhenLate: true).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Ordered cleanup completed and its failure is observed, so the slot is safe to release.
                    releaseAdmission = true;
                }
            }
            else
            {
                releaseAdmission = true;
            }
        }
        catch (Exception)
        {
            // A canceled or failed activation owns no returned resources and is safe to release.
            releaseAdmission = true;
        }

        if (releaseAdmission)
        {
            ReleaseAdmission();
        }
    }

    private async Task ObserveDisposalAndReleaseAdmissionAsync(Task disposal)
    {
        await ObserveFailureAsync(disposal).ConfigureAwait(false);
        ReleaseAdmission();
    }

    private void ReleaseAdmission()
    {
        lock (_lifetimeGate)
        {
            _admittedSessions--;
        }
    }

    private static async Task ObserveFailureAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Detached consumer work is observed so it cannot become an unobserved failure.
        }
    }

    private ValueTask OnSessionClosedAsync(MvvmSession session)
    {
        bool removed;
        lock (_lifetimeGate)
        {
            removed = _sessions.TryRemove(session.Id, out _);
            if (removed)
            {
                _admittedSessions--;
            }
        }

        if (removed)
        {
            MvvmTelemetry.SessionClosed();
        }

        return ValueTask.CompletedTask;
    }
}
