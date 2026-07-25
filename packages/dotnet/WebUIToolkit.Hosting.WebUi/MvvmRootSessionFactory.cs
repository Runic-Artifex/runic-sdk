using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.Hosting.WebUi;

/// <summary>
/// Opens a typed MVVM contract inside one asynchronous dependency-injection scope.
/// </summary>
public sealed class MvvmRootSessionFactory : IRootSessionFactory
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MvvmContract _contract;

    /// <summary>Initializes a closed root-session bridge.</summary>
    public MvvmRootSessionFactory(IServiceScopeFactory scopeFactory, MvvmContract contract)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _contract = contract;
    }

    /// <inheritdoc />
    public async ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken)
    {
        AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        try
        {
            IMvvmSessionFactory sessionFactory =
                scope.ServiceProvider.GetRequiredService<IMvvmSessionFactory>();
            IMvvmSession session = await sessionFactory
                .OpenAsync(_contract, cancellationToken)
                .ConfigureAwait(false);
            IMvvmRootActivation? activation =
                scope.ServiceProvider.GetService<IMvvmRootActivation>();
            return new ScopedMvvmRootSession(scope, sessionFactory, session, activation);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class ScopedMvvmRootSession : IRootSession
    {
        private readonly AsyncServiceScope _scope;
        private readonly IMvvmSessionFactory _sessionFactory;
        private readonly IMvvmRootActivation? _activation;
        private IMvvmSession? _session;
        private int _active;
        private int _disposed;

        internal ScopedMvvmRootSession(
            AsyncServiceScope scope,
            IMvvmSessionFactory sessionFactory,
            IMvvmSession session,
            IMvvmRootActivation? activation)
        {
            _scope = scope;
            _sessionFactory = sessionFactory;
            _session = session;
            _activation = activation;
        }

        public async ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _active, 1, 0) == 0 && _activation is not null)
            {
                await _activation
                    .ActivateAsync(_session!, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public async ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0
                || Interlocked.CompareExchange(ref _active, 0, 1) != 1)
            {
                return;
            }

            if (_activation is not null)
            {
                await _activation
                    .DeactivateAsync(_session!, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            IMvvmSession? session = Interlocked.Exchange(ref _session, null);
            Exception? failure = null;
            if (session is not null)
            {
                try
                {
                    if (Interlocked.Exchange(ref _active, 0) != 0 && _activation is not null)
                    {
                        await _activation
                            .DeactivateAsync(session, CancellationToken.None)
                            .ConfigureAwait(false);
                    }

                    await _sessionFactory.CloseAsync(session.Id).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            try
            {
                await _scope.DisposeAsync().ConfigureAwait(false);
            }
            catch when (failure is not null)
            {
                // The first root-session failure retains precedence.
            }

            if (failure is not null)
            {
                throw failure;
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
