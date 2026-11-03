using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Runic.Navigation;

namespace Runic.Application.Views;

/// <summary>
/// A large list that the page sorts, filters and scrolls without holding it: rows stay in an
/// <see cref="IRunicRowSource{TRow, TKey}"/>, and each presentation loads only the range it shows.
/// </summary>
/// <typeparam name="TRow">The row type, a Bridge DTO.</typeparam>
/// <typeparam name="TKey">The row key: a <see cref="string"/>, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
/// <remarks>
/// <para>
/// Expose it as a get-only ViewModel property. The page's state holds only the collection's <see cref="Query"/>,
/// <see cref="Total"/> and <see cref="Version"/>; each page asks for the rows it shows with the generated client's
/// <c>queries.{name}.setRange(start, count)</c>, and .NET answers that page alone (<c>query-collections/1</c>). Two pages
/// that scroll the same list therefore don't move each other's rows. The query is ViewModel state that every presentation
/// shares, like any other member: a sort or filter one page sets shows on every page, and the ViewModel can read it.
/// A ViewModel per presentation gives each its own query.
/// </para>
/// <para>
/// Loads are coalesced, bounded and cancellable: the collection reads the source in pages of
/// <see cref="RunicQueryCollectionOptions.PageSize"/> rows, at most
/// <see cref="RunicQueryCollectionOptions.MaximumConcurrentLoads"/> at a time, shares a page that several requests need,
/// keeps at most <see cref="RunicQueryCollectionOptions.MaximumCachedPages"/> pages, and cancels a load once no request
/// waits for it. Memory therefore does not grow with the number of rows.
/// </para>
/// <para>
/// <see cref="SetQuery"/>, <see cref="Refresh"/> and <see cref="Invalidate(TKey)"/> raise <see cref="Version"/>, which tells
/// every page to load its range again. Call <see cref="Invalidate(TKey)"/> when rows change in place and
/// <see cref="Refresh"/> when rows are added or removed, since that moves the rows after them. Property changes are raised
/// in the <see cref="RunicQueryCollectionOptions.ModelContext"/>, or else in the <see cref="SynchronizationContext"/> that
/// was current when the collection was created.
/// </para>
/// <para>
/// A WPF View binds a <see cref="CreateView"/> instead, which loads the rows its virtualizing panel asks for.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is the ViewModel's collection of rows; the rows stay in the source, so it does not implement ICollection.")]
public sealed class RunicQueryCollection<TRow, TKey> : INotifyPropertyChanged, IDisposable, IBridgeQueryCollection
    where TKey : notnull
{
    private static readonly PropertyChangedEventArgs QueryChanged = new(nameof(Query));
    private static readonly PropertyChangedEventArgs TotalChanged = new(nameof(Total));
    private static readonly PropertyChangedEventArgs VersionChanged = new(nameof(Version));

    private readonly object _gate = new();
    private readonly IRunicRowSource<TRow, TKey> _source;
    private readonly Func<TRow, TKey> _key;
    private readonly int _pageSize;
    private readonly int _maximumPages;
    private readonly SemaphoreSlim _loads;
    private readonly IRunicModelContext? _context;
    private readonly SynchronizationContext? _synchronization;
    private readonly CancellationTokenSource _lifetime = new();
    // The pages by index, and their use order: most recent first.
    private readonly Dictionary<int, Page> _pages = [];
    private readonly LinkedList<Page> _recent = new();

    // The collection's current state, which loads use. Generation changes with the query and Refresh.
    private RunicQuery _query;
    private long _version;
    private int _generation;
    private int? _total;
    private Task<int>? _count;
    private int _countGeneration = -1;

    // The state the ViewModel and the Bridge see, committed in the model context.
    private RunicQuery _publishedQuery;
    private int? _publishedTotal;
    private long _publishedVersion;
    private bool _commitPosted;

    private long _pageLoads;
    private long _rowsLoaded;
    private long _counts;
    private bool _disposed;

    /// <summary>Creates a query collection over <paramref name="source"/>.</summary>
    /// <param name="source">The rows.</param>
    /// <param name="key">The key of a row. Keys are unique and do not change while a row exists.</param>
    /// <param name="options">The cache, concurrency, initial query and model context, or <see langword="null"/> for the defaults.</param>
    /// <exception cref="NotSupportedException"><typeparamref name="TKey"/> is not a string, Guid or Int32.</exception>
    public RunicQueryCollection(IRunicRowSource<TRow, TKey> source, Func<TRow, TKey> key, RunicQueryCollectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);
        if (typeof(TKey) != typeof(string) && typeof(TKey) != typeof(Guid) && typeof(TKey) != typeof(int))
            throw new NotSupportedException($"A query collection key is a string, Guid or Int32, not {typeof(TKey).Name}.");
        options ??= new();
        options.Check();
        _source = source;
        _key = key;
        _pageSize = options.PageSize;
        _maximumPages = options.MaximumCachedPages;
        _loads = new(options.MaximumConcurrentLoads, options.MaximumConcurrentLoads);
        _context = options.ModelContext;
        _synchronization = _context is null ? SynchronizationContext.Current : null;
        _query = _publishedQuery = options.Query ?? RunicQuery.Empty;
        if (!ReferenceEquals(_query, RunicQuery.Empty)) source.Validate(_query);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The sort and filter. The page changes it with <c>setQuery</c>; .NET with <see cref="SetQuery"/>.</summary>
    public RunicQuery Query => _publishedQuery;

    /// <summary>The number of rows that match <see cref="Query"/>, or <see langword="null"/> until it was counted.</summary>
    /// <remarks>The collection counts when a page first loads rows, and again after each query change and refresh.</remarks>
    public int? Total => _publishedTotal;

    /// <summary>
    /// Increases whenever loaded rows may be out of date: after a query change, <see cref="Refresh"/> or
    /// <see cref="Invalidate(TKey)"/>. Pages load their range again when it changes.
    /// </summary>
    public long Version => _publishedVersion;

    /// <summary>What the collection has loaded and keeps, for tests and diagnostics.</summary>
    public RunicQueryCollectionStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                var rows = 0;
                foreach (var page in _pages.Values)
                    if (page.Task.IsCompletedSuccessfully) rows += page.Task.Result.Count;
                return new(_pages.Count, rows, Interlocked.Read(ref _pageLoads), Interlocked.Read(ref _rowsLoaded), Interlocked.Read(ref _counts));
            }
        }
    }

    /// <summary>Changes the sort and filter. Every page loads its range again.</summary>
    /// <param name="query">The new query.</param>
    /// <exception cref="ArgumentException">The source rejects the query (<see cref="IRunicRowSource{TRow, TKey}.Validate"/>).</exception>
    public void SetQuery(RunicQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _source.Validate(query);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_query.Equals(query)) return;
            _query = query;
            Restart();
        }
        Commit();
    }

    /// <summary>Discards every loaded row and the count, as after rows were added or removed. Every page loads its range again.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Restart();
        }
        Commit();
    }

    /// <summary>Discards the loaded page that holds the row with <paramref name="key"/>, after the row changed in place.</summary>
    /// <param name="key">The row's key.</param>
    public void Invalidate(TKey key) => Invalidate([key]);

    /// <summary>Discards the loaded pages that hold rows with these keys, after the rows changed in place.</summary>
    /// <param name="keys">The rows' keys.</param>
    /// <remarks>The rows stay where they are: when a change moves a row in the query's order, call <see cref="Refresh"/>.</remarks>
    public void Invalidate(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var changed = keys as IReadOnlyCollection<TKey> ?? [.. keys];
        if (changed.Count == 0) return;
        var set = changed.Count > 4 ? new HashSet<TKey>(changed) : null;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var page in _pages.Values.ToArray())
            {
                // A load in flight may have read the row before it changed: later requests start another.
                if (!page.Task.IsCompleted) { Remove(page, cancelUnwaited: false); continue; }
                if (!page.Task.IsCompletedSuccessfully) continue;
                foreach (var row in page.Task.Result)
                {
                    var key = _key(row);
                    if (set?.Contains(key) ?? changed.Contains(key)) { Remove(page, cancelUnwaited: false); break; }
                }
            }
            _version++;
        }
        Commit();
    }

    /// <summary>Loads the rows of <paramref name="range"/> in the query's order, from the cache where it can.</summary>
    /// <param name="range">The rows; at most <see cref="RunicQueryCollectionOptions.MaximumRangeCount"/>.</param>
    /// <param name="cancellationToken">Cancels this request. A page load stops when no request waits for it any more.</param>
    /// <returns>The rows, fewer at the end of the list.</returns>
    public async ValueTask<IReadOnlyList<TRow>> LoadAsync(RunicRowRange range, CancellationToken cancellationToken = default) =>
        [.. (await LoadCoreAsync(range.Start, range.Count, cancellationToken).ConfigureAwait(false)).Rows.Select(row => (TRow)row!)];

    /// <summary>Returns the position of the row with <paramref name="key"/> in the current query's order, or -1.</summary>
    /// <param name="key">The row's key.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    public async ValueTask<int> IndexOfAsync(TKey key, CancellationToken cancellationToken = default) =>
        (await IndexOfCoreAsync(key, cancellationToken).ConfigureAwait(false)).Index;

    /// <summary>
    /// Creates a list that a WPF <c>ItemsControl</c> with a virtualizing panel binds as its <c>ItemsSource</c>: it has
    /// <see cref="Total"/> items, loads the pages its panel reads, and shows <see langword="null"/> for a row until
    /// its page arrives. Each View creates its own, as each page has its own range.
    /// </summary>
    /// <param name="context">
    /// Where the list raises its change notifications: the UI thread's context. Defaults to
    /// <see cref="SynchronizationContext.Current"/>.
    /// </param>
    /// <returns>The list. Dispose it with its View.</returns>
    public RunicQueryCollectionView<TRow> CreateView(SynchronizationContext? context = null) =>
        new(this, () => Total, () => Version, LoadCoreAsync, context ?? SynchronizationContext.Current, _pageSize);

    /// <summary>Stops loading and cancels the loads in flight.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var page in _pages.Values.ToArray())
            {
                Remove(page, cancelUnwaited: false);
                page.TryCancel();
            }
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // A new query or refresh: a new generation of pages and count. The gate is held.
    private void Restart()
    {
        _generation++;
        _version++;
        foreach (var page in _pages.Values.ToArray())
        {
            Remove(page, cancelUnwaited: false);
            // Requests that wait for a page of the old query read the new one instead.
            page.TryCancel();
        }
        // The count of a list that pages use is taken again at once, so the total follows the query.
        var counting = _count is not null;
        _count = null;
        _countGeneration = -1;
        if (counting) EnsureCount();
    }

    internal async Task<BridgeQueryRows> LoadCoreAsync(int start, int count, CancellationToken cancellationToken)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start), "A range starts at 0 or later.");
        if (count is < 0 or > RunicQueryCollectionOptions.MaximumRangeCount)
            throw new ArgumentOutOfRangeException(nameof(count), $"A range has 0 to {RunicQueryCollectionOptions.MaximumRangeCount} rows.");
        // A query change while the pages load cancels them; the request then reads the new generation.
        for (var attempt = 0; ; attempt++)
        {
            long version;
            int generation;
            Task<int> total;
            var pages = new List<Page>();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                version = _version;
                generation = _generation;
                total = EnsureCount();
                if (count > 0)
                    for (var index = start / _pageSize; index <= (start + count - 1) / _pageSize; index++)
                    {
                        var page = Acquire(index);
                        page.Waiters++;
                        pages.Add(page);
                    }
            }
            try
            {
                var rows = new List<object?>(count);
                foreach (var page in pages)
                {
                    var loaded = await page.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var first = Math.Max(start, page.Index * _pageSize) - page.Index * _pageSize;
                    var last = Math.Min(start + count, page.Index * _pageSize + loaded.Count) - page.Index * _pageSize;
                    for (var index = first; index < last; index++) rows.Add(loaded[index]);
                    // A short page is the end of the list.
                    if (loaded.Count < _pageSize) break;
                }
                var counted = await total.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new(version, counted, start, rows);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 3 && Superseded(generation))
            {
            }
            finally
            {
                lock (_gate)
                    foreach (var page in pages) Release(page);
            }
        }
    }

    internal async Task<(long Version, int Index)> IndexOfCoreAsync(TKey key, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            RunicQuery query;
            long version;
            int generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                query = _query;
                version = _version;
                generation = _generation;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var index = await Task.Run(async () => await _source.IndexOfAsync(query, key, linked.Token).ConfigureAwait(false), linked.Token)
                .ConfigureAwait(false);
            if (!Superseded(generation) || attempt >= 3) return (version, index);
        }
    }

    private bool Superseded(int generation)
    {
        lock (_gate) return !_disposed && _generation != generation;
    }

    // The count of the current generation, started on first use. The gate is held.
    private Task<int> EnsureCount()
    {
        if (_count is not null && _countGeneration == _generation) return _count;
        var generation = _countGeneration = _generation;
        var query = _query;
        var token = _lifetime.Token;
        var count = _count = Task.Run(async () =>
        {
            Interlocked.Increment(ref _counts);
            return await _source.CountAsync(query, token).ConfigureAwait(false);
        }, token);
        _ = count.ContinueWith(completed =>
        {
            lock (_gate)
            {
                if (_generation != generation || _disposed) return;
                // A failed count is taken again by the next request.
                if (!completed.IsCompletedSuccessfully) { if (ReferenceEquals(_count, completed)) _count = null; return; }
                if (_total == completed.Result) return;
                _total = completed.Result;
            }
            Commit();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return count;
    }

    // The page at index, loaded or loading, most recently used first. The gate is held.
    private Page Acquire(int index)
    {
        if (_pages.TryGetValue(index, out var page))
        {
            _recent.Remove(page.Node);
            _recent.AddFirst(page.Node);
            return page;
        }
        var generation = _generation;
        var query = _query;
        // Not linked to the lifetime: Dispose cancels every page, and a linked source would register with it.
        var cancel = new CancellationTokenSource();
        var range = new RunicRowRange(index * _pageSize, _pageSize);
        var task = Task.Run(async () =>
        {
            await _loads.WaitAsync(cancel.Token).ConfigureAwait(false);
            try
            {
                Interlocked.Increment(ref _pageLoads);
                var loaded = await _source.LoadAsync(query, range, cancel.Token).ConfigureAwait(false);
                var rows = loaded.Rows ?? throw new InvalidOperationException("A row source returned a page without rows.");
                if (rows.Count > range.Count) throw new InvalidOperationException($"A row source returned {rows.Count} rows for a range of {range.Count}.");
                Interlocked.Add(ref _rowsLoaded, rows.Count);
                if (loaded.Total is { } total) ReportTotal(generation, total);
                return rows;
            }
            finally { _loads.Release(); }
        }, cancel.Token);
        page = new Page(index, task, cancel);
        page.Node = _recent.AddFirst(page);
        _pages.Add(index, page);
        _ = task.ContinueWith(completed =>
        {
            lock (_gate)
            {
                // A failed page is not kept: the next request loads it again.
                if (!completed.IsCompletedSuccessfully && page.Cached) Remove(page, cancelUnwaited: false);
                page.Complete();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Trim();
        return page;
    }

    // Evicts the least recently used pages beyond the cache's size. The gate is held.
    private void Trim()
    {
        var node = _recent.Last;
        while (_pages.Count > _maximumPages && node is not null)
        {
            var previous = node.Previous;
            // A page that requests still wait for leaves the cache, but its load continues for them.
            Remove(node.Value, cancelUnwaited: true);
            node = previous;
        }
    }

    private void Release(Page page)
    {
        page.Waiters--;
        // Nobody waits for the page any more, as after a page scrolled past it: stop its load, and forget it.
        if (page.Waiters == 0 && !page.Task.IsCompleted) Remove(page, cancelUnwaited: true);
    }

    private void Remove(Page page, bool cancelUnwaited)
    {
        if (page.Cached)
        {
            page.Cached = false;
            _pages.Remove(page.Index);
            _recent.Remove(page.Node);
        }
        if (cancelUnwaited && page.Waiters == 0) page.TryCancel();
    }

    private void ReportTotal(int generation, int total)
    {
        lock (_gate)
        {
            if (_generation != generation || _disposed || _total == total) return;
            _total = total;
        }
        Commit();
    }

    // Publishes the current state as the collection's properties: at once in the model context, otherwise in a turn.
    private void Commit()
    {
        if (_context is { IsExecuting: true } || _context is null && (_synchronization is null || SynchronizationContext.Current == _synchronization))
        {
            Publish();
            return;
        }
        lock (_gate)
        {
            if (_commitPosted) return;
            _commitPosted = true;
        }
        if (_context is not null)
        {
            if (!_context.TryPost(Publish)) lock (_gate) _commitPosted = false;
        }
        else _synchronization!.Post(static state => ((RunicQueryCollection<TRow, TKey>)state!).Publish(), this);
    }

    private void Publish()
    {
        bool query, total, version;
        lock (_gate)
        {
            _commitPosted = false;
            query = !ReferenceEquals(_publishedQuery, _query);
            total = _publishedTotal != _total;
            version = _publishedVersion != _version;
            _publishedQuery = _query;
            _publishedTotal = _total;
            _publishedVersion = _version;
        }
        if (query) PropertyChanged?.Invoke(this, QueryChanged);
        if (total) PropertyChanged?.Invoke(this, TotalChanged);
        if (version) PropertyChanged?.Invoke(this, VersionChanged);
    }

    void IBridgeQueryCollection.WriteState(Utf8JsonWriter writer)
    {
        RunicQuery query;
        int? total;
        long version;
        lock (_gate)
        {
            query = _publishedQuery;
            total = _publishedTotal;
            version = _publishedVersion;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("query");
        BridgeQueryWire.Write(writer, query);
        if (total is { } count) writer.WriteNumber("total", count);
        else writer.WriteNull("total");
        writer.WriteNumber("version", version);
        writer.WriteEndObject();
    }

    Task<BridgeQueryRows> IBridgeQueryCollection.LoadRowsAsync(int start, int count, CancellationToken cancellationToken) =>
        LoadCoreAsync(start, count, cancellationToken);

    string IBridgeQueryCollection.KeyOf(object? row)
    {
        if (row is null) throw new InvalidOperationException("A query collection row is null.");
        var key = _key((TRow)row);
        var text = key switch
        {
            string value => value,
            Guid value => value.ToString("D"),
            int value => value.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? throw new InvalidOperationException("A query collection row has a null or empty key.") : text;
    }

    async Task<(long Version, int Index)> IBridgeQueryCollection.IndexOfAsync(string key, CancellationToken cancellationToken)
    {
        object? parsed = typeof(TKey) == typeof(string) ? key
            : typeof(TKey) == typeof(Guid) ? Guid.TryParseExact(key, "D", out var guid) ? guid : null
            : int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;
        if (parsed is null)
            lock (_gate) return (_version, -1);
        return await IndexOfCoreAsync((TKey)parsed, cancellationToken).ConfigureAwait(false);
    }

    void IBridgeQueryCollection.SetQuery(RunicQuery query) => SetQuery(query);

    private sealed class Page(int index, Task<IReadOnlyList<TRow>> task, CancellationTokenSource cancel)
    {
        public int Index { get; } = index;
        public Task<IReadOnlyList<TRow>> Task { get; } = task;
        private CancellationTokenSource? _cancel = cancel;
        public LinkedListNode<Page> Node { get; set; } = null!;
        // The requests that wait for the page. The gate guards it.
        public int Waiters { get; set; }
        public bool Cached { get; set; } = true;

        // Cancels the load unless it completed. The gate is held, as for Complete.
        public void TryCancel() => _cancel?.Cancel();

        // Releases the cancellation source once the load completed. The gate is held.
        public void Complete()
        {
            _cancel?.Dispose();
            _cancel = null;
        }
    }
}

/// <summary>The cache, concurrency, initial query and model context of a <see cref="RunicQueryCollection{TRow, TKey}"/>.</summary>
public sealed class RunicQueryCollectionOptions
{
    /// <summary>The most rows one request reads: a page's <c>setRange</c>, or <see cref="RunicQueryCollection{TRow, TKey}.LoadAsync"/>.</summary>
    public const int MaximumRangeCount = 1_000;

    /// <summary>The rows the collection reads from the source at once, and caches together. 100 by default; 1 to 1,000.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>The most pages the collection keeps; the least recently used leave first. 64 by default.</summary>
    public int MaximumCachedPages { get; init; } = 64;

    /// <summary>The most source loads that run at once. 4 by default.</summary>
    public int MaximumConcurrentLoads { get; init; } = 4;

    /// <summary>The query the collection starts with. <see cref="RunicQuery.Empty"/> by default.</summary>
    public RunicQuery? Query { get; init; }

    /// <summary>
    /// The ViewModel's model context, in which the collection raises its property changes. Without one it raises them in
    /// the <see cref="SynchronizationContext"/> that was current when it was created, or else where they happen.
    /// </summary>
    public IRunicModelContext? ModelContext { get; init; }

    internal void Check()
    {
        if (PageSize is < 1 or > MaximumRangeCount) throw new ArgumentOutOfRangeException(nameof(PageSize), $"A page has 1 to {MaximumRangeCount} rows.");
        if (MaximumCachedPages < 1) throw new ArgumentOutOfRangeException(nameof(MaximumCachedPages), "The cache keeps at least one page.");
        if (MaximumConcurrentLoads < 1) throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentLoads), "At least one load runs at a time.");
    }
}

/// <summary>What a <see cref="RunicQueryCollection{TRow, TKey}"/> has loaded and keeps.</summary>
/// <param name="CachedPages">The pages in the cache, loaded or loading.</param>
/// <param name="CachedRows">The rows of the loaded pages in the cache.</param>
/// <param name="PageLoads">The source loads started so far.</param>
/// <param name="RowsLoaded">The rows the source returned so far.</param>
/// <param name="Counts">The source counts started so far.</param>
public readonly record struct RunicQueryCollectionStatistics(int CachedPages, int CachedRows, long PageLoads, long RowsLoaded, long Counts);

// The Bridge's view of a query collection (query-collections/1).
internal interface IBridgeQueryCollection
{
    void WriteState(Utf8JsonWriter writer);
    Task<BridgeQueryRows> LoadRowsAsync(int start, int count, CancellationToken cancellationToken);
    string KeyOf(object? row);
    Task<(long Version, int Index)> IndexOfAsync(string key, CancellationToken cancellationToken);
    void SetQuery(RunicQuery query);
}

// The rows of a query-rows reply: the version they were loaded at, the total, and the rows from start.
internal readonly record struct BridgeQueryRows(long Version, int? Total, int Start, IReadOnlyList<object?> Rows);
