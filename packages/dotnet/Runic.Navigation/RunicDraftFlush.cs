using System.Runtime.CompilerServices;

namespace Runic.Navigation;

/// <summary>
/// Writes the drafts that presentations of a model context hold before a departure is decided: a web page's field that
/// waits to write what the user typed (<c>commit: "pause"</c>), for example. The navigator flushes its
/// <see cref="RunicNavigator.ModelContext"/> before it asks the departure guards of a transition that has any, so a
/// guard that reads <c>IsDirty</c> sees the user's last input. Presentation hosts register a flush; applications rarely
/// call it.
/// </summary>
/// <remarks>
/// <para>
/// Flushes are per model context: a departure flushes the presentations of its own models, not those of another
/// window with another context. <c>Runic.Application.Views</c> registers each window's session with the window's
/// context, until the session is disposed: it asks every page that uses the <c>field-flush/1</c> feature to write its
/// drafts and waits up to a short limit for them.
/// </para>
/// <para>
/// A flush must not wait for the model context's turn: the drafts it writes run in turns of their own.
/// </para>
/// </remarks>
public static class RunicDraftFlush
{
    private static readonly ConditionalWeakTable<IRunicModelContext, Registrations> Contexts = new();

    /// <summary>Registers a flush of <paramref name="context"/>'s presentations until the result is disposed.</summary>
    public static IDisposable Register(IRunicModelContext context, Func<CancellationToken, ValueTask> flush)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flush);
        var registrations = Contexts.GetValue(context, static _ => new Registrations());
        var registration = new Registration(registrations, flush);
        lock (registrations.Gate) registrations.Items = [.. registrations.Items, registration];
        return registration;
    }

    /// <summary>
    /// Runs every flush registered for <paramref name="context"/> at once and completes when all completed. A flush
    /// that fails or is cancelled does not fail the others or the caller: the departure is then decided on the state as
    /// it is.
    /// </summary>
    public static async ValueTask FlushAsync(IRunicModelContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Contexts.TryGetValue(context, out var registrations)) return;
        var items = Volatile.Read(ref registrations.Items);
        if (items.Length == 0) return;
        var running = new Task[items.Length];
        for (var index = 0; index < items.Length; index++) running[index] = Run(items[index].Flush, cancellationToken);
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private static async Task Run(Func<CancellationToken, ValueTask> flush, CancellationToken cancellationToken)
    {
        try { await flush(cancellationToken).ConfigureAwait(false); }
#pragma warning disable CA1031 // A presentation that cannot flush does not decide the departure.
        catch (Exception) { }
#pragma warning restore CA1031
    }

    private sealed class Registrations
    {
        internal readonly Lock Gate = new();
        internal Registration[] Items = [];
    }

    private sealed class Registration(Registrations owner, Func<CancellationToken, ValueTask> flush) : IDisposable
    {
        private int _disposed;

        internal Func<CancellationToken, ValueTask> Flush { get; } = flush;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // By identity: two registrations of the same delegate are two registrations.
            lock (owner.Gate) owner.Items = [.. owner.Items.Where(item => !ReferenceEquals(item, this))];
        }
    }
}
