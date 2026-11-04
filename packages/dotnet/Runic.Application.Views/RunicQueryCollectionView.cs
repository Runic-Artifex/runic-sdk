using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Runic.Application.Views;

/// <summary>
/// One View's list of a <see cref="RunicQueryCollection{TRow, TKey}"/>, for a WPF <c>ItemsControl</c> with UI
/// virtualization: it has <see cref="RunicQueryCollection{TRow, TKey}.Total"/> items and loads the pages that its panel
/// reads. Create it with <see cref="RunicQueryCollection{TRow, TKey}.CreateView"/>.
/// </summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <remarks>
/// <para>
/// An item whose page has not arrived is <see langword="null"/>; the list then replaces it with the row, one
/// <see cref="NotifyCollectionChangedAction.Replace"/> per row. A new <see cref="RunicQueryCollection{TRow, TKey}.Version"/>
/// or total resets the list. It keeps at most 32 pages and raises its notifications in the
/// <see cref="SynchronizationContext"/> it was created with, normally the UI thread's.
/// </para>
/// <para>
/// <see cref="IList.IndexOf"/> and <see cref="IList.Contains"/> search only the loaded rows. The list is read-only.
/// </para>
/// </remarks>
public sealed class RunicQueryCollectionView<TRow> : IList, IReadOnlyList<TRow?>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    private const int MaximumPages = 32;
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    private readonly INotifyPropertyChanged _owner;
    private readonly Func<int?> _total;
    private readonly Func<long> _version;
    private readonly Func<int, int, CancellationToken, Task<BridgeQueryRows>> _load;
    private readonly SynchronizationContext? _context;
    private readonly int _pageSize;
    private readonly Dictionary<int, IReadOnlyList<object?>> _pages = [];
    private readonly HashSet<int> _loading = [];
    private readonly CancellationTokenSource _lifetime = new();
    private int _count;
    private long _loadedVersion;
    private int _lastPage;
    private bool _disposed;

    internal RunicQueryCollectionView(INotifyPropertyChanged owner, Func<int?> total, Func<long> version,
        Func<int, int, CancellationToken, Task<BridgeQueryRows>> load, SynchronizationContext? context, int pageSize)
    {
        _owner = owner;
        _total = total;
        _version = version;
        _load = load;
        _context = context;
        _pageSize = pageSize;
        _count = total() ?? 0;
        _loadedVersion = version();
        owner.PropertyChanged += OnOwnerChanged;
        // An unknown total is counted by the first load.
        if (total() is null) StartLoad(0);
    }

    /// <inheritdoc />
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The number of rows: the collection's total, or 0 until it was counted.</summary>
    public int Count => _count;

    /// <summary>The row at <paramref name="index"/>, or <see langword="null"/> while its page loads.</summary>
    /// <param name="index">The row's position.</param>
    public TRow? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            var page = index / _pageSize;
            _lastPage = page;
            if (_pages.TryGetValue(page, out var rows))
                return index - page * _pageSize < rows.Count ? (TRow?)rows[index - page * _pageSize] : default;
            StartLoad(page);
            return default;
        }
    }

    /// <summary>Stops loading and following the collection.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.PropertyChanged -= OnOwnerChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <inheritdoc />
    public IEnumerator<TRow?> GetEnumerator()
    {
        for (var index = 0; index < _count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException("A query collection view is read-only.");
    }

    bool IList.IsFixedSize => false;
    bool IList.IsReadOnly => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    int IList.Add(object? value) => throw new NotSupportedException("A query collection view is read-only.");
    void IList.Clear() => throw new NotSupportedException("A query collection view is read-only.");
    void IList.Insert(int index, object? value) => throw new NotSupportedException("A query collection view is read-only.");
    void IList.Remove(object? value) => throw new NotSupportedException("A query collection view is read-only.");
    void IList.RemoveAt(int index) => throw new NotSupportedException("A query collection view is read-only.");
    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.IndexOf(object? value)
    {
        if (value is null) return -1;
        foreach (var (page, rows) in _pages)
            for (var index = 0; index < rows.Count; index++)
                if (Equals(rows[index], value)) return page * _pageSize + index;
        return -1;
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        for (var item = 0; item < _count; item++) array.SetValue(this[item], index + item);
    }

    private void StartLoad(int page)
    {
        if (_disposed || !_loading.Add(page)) return;
        var version = _loadedVersion;
        var token = _lifetime.Token;
        _ = LoadAsync();

        async Task LoadAsync()
        {
            BridgeQueryRows loaded;
            try { loaded = await _load(page * _pageSize, _pageSize, token).ConfigureAwait(false); }
            catch (Exception) when (token.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // The page is read again when the panel asks for it again.
                Run(() => _loading.Remove(page));
                return;
            }
            Run(() => Loaded(page, version, loaded));
        }
    }

    private void Loaded(int page, long version, BridgeQueryRows loaded)
    {
        _loading.Remove(page);
        if (_disposed || version != _loadedVersion) return;
        if (loaded.Total is { } total && total != _count)
        {
            Reset(total);
            return;
        }
        _pages[page] = loaded.Rows;
        // Keeps the pages nearest the one the panel read last.
        while (_pages.Count > MaximumPages)
            _pages.Remove(_pages.Keys.MaxBy(candidate => Math.Abs(candidate - _lastPage)));
        for (var index = 0; index < loaded.Rows.Count && page * _pageSize + index < _count; index++)
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace,
                loaded.Rows[index], null, page * _pageSize + index));
        PropertyChanged?.Invoke(this, IndexerChanged);
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RunicQueryCollection<object, int>.Total) or nameof(RunicQueryCollection<object, int>.Version))) return;
        var total = _total() ?? 0;
        var version = _version();
        Run(() =>
        {
            if (_disposed || total == _count && version == _loadedVersion) return;
            _loadedVersion = version;
            Reset(total);
            // A new query's total is unknown until counted: the first page's load brings it.
            if (_total() is null) StartLoad(0);
        });
    }

    private void Reset(int total)
    {
        _pages.Clear();
        _loading.Clear();
        var counted = total != _count;
        _count = total;
        if (counted) PropertyChanged?.Invoke(this, CountChanged);
        PropertyChanged?.Invoke(this, IndexerChanged);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void Run(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context) action();
        else _context.Post(static state => ((Action)state!)(), action);
    }
}
