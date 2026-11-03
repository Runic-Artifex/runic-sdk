namespace Runic.Navigation;

/// <summary>
/// Writes the drafts that presentations hold before a departure is decided: a web page's field that waits to write
/// what the user typed (<c>commit: "pause"</c>), for example. The navigator flushes before it asks the departure
/// guards of a transition that has any, and a Runic Window before its close guard, so a guard that reads
/// <c>IsDirty</c> sees the user's last input. Presentation hosts register a flush; applications rarely call it.
/// </summary>
/// <remarks>
/// <c>Runic.Application.Views</c> registers each window's pages: it asks every page that uses the <c>field-flush/1</c>
/// feature to write its drafts and waits up to a short limit for them. A flush must not wait for the model context's
/// turn: the drafts it writes run in turns of their own.
/// </remarks>
public static class RunicDraftFlush
{
    private static readonly Lock Gate = new();
    private static Func<CancellationToken, ValueTask>[] _flushes = [];

    /// <summary>Registers a flush until the result is disposed.</summary>
    public static IDisposable Register(Func<CancellationToken, ValueTask> flush)
    {
        ArgumentNullException.ThrowIfNull(flush);
        lock (Gate) _flushes = [.. _flushes, flush];
        return new Registration(flush);
    }

    /// <summary>
    /// Runs every registered flush at once and completes when all completed. A flush that fails or is cancelled does
    /// not fail the others or the caller: the departure is then decided on the state as it is.
    /// </summary>
    public static async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        var flushes = Volatile.Read(ref _flushes);
        if (flushes.Length == 0) return;
        var running = new Task[flushes.Length];
        for (var index = 0; index < flushes.Length; index++)
        {
            var flush = flushes[index];
            running[index] = Run(flush, cancellationToken);
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private static async Task Run(Func<CancellationToken, ValueTask> flush, CancellationToken cancellationToken)
    {
        try { await flush(cancellationToken).ConfigureAwait(false); }
#pragma warning disable CA1031 // A presentation that cannot flush does not decide the departure.
        catch (Exception) { }
#pragma warning restore CA1031
    }

    private sealed class Registration(Func<CancellationToken, ValueTask> flush) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate) _flushes = [.. _flushes.Where(item => item != flush)];
        }
    }
}
