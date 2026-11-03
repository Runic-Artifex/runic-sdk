using System.Buffers;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>
/// The sort and filter of a <see cref="RunicQueryCollection{TRow, TKey}"/>: portable between the page and .NET, and
/// mapped to LINQ by <see cref="RunicQueryFields{TRow}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sort"/> orders the rows by its keys in turn; <see cref="Filters"/> all apply (a logical AND). Field names
/// are the names the page uses, normally the rows' wire names (<c>label</c> for a <c>Label</c> property). A row source
/// decides which fields it accepts and rejects the others with <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// A query is immutable. Two queries are equal when their canonical JSON (<see cref="ToString"/>) is equal.
/// The wire form is in <c>specs/application/README.md</c>, "Query collections".
/// </para>
/// </remarks>
public sealed class RunicQuery : IEquatable<RunicQuery>
{
    /// <summary>The most sort keys a query has.</summary>
    public const int MaximumSortKeys = 8;
    /// <summary>The most filters a query has.</summary>
    public const int MaximumFilters = 32;
    /// <summary>The longest field name, in UTF-16 code units.</summary>
    public const int MaximumFieldLength = 128;

    private string? _canonical;

    /// <summary>A query without sort keys and filters: the source's own order.</summary>
    public static RunicQuery Empty { get; } = new();

    /// <summary>Creates a query.</summary>
    /// <param name="sort">The sort keys, in order of precedence.</param>
    /// <param name="filters">The filters, which all apply.</param>
    /// <exception cref="ArgumentException">A field name is empty or too long, or there are too many keys or filters.</exception>
    public RunicQuery(IEnumerable<RunicSortKey>? sort = null, IEnumerable<RunicFilter>? filters = null)
    {
        var keys = sort?.ToArray() ?? [];
        var all = filters?.ToArray() ?? [];
        if (keys.Length > MaximumSortKeys) throw new ArgumentException($"A query has at most {MaximumSortKeys} sort keys.", nameof(sort));
        if (all.Length > MaximumFilters) throw new ArgumentException($"A query has at most {MaximumFilters} filters.", nameof(filters));
        foreach (var key in keys) CheckField(key.Field, nameof(sort));
        if (Array.IndexOf(all, null) >= 0) throw new ArgumentException("A query filter is null.", nameof(filters));
        Sort = new ReadOnlyCollection<RunicSortKey>(keys);
        Filters = new ReadOnlyCollection<RunicFilter>(all);
    }

    /// <summary>The sort keys, in order of precedence.</summary>
    public IReadOnlyList<RunicSortKey> Sort { get; }

    /// <summary>The filters, which all apply.</summary>
    public IReadOnlyList<RunicFilter> Filters { get; }

    /// <summary>Returns a query with these sort keys and this query's filters.</summary>
    /// <param name="sort">The sort keys, in order of precedence.</param>
    public RunicQuery WithSort(params IEnumerable<RunicSortKey> sort) => new(sort, Filters);

    /// <summary>Returns a query with this query's sort keys and these filters.</summary>
    /// <param name="filters">The filters, which all apply.</param>
    public RunicQuery WithFilters(params IEnumerable<RunicFilter> filters) => new(Sort, filters);

    /// <inheritdoc />
    public bool Equals(RunicQuery? other) => other is not null && (ReferenceEquals(this, other) || ToString() == other.ToString());

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as RunicQuery);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>The query's canonical wire JSON.</summary>
    public override string ToString() => _canonical ??= BridgeQueryWire.Encode(this);

    internal static void CheckField(string? field, string parameter)
    {
        if (string.IsNullOrEmpty(field) || field.Length > MaximumFieldLength)
            throw new ArgumentException($"A query field name has 1 to {MaximumFieldLength} characters.", parameter);
    }
}

/// <summary>One sort key of a <see cref="RunicQuery"/>.</summary>
/// <param name="Field">The field to sort by.</param>
/// <param name="Descending">Whether larger values come first.</param>
public readonly record struct RunicSortKey(string Field, bool Descending = false);

/// <summary>What a <see cref="RunicFilter"/> compares.</summary>
public enum RunicFilterOperator
{
    /// <summary>The field equals <see cref="RunicFilter.Value"/>. Wire name <c>equals</c>.</summary>
    Equal,
    /// <summary>The text field contains the string <see cref="RunicFilter.Value"/>. Wire name <c>contains</c>.</summary>
    Contains,
    /// <summary>
    /// The field is at least <see cref="RunicFilter.Minimum"/> and at most <see cref="RunicFilter.Maximum"/>; a bound
    /// without a value is open. Wire name <c>range</c>.
    /// </summary>
    Range,
    /// <summary>
    /// A filter the row source defines, named by <see cref="RunicFilter.Field"/>, with an optional JSON
    /// <see cref="RunicFilter.Value"/>. Wire name <c>custom</c>.
    /// </summary>
    Custom,
}

