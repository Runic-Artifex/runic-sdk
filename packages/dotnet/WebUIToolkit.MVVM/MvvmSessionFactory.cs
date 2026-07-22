using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace WebUIToolkit.MVVM;

/// <summary>A reflection-free registry of closed session activators.</summary>
public sealed class MvvmSessionRegistry
{
    private readonly Dictionary<MvvmContract, MvvmSessionActivator> _activators = [];

    /// <summary>Registers one logical contract using ordinal, case-sensitive identity.</summary>
    /// <exception cref="InvalidOperationException">The contract is already registered.</exception>
    public MvvmSessionRegistry Map(MvvmContract contract, MvvmSessionActivator activator)
    {
        if (string.IsNullOrEmpty(contract.Value))
        {
            throw new ArgumentException("A contract cannot be empty.", nameof(contract));
        }

        ArgumentNullException.ThrowIfNull(activator);
        if (!_activators.TryAdd(contract, activator))
        {
            throw new InvalidOperationException($"The MVVM contract '{contract}' is already registered.");
        }

        return this;
    }

    /// <summary>Builds an independently owned session factory.</summary>
    public IMvvmSessionFactory Build(MvvmLimits? limits = null)
    {
        MvvmLimits selectedLimits = limits ?? MvvmLimits.Default;
        selectedLimits.Validate();
        return new MvvmSessionFactory(new Dictionary<MvvmContract, MvvmSessionActivator>(_activators), selectedLimits);
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

        if (!_activators.TryGetValue(contract, out MvvmSessionActivator? activator))
        {
            throw new KeyNotFoundException($"The MVVM contract '{contract}' is not registered.");
        }

        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeState != 0, this);
            if (_admittedSessions >= _limits.MaxSessions)
            {
                throw new InvalidOperationException("The configured MVVM session limit was exceeded.");
            }

            _admittedSessions++;
            _openOperations++;
        }

        MvvmSessionActivation? activation = null;
        bool admissionOwnedByOpen = true;
        try
        {
            using var activationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            activation = await activator(activationCancellation.Token).ConfigureAwait(false);
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

            return session;
        }
        finally
        {
            try
            {
                if (activation is not null)
                {
                    await DisposeActivationAsync(activation).ConfigureAwait(false);
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
            _shutdown.Cancel();
            await _opensDrained.Task.ConfigureAwait(false);
            foreach (MvvmSession session in sessions)
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
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

    private static async ValueTask DisposeActivationAsync(MvvmSessionActivation activation)
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

    private ValueTask OnSessionClosedAsync(MvvmSession session)
    {
        lock (_lifetimeGate)
        {
            if (_sessions.TryRemove(session.Id, out _))
            {
                _admittedSessions--;
            }
        }

        return ValueTask.CompletedTask;
    }
}
