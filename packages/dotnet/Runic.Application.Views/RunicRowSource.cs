namespace Runic.Application.Views;

/// <summary>
/// The rows of a <see cref="RunicQueryCollection{TRow, TKey}"/>: an application's database table, HTTP API or in-memory
/// list, read one range at a time.
/// </summary>
/// <typeparam name="TRow">The row type, a Bridge DTO.</typeparam>
/// <typeparam name="TKey">The row key: a <see cref="string"/>, <see cref="Guid"/> or <see cref="int"/>.</typeparam>
/// <remarks>
/// <para>
/// A source orders rows by the query's sort keys and then by key, so that a range is stable. The collection calls it
/// from any thread, possibly concurrently, and cancels a call whose result nobody needs any more. It never calls it in a
/// model turn. <see cref="RunicQueryableRowSource{TRow, TKey}"/> implements it over an <see cref="IQueryable{T}"/> with
/// <see cref="RunicQueryFields{TRow}"/>.
/// </para>
/// </remarks>
public interface IRunicRowSource<TRow, in TKey> where TKey : notnull
{
    /// <summary>Counts the rows that match <paramref name="query"/>'s filters.</summary>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    ValueTask<int> CountAsync(RunicQuery query, CancellationToken cancellationToken);

    /// <summary>Loads the rows of <paramref name="range"/> in <paramref name="query"/>'s order.</summary>
    /// <param name="query">The query.</param>
    /// <param name="range">The rows to load. Fewer rows come back at the end of the list.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The rows, and optionally the number of rows that match the query, such as from <c>COUNT(*) OVER ()</c>.</returns>
    ValueTask<RunicRowPage<TRow>> LoadAsync(RunicQuery query, RunicRowRange range, CancellationToken cancellationToken);

    /// <summary>Returns the position of the row with <paramref name="key"/> in <paramref name="query"/>'s order, or -1.</summary>
    /// <param name="query">The query.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    ValueTask<int> IndexOfAsync(RunicQuery query, TKey key, CancellationToken cancellationToken);

    /// <summary>Checks that the source can run <paramref name="query"/> before the collection takes it.</summary>
    /// <remarks>
    /// The query comes from a page and is untrusted: its field names, custom filter names and values are whatever the
    /// page sent. Reject every field, operator and value the source does not support by name, rather than passing them
    /// to a database. <see cref="RunicQueryFields{TRow}.Validate"/> does so for registered fields.
    /// </remarks>
    /// <param name="query">The query.</param>
    /// <exception cref="ArgumentException">
    /// The query names a field or filter the source does not have, or a value it cannot use. The page receives
    /// <c>invalid-input</c>, and the collection keeps its query.
    /// </exception>
    void Validate(RunicQuery query);
}

/// <summary>A range of rows: <paramref name="Count"/> rows from position <paramref name="Start"/>.</summary>
/// <param name="Start">The position of the first row, from 0.</param>
/// <param name="Count">The number of rows.</param>
public readonly record struct RunicRowRange(int Start, int Count)
{
    /// <summary>The position after the last row.</summary>
    public int End => Start + Count;
}

/// <summary>The rows a <see cref="IRunicRowSource{TRow, TKey}"/> loaded for a range.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <param name="Rows">The rows, at most the range's count.</param>
/// <param name="Total">The number of rows that match the query, when the load learned it, or <see langword="null"/>.</param>
public sealed record RunicRowPage<TRow>(IReadOnlyList<TRow> Rows, int? Total = null);