/// <summary>One filter of a <see cref="RunicQuery"/>. Values are JSON, as the page sends them.</summary>
public sealed class RunicFilter
{
    private RunicFilter(string field, RunicFilterOperator @operator, JsonElement value, JsonElement minimum, JsonElement maximum)
    {
        RunicQuery.CheckField(field, nameof(field));
        Field = field;
        Operator = @operator;
        Value = Detach(value);
        Minimum = Detach(minimum);
        Maximum = Detach(maximum);
    }

    /// <summary>The field the filter applies to, or the name of a <see cref="RunicFilterOperator.Custom"/> filter.</summary>
    public string Field { get; }

    /// <summary>What the filter compares.</summary>
    public RunicFilterOperator Operator { get; }

    /// <summary>
    /// The value of an <see cref="RunicFilterOperator.Equal"/>, <see cref="RunicFilterOperator.Contains"/> or
    /// <see cref="RunicFilterOperator.Custom"/> filter. <see cref="JsonValueKind.Undefined"/> when it has none.
    /// </summary>
    public JsonElement Value { get; }

    /// <summary>The inclusive lower bound of a range, or <see cref="JsonValueKind.Undefined"/> for none.</summary>
    public JsonElement Minimum { get; }

    /// <summary>The inclusive upper bound of a range, or <see cref="JsonValueKind.Undefined"/> for none.</summary>
    public JsonElement Maximum { get; }

    /// <summary>A filter that keeps rows whose <paramref name="field"/> equals <paramref name="value"/>.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The JSON value, encoded as the field's wire type.</param>
    public static RunicFilter Equal(string field, JsonElement value) => new(field, RunicFilterOperator.Equal, value, default, default);

    /// <summary>A filter that keeps rows whose <paramref name="field"/> equals the string <paramref name="value"/>.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The string.</param>
    public static RunicFilter Equal(string field, string? value) => Equal(field, Json(writer => writer.WriteStringValue(value)));

    /// <summary>A filter that keeps rows whose <paramref name="field"/> equals the number <paramref name="value"/>.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The number.</param>
    public static RunicFilter Equal(string field, double value) => Equal(field, Json(writer => writer.WriteNumberValue(value)));

    /// <summary>A filter that keeps rows whose <paramref name="field"/> equals the Boolean <paramref name="value"/>.</summary>
    /// <param name="field">The field.</param>
    /// <param name="value">The Boolean.</param>
    public static RunicFilter Equal(string field, bool value) => Equal(field, Json(writer => writer.WriteBooleanValue(value)));

    /// <summary>A filter that keeps rows whose text <paramref name="field"/> contains <paramref name="text"/>.</summary>
    /// <param name="field">The field.</param>
    /// <param name="text">The text to find.</param>
    public static RunicFilter Contains(string field, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(field, RunicFilterOperator.Contains, Json(writer => writer.WriteStringValue(text)), default, default);
    }

    /// <summary>A filter that keeps rows whose <paramref name="field"/> lies between two inclusive JSON bounds.</summary>
    /// <param name="field">The field.</param>
    /// <param name="minimum">The lower bound, or a default <see cref="JsonElement"/> for none.</param>
    /// <param name="maximum">The upper bound, or a default <see cref="JsonElement"/> for none.</param>
    public static RunicFilter Range(string field, JsonElement minimum, JsonElement maximum) =>
        new(field, RunicFilterOperator.Range, default, Bound(minimum), Bound(maximum));

    // A null bound is an open one.
    private static JsonElement Bound(JsonElement value) => value.ValueKind == JsonValueKind.Null ? default : value;

    /// <summary>A filter that keeps rows whose numeric <paramref name="field"/> lies between two inclusive bounds.</summary>
    /// <param name="field">The field.</param>
    /// <param name="minimum">The lower bound, or <see langword="null"/> for none.</param>
    /// <param name="maximum">The upper bound, or <see langword="null"/> for none.</param>
    public static RunicFilter Range(string field, double? minimum, double? maximum) => Range(field,
        minimum is { } low ? Json(writer => writer.WriteNumberValue(low)) : default,
        maximum is { } high ? Json(writer => writer.WriteNumberValue(high)) : default);

    /// <summary>A filter that the row source defines, such as "rows I own" or a full-text search.</summary>
    /// <param name="name">The filter's name.</param>
    /// <param name="value">Its JSON argument, or a default <see cref="JsonElement"/> for none.</param>
    public static RunicFilter Custom(string name, JsonElement value = default) => new(name, RunicFilterOperator.Custom, value, default, default);

    // A detached copy, so a filter does not depend on the document it was read from.
    private static JsonElement Detach(JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();

    internal static JsonElement Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
