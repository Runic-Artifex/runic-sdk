namespace Runic.Desktop.Internal;

// Cancellation can claim queued work, but cannot abandon a callback that already
// owns native resources. The caller observes its eventual completion in that case.
internal sealed class NativeDispatchWork(Action action, CancellationToken cancellationToken)
{
    private int _claimed;
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task WaitAsync()
    {
        using var registration = cancellationToken.UnsafeRegister(static state => ((NativeDispatchWork)state!).Cancel(), this);
        await Completion.Task.ConfigureAwait(false);
    }

    private void Cancel()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) == 0) Completion.TrySetCanceled(cancellationToken);
    }

    internal void Run()
    {
        if (cancellationToken.IsCancellationRequested) Cancel();
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;
        try { action(); Completion.TrySetResult(); }
        catch (Exception error) { Completion.TrySetException(error); }
    }
}
