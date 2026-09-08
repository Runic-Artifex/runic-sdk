namespace Runic.Platform.Runtime;

/// <summary>The native presentation owner is no longer usable.</summary>
public sealed class OwnerClosedException() : InvalidOperationException("The presentation owner has closed.");

/// <summary>Dispatches once through a synchronous posting adapter, guarded by presentation shutdown.</summary>
public sealed class PresentationDispatcher(PresentationLifetime lifetime, Func<bool> checkAccess, Action<Action> post)
    : IUiDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() => checkAccess();

    /// <inheritdoc />
    public async ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = lifetime.TryBeginOperation() ?? throw new OwnerClosedException();
        var work = new Work(action, lifetime, cancellationToken);
        using var callerRegistration = cancellationToken.Register(() => work.Cancel(cancellationToken));
        using var ownerRegistration = lifetime.Shutdown.Register(work.Close);
        if (CheckAccess()) work.Execute();
        else
        {
            try { post(work.Execute); }
            catch (Exception error) { work.Reject(error); }
        }
        await work.Completion.ConfigureAwait(false);
    }

    private sealed class Work(Action action, PresentationLifetime lifetime, CancellationToken caller)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;
        internal Task Completion => _completion.Task;

        internal void Execute()
        {
            if (caller.IsCancellationRequested) { Cancel(caller); return; }
            if (lifetime.IsClosing) { Close(); return; }
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
            try { action(); _completion.TrySetResult(); }
            catch (Exception error) { _completion.TrySetException(error); }
        }

        internal void Cancel(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0) _completion.TrySetCanceled(token);
        }

        internal void Close() => Reject(new OwnerClosedException());
        internal void Reject(Exception error)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0) _completion.TrySetException(error);
        }
    }
}
