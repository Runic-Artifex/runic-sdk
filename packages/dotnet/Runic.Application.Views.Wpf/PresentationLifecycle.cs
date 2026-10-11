using System.Diagnostics;

namespace Runic.Application.Views.Wpf;

// Serialises one host control's presentation changes. Free of WPF types so the portable tests cover the ordering.
// Call RequestAsync on the UI thread: the awaits keep its synchronization context, so open, release and fallback run there.
internal sealed class PresentationLifecycle
{
    private readonly Stack<Func<ValueTask>> _owned = new();
    private CancellationTokenSource? _opening;
    private int _generation;

    // The latest request's completion. It never faults.
    internal Task Settled { get; private set; } = Task.CompletedTask;
    internal RunicViewHostState State { get; private set; }
    internal Exception? Error { get; private set; }
    internal event Action? Changed;

    // Each request supersedes earlier ones: it cancels an open in progress, and a request still waiting for the
    // previous change is skipped, because this one applies the current state. Every change first releases what the
    // previous open acquired. `open` registers each resource with Own as it acquires it; `failed` may show a
    // fallback, also registered with Own.
    internal Task RequestAsync(Func<bool> wanted, Func<PresentationLifecycle, CancellationToken, Task> open,
        Action<PresentationLifecycle, Exception>? failed = null)
    {
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Read(ref _opening)?.Cancel();
        // Chained, not locked: each change starts after the previous one, which never faults.
        return Settled = RunAsync(Settled, generation, wanted, open, failed);
    }

    // Released in reverse order: a resource acquired later depends on the ones before it.
    internal void Own(Func<ValueTask> release) => _owned.Push(release);

    internal void Own(Action release) => Own(() =>
    {
        release();
        return ValueTask.CompletedTask;
    });

    private async Task RunAsync(Task previous, int generation, Func<bool> wanted, Func<PresentationLifecycle, CancellationToken, Task> open,
        Action<PresentationLifecycle, Exception>? failed)
    {
        await previous;
        try
        {
            if (generation != Volatile.Read(ref _generation)) return;
            await ReleaseAsync();
            if (!wanted())
            {
                Set(RunicViewHostState.Closed, null);
                return;
            }
            var opening = new CancellationTokenSource();
            Volatile.Write(ref _opening, opening);
            // A request between the generation check and publishing the source found nothing to cancel.
            if (generation != Volatile.Read(ref _generation)) opening.Cancel();
            Set(RunicViewHostState.Opening, null);
            try
            {
                await open(this, opening.Token);
                opening.Token.ThrowIfCancellationRequested();
                Set(RunicViewHostState.Open, null);
            }
            catch (OperationCanceledException) when (opening.IsCancellationRequested)
            {
                await ReleaseAsync();
                Set(RunicViewHostState.Closed, null);
            }
            catch (Exception error)
            {
                // A presentation failure: the borrowed model and services stay, and the fallback presents them.
                await ReleaseAsync();
                try { failed?.Invoke(this, error); }
                catch (Exception fallbackError) { Trace.TraceError($"RunicViewHost fallback failed: {fallbackError}"); }
                Set(RunicViewHostState.Failed, error);
            }
            finally { Interlocked.CompareExchange(ref _opening, null, opening); }
        }
        catch (Exception error) { Trace.TraceError($"RunicViewHost presentation change failed: {error}"); }
    }

    private async Task ReleaseAsync()
    {
        while (_owned.TryPop(out var release))
        {
            try { await release(); }
            catch (Exception error) { Trace.TraceError($"RunicViewHost presentation cleanup failed: {error}"); }
        }
    }

    private void Set(RunicViewHostState state, Exception? error)
    {
        if (State == state && ReferenceEquals(Error, error)) return;
        (State, Error) = (state, error);
        Changed?.Invoke();
    }
}
