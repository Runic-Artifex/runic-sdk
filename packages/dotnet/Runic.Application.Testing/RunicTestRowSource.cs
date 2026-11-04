using System.Linq.Expressions;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>
/// An in-memory <see cref="IRunicRowSource{TRow, TKey}"/> for tests of query collections: it serves a list through
/// <see cref="RunicQueryFields{TRow}"/>, with optional latency, and records every load, count and search, so a test can
/// assert what a page's scrolling and sorting loaded.
/// </summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TKey">The row key.</typeparam>
/// <remarks>
/// Latency waits on <see cref="TimeProvider"/>: with the test host's <c>FakeTimeProvider</c>, loads complete when the
/// test advances it. <see cref="FailNext"/> makes the next load throw, as a database outage does.
/// </remarks>
public sealed class RunicTestRowSource<TRow, TKey> : IRunicRowSource<TRow, TKey> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly RunicQueryableRowSource<TRow, TKey> _source;
    private readonly TimeProvider _time;
    private readonly List<RunicRowLoad> _loads = [];
    private TRow[] _rows;
    private int _counts;
    private int _searches;
    private int _active;
    private int _maximumActive;
    private Exception? _failure;

    /// <summary>Creates a source of <paramref name="rows"/>.</summary>
    /// <param name="rows">The rows, in the source's own order.</param>
    /// <param name="fields">The fields a page may sort and filter by.</param>
    /// <param name="key">The row key.</param>
    /// <param name="timeProvider">The clock latency waits on; <see cref="TimeProvider.System"/> by default.</param>
    public RunicTestRowSource(IEnumerable<TRow> rows, RunicQueryFields<TRow> fields, Expression<Func<TRow, TKey>> key,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _rows = [.. rows];
        _time = timeProvider ?? TimeProvider.System;
        _source = new(() => Volatile.Read(ref _rows).AsQueryable(), fields, key);
    }

    /// <summary>How long each load, count and search takes. Zero by default.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Every load, oldest first: its query and range.</summary>
    public IReadOnlyList<RunicRowLoad> Loads
    {
        get { lock (_gate) return [.. _loads]; }
    }

    /// <summary>The rows loaded so far, summed over <see cref="Loads"/>.</summary>
    public long RowsLoaded
    {
        get { lock (_gate) return _loads.Sum(load => (long)load.Returned); }
    }

    /// <summary>The counts taken so far.</summary>
    public int Counts => Volatile.Read(ref _counts);

    /// <summary>The key searches (<c>IndexOfAsync</c>) so far.</summary>
    public int Searches => Volatile.Read(ref _searches);

    /// <summary>The most loads, counts and searches that ran at once.</summary>
    public int MaximumConcurrency => Volatile.Read(ref _maximumActive);

    /// <summary>Replaces the rows. Call the collection's <c>Refresh</c> or <c>Invalidate</c> afterwards, as after a database change.</summary>
    /// <param name="rows">The new rows.</param>
    public void SetRows(IEnumerable<TRow> rows) => Volatile.Write(ref _rows, [.. rows]);

    /// <summary>Makes the next load throw <paramref name="failure"/>.</summary>
    /// <param name="failure">The exception.</param>
    public void FailNext(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate) _failure = failure;
    }

    /// <inheritdoc />
    public async ValueTask<int> CountAsync(RunicQuery query, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _counts);
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await _source.CountAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<RunicRowPage<TRow>> LoadAsync(RunicQuery query, RunicRowRange range, CancellationToken cancellationToken)
    {
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        Exception? failure;
        lock (_gate) (failure, _failure) = (_failure, null);
        if (failure is not null) throw failure;
        var page = await _source.LoadAsync(query, range, cancellationToken).ConfigureAwait(false);
        lock (_gate) _loads.Add(new(query, range, page.Rows.Count));
        return page;
    }

    /// <inheritdoc />
    public async ValueTask<int> IndexOfAsync(RunicQuery query, TKey key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _searches);
        using var _ = await EnterAsync(cancellationToken).ConfigureAwait(false);
        return await _source.IndexOfAsync(query, key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Validate(RunicQuery query) => _source.Validate(query);

    private async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        int maximum;
        while ((maximum = Volatile.Read(ref _maximumActive)) < active && Interlocked.CompareExchange(ref _maximumActive, active, maximum) != maximum) { }
        var exit = new Exit(this);
        try
        {
            if (Latency > TimeSpan.Zero) await Task.Delay(Latency, _time, cancellationToken).ConfigureAwait(false);
            return exit;
        }
        catch
        {
            exit.Dispose();
            throw;
        }
    }

    private sealed class Exit(RunicTestRowSource<TRow, TKey> owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._active);
        }
    }
}

/// <summary>One load of a <see cref="RunicTestRowSource{TRow, TKey}"/>.</summary>
/// <param name="Query">The query it ran.</param>
/// <param name="Range">The range it read.</param>
/// <param name="Returned">The rows it returned.</param>
public readonly record struct RunicRowLoad(RunicQuery Query, RunicRowRange Range, int Returned);
