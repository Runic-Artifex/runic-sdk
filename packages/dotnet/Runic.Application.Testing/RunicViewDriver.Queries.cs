using System.Linq.Expressions;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

public sealed partial class RunicViewDriver<TModel>
{
    /// <summary>
    /// Reads rows of a <see cref="RunicQueryCollection{TRow, TKey}"/> as a page's <c>queries.{name}.setRange(start, count)</c>
    /// does (<c>query-rows</c>), on the test connection or on <paramref name="connectionKey"/>, as another page.
    /// </summary>
    /// <param name="collection">The query collection property, such as <c>vm => vm.Rows</c>.</param>
    /// <param name="start">The first row.</param>
    /// <param name="count">The number of rows, at most <see cref="RunicQueryCollectionOptions.MaximumRangeCount"/>.</param>
    /// <param name="connectionKey">The connection, or <see langword="null"/> for the test connection.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The rows as the page receives them.</returns>
    /// <exception cref="InvalidOperationException">The route answered with a failure.</exception>
    public async Task<RunicQueryRows> QueryRowsAsync<TRow, TKey>(Expression<Func<TModel, RunicQueryCollection<TRow, TKey>>> collection,
        int start, int count, string? connectionKey = null, CancellationToken cancellationToken = default) where TKey : notnull
    {
        var member = QueryMember(collection);
        var reply = await SendAsync(writer =>
        {
            writer.WriteString("kind", "query-rows");
            writer.WriteString("member", member);
            writer.WriteNumber("start", start);
            writer.WriteNumber("count", count);
        }, connectionKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(reply);
        var root = Succeeded(document, "query-rows", member, reply);
        return new(root.GetProperty("version").GetInt64(),
            root.GetProperty("total").ValueKind == JsonValueKind.Null ? null : root.GetProperty("total").GetInt32(),
            root.GetProperty("start").GetInt32(),
            [.. root.GetProperty("keys").EnumerateArray().Select(key => key.GetString()!)],
            [.. root.GetProperty("rows").EnumerateArray().Select(row => row.Clone())]);
    }

    /// <summary>Changes the query of a query collection as a page's <c>setQuery</c> does (<c>query-set</c>).</summary>
    /// <param name="collection">The query collection property, such as <c>vm => vm.Rows</c>.</param>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The reply, with the state after it.</returns>
    public async Task<RunicCallReply<TModel>> SetQueryAsync<TRow, TKey>(Expression<Func<TModel, RunicQueryCollection<TRow, TKey>>> collection,
        RunicQuery query, CancellationToken cancellationToken = default) where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        var member = QueryMember(collection);
        var reply = await SendAsync(writer =>
        {
            writer.WriteString("kind", "query-set");
            writer.WriteString("member", member);
            writer.WritePropertyName("query");
            writer.WriteRawValue(query.ToString());
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReplyAsync("query-set", member, reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the position of a row in a query collection's current query, as a page's <c>scrollToKey</c> does
    /// (<c>query-index</c>).
    /// </summary>
    /// <param name="collection">The query collection property, such as <c>vm => vm.Rows</c>.</param>
    /// <param name="key">The row's wire key: the string, a lowercase GUID or an <c>Int32</c> in decimal.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The row's position, or -1 when no row has the key.</returns>
    public async Task<int> IndexOfAsync<TRow, TKey>(Expression<Func<TModel, RunicQueryCollection<TRow, TKey>>> collection, string key,
        CancellationToken cancellationToken = default) where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        var member = QueryMember(collection);
        var reply = await SendAsync(writer =>
        {
            writer.WriteString("kind", "query-index");
            writer.WriteString("member", member);
            writer.WriteString("key", key);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(reply);
        return Succeeded(document, "query-index", member, reply).GetProperty("index").GetInt32();
    }

    private static string QueryMember<TRow, TKey>(Expression<Func<TModel, RunicQueryCollection<TRow, TKey>>> collection)
        where TKey : notnull => RunicMembers.WireName(RunicMembers.Property(collection));

    private JsonElement Succeeded(JsonDocument document, string kind, string member, string reply) =>
        document.RootElement.GetProperty("ok").GetBoolean() ? document.RootElement
            : throw new InvalidOperationException($"{Route} {kind} {member} failed: {reply}");
}

/// <summary>The rows of a <c>query-rows</c> reply, as a page receives them.</summary>
/// <param name="Version">The collection's version the rows were loaded at.</param>
/// <param name="Total">The rows that match the query, or <see langword="null"/> when it is not known.</param>
/// <param name="Start">The position of the first row.</param>
/// <param name="Keys">The rows' wire keys.</param>
/// <param name="Rows">The rows' wire JSON.</param>
public sealed record RunicQueryRows(long Version, int? Total, int Start, IReadOnlyList<string> Keys, IReadOnlyList<JsonElement> Rows);
