using System.Linq.Expressions;

namespace Runic.Application.Views;

/// <summary>
/// A <see cref="IRunicRowSource{TRow, TKey}"/> over an <see cref="IQueryable{T}"/>: an in-memory list
/// (<c>rows.AsQueryable()</c>), or a database table through a LINQ provider such as Entity Framework Core.
/// </summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TKey">The row key.</typeparam>
/// <remarks>
/// <para>
/// Each call takes a new query from <c>rows</c>, so a factory can open a short-lived database context per call. Queries are
/// mapped by <see cref="RunicQueryFields{TRow}"/> and ordered by the row key last. The source runs the LINQ query
/// synchronously on the caller's thread, which the collection never makes a model turn. A source with asynchronous
/// LINQ (<c>ToListAsync</c>) implements <see cref="IRunicRowSource{TRow, TKey}"/> itself with the same fields.
/// </para>
/// <para>
/// <see cref="IndexOfAsync"/> reads the keys of the filtered, ordered rows until it finds the key. A database source that
/// needs a faster search implements it with <c>ROW_NUMBER()</c>.
/// </para>
/// </remarks>
/// <param name="rows">Returns the rows to query.</param>
/// <param name="fields">The fields the page may sort and filter by.</param>
/// <param name="key">The row key, which the source sorts by last.</param>
public sealed class RunicQueryableRowSource<TRow, TKey>(Func<IQueryable<TRow>> rows, RunicQueryFields<TRow> fields,
    Expression<Func<TRow, TKey>> key) : IRunicRowSource<TRow, TKey> where TKey : notnull
{
    private readonly Func<IQueryable<TRow>> _rows = rows ?? throw new ArgumentNullException(nameof(rows));
    private readonly RunicQueryFields<TRow> _fields = fields ?? throw new ArgumentNullException(nameof(fields));
    private readonly Expression<Func<TRow, TKey>> _key = key ?? throw new ArgumentNullException(nameof(key));

    /// <inheritdoc />
    public ValueTask<int> CountAsync(RunicQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_fields.Filter(_rows(), query).Count());
    }

    /// <inheritdoc />
    public ValueTask<RunicRowPage<TRow>> LoadAsync(RunicQuery query, RunicRowRange range, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = _fields.Apply(_rows(), query, _key).Skip(range.Start).Take(range.Count).ToList();
        return new(new RunicRowPage<TRow>(page));
    }

    /// <inheritdoc />
    public ValueTask<int> IndexOfAsync(RunicQuery query, TKey key, CancellationToken cancellationToken)
    {
        var comparer = EqualityComparer<TKey>.Default;
        var index = 0;
        foreach (var candidate in _fields.Apply(_rows(), query, _key).Select(_key))
        {
            if (comparer.Equals(candidate, key)) return new(index);
            if ((++index & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
        return new(-1);
    }

    /// <inheritdoc />
    public void Validate(RunicQuery query) => _fields.Validate(query);
}
