using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>
/// The fields a page may sort and filter a <see cref="RunicQuery"/> by, and how each maps to a LINQ expression of the
/// row. <see cref="Apply"/> turns a query into <c>Where</c> and <c>OrderBy</c> calls, so an Entity Framework Core or other
/// LINQ provider runs it in the database.
/// </summary>
/// <typeparam name="TRow">The row type of the <see cref="IQueryable{T}"/>.</typeparam>
/// <remarks>
/// <para>
/// Only registered fields can be used: a query that names another field, or uses an operator a field does not
/// support, fails <see cref="Validate"/> with <see cref="ArgumentException"/>, which the page receives as
/// <c>invalid-input</c>. Register fields under the names the page uses, normally the rows' wire names.
/// </para>
/// <para>
/// A field accepts <see cref="RunicFilterOperator.Equal"/> for any supported value type, <see cref="RunicFilterOperator.Range"/>
/// for types with comparison operators (numbers, dates and times, <see cref="TimeSpan"/>), and
/// <see cref="RunicFilterOperator.Contains"/> when it is registered with <see cref="AddText"/>. Values are read from JSON
/// as the field's type: strings, Booleans, integers, floating-point and decimal numbers, <see cref="Guid"/>,
/// <see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/>,
/// <see cref="TimeSpan"/> (ISO 8601 or <c>c</c> format) and enums (by name or number).
/// </para>
/// </remarks>
public sealed class RunicQueryFields<TRow>
{
    private readonly Dictionary<string, Field> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<IQueryable<TRow>, JsonElement, IQueryable<TRow>>> _custom = new(StringComparer.Ordinal);

    /// <summary>Registers a field that can be sorted by and filtered with equality and, where the type allows, ranges.</summary>
    /// <typeparam name="TValue">The field's type.</typeparam>
    /// <param name="name">The field name the page uses.</param>
    /// <param name="selector">The field of a row, such as <c>row =&gt; row.Created</c>.</param>
    /// <returns>This instance.</returns>
    public RunicQueryFields<TRow> Add<TValue>(string name, Expression<Func<TRow, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        RunicQuery.CheckField(name, nameof(name));
        if (!RunicQueryValues.Supports(typeof(TValue)))
            throw new ArgumentException($"A query field of type {typeof(TValue).Name} is not supported.", nameof(selector));
        _fields.Add(name, new Field<TValue>(name, selector, text: false, ignoreCase: false));
        return this;
    }

    /// <summary>Registers a text field, which also supports <see cref="RunicFilterOperator.Contains"/>.</summary>
    /// <param name="name">The field name the page uses.</param>
    /// <param name="selector">The text of a row.</param>
    /// <param name="ignoreCase">
    /// Whether <c>contains</c> ignores case, by comparing <see cref="string.ToLower()"/> of both, which database providers
    /// translate. Otherwise the provider's own comparison applies.
    /// </param>
    /// <returns>This instance.</returns>
    public RunicQueryFields<TRow> AddText(string name, Expression<Func<TRow, string?>> selector, bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(selector);
        RunicQuery.CheckField(name, nameof(name));
        _fields.Add(name, new Field<string?>(name, selector, text: true, ignoreCase));
        return this;
    }

    /// <summary>Registers a <see cref="RunicFilterOperator.Custom"/> filter, the escape hatch for filters the typed model lacks.</summary>
    /// <param name="name">The filter's name.</param>
    /// <param name="apply">
    /// Applies the filter with its JSON argument (<see cref="JsonValueKind.Undefined"/> when the page sent none). It
    /// throws <see cref="ArgumentException"/> for an argument it does not accept.
    /// </param>
    /// <returns>This instance.</returns>
    public RunicQueryFields<TRow> AddCustom(string name, Func<IQueryable<TRow>, JsonElement, IQueryable<TRow>> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        RunicQuery.CheckField(name, nameof(name));
        _custom.Add(name, apply);
        return this;
    }

    /// <summary>Checks that every sort key and filter of <paramref name="query"/> names a field that supports it.</summary>
    /// <param name="query">The query.</param>
    /// <exception cref="ArgumentException">The query names an unknown field, or a value does not read as the field's type.</exception>
    public void Validate(RunicQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        foreach (var key in query.Sort) Lookup(key.Field);
        foreach (var filter in query.Filters)
        {
            if (filter.Operator == RunicFilterOperator.Custom)
            {
                if (!_custom.ContainsKey(filter.Field)) throw new ArgumentException($"The query has no custom filter '{filter.Field}'.", nameof(query));
                continue;
            }
            // Building the predicate reads its values.
            _ = Lookup(filter.Field).Predicate(filter);
        }
    }

    /// <summary>Applies <paramref name="query"/>'s filters to <paramref name="rows"/>.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="query">The query.</param>
    /// <returns>The filtered rows.</returns>
    /// <exception cref="ArgumentException">The query names an unknown field, or a value does not read as the field's type.</exception>
    public IQueryable<TRow> Filter(IQueryable<TRow> rows, RunicQuery query)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(query);
        foreach (var filter in query.Filters)
            rows = filter.Operator == RunicFilterOperator.Custom
                ? _custom.TryGetValue(filter.Field, out var custom) ? custom(rows, filter.Value)
                    : throw new ArgumentException($"The query has no custom filter '{filter.Field}'.", nameof(query))
                : rows.Where(Lookup(filter.Field).Predicate(filter));
        return rows;
    }

    /// <summary>
    /// Orders <paramref name="rows"/> by <paramref name="query"/>'s sort keys, then by <paramref name="key"/>, so that
    /// ranges of the result are stable.
    /// </summary>
    /// <typeparam name="TKey">The row key.</typeparam>
    /// <param name="rows">The rows.</param>
    /// <param name="query">The query.</param>
    /// <param name="key">The row key, the last sort key.</param>
    /// <returns>The ordered rows.</returns>
    public IOrderedQueryable<TRow> Sort<TKey>(IQueryable<TRow> rows, RunicQuery query, Expression<Func<TRow, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(key);
        IOrderedQueryable<TRow>? ordered = null;
        foreach (var sort in query.Sort) ordered = Lookup(sort.Field).Order(rows, ordered, sort.Descending);
        return ordered is null ? rows.OrderBy(key) : ordered.ThenBy(key);
    }

    /// <summary>Filters and orders <paramref name="rows"/>: <see cref="Filter"/>, then <see cref="Sort"/>.</summary>
    /// <typeparam name="TKey">The row key.</typeparam>
    /// <param name="rows">The rows.</param>
    /// <param name="query">The query.</param>
    /// <param name="key">The row key, the last sort key.</param>
    /// <returns>The filtered, ordered rows.</returns>
    public IOrderedQueryable<TRow> Apply<TKey>(IQueryable<TRow> rows, RunicQuery query, Expression<Func<TRow, TKey>> key) =>
        Sort(Filter(rows, query), query, key);

    private Field Lookup(string name) => _fields.TryGetValue(name, out var field) ? field
        : throw new ArgumentException($"The query has no field '{name}'.", nameof(name));

    private abstract class Field(string name)
    {
        public string Name { get; } = name;
        public abstract IOrderedQueryable<TRow> Order(IQueryable<TRow> rows, IOrderedQueryable<TRow>? ordered, bool descending);
        public abstract Expression<Func<TRow, bool>> Predicate(RunicFilter filter);
    }

    private sealed class Field<TValue>(string name, Expression<Func<TRow, TValue>> selector, bool text, bool ignoreCase) : Field(name)
    {
        // Whether Expression has comparison operators for the type (lifted for nullable types).
        private readonly bool _ordered = HasComparison();

        public override IOrderedQueryable<TRow> Order(IQueryable<TRow> rows, IOrderedQueryable<TRow>? ordered, bool descending) =>
            ordered is null
                ? descending ? rows.OrderByDescending(selector) : rows.OrderBy(selector)
                : descending ? ordered.ThenByDescending(selector) : ordered.ThenBy(selector);

        public override Expression<Func<TRow, bool>> Predicate(RunicFilter filter)
        {
            var body = selector.Body;
            Expression condition;
            switch (filter.Operator)
            {
                case RunicFilterOperator.Equal:
                    condition = Expression.Equal(body, Constant(filter.Value, "value"));
                    break;
                case RunicFilterOperator.Contains:
                    if (!text) throw new ArgumentException($"The query field '{Name}' does not support contains.", nameof(filter));
                    if (filter.Value.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"The contains filter of '{Name}' needs a string.", nameof(filter));
                    var needle = filter.Value.GetString()!;
                    Expression haystack = body;
                    if (ignoreCase)
                    {
                        haystack = Expression.Call(body, ToLowerMethod);
#pragma warning disable CA1308, CA1304, CA1311 // The value must match the provider's ToLower translation of the field.
                        needle = needle.ToLower();
#pragma warning restore CA1308, CA1304, CA1311
                    }
                    condition = Expression.AndAlso(
                        Expression.NotEqual(body, Expression.Constant(null, typeof(string))),
                        Expression.Call(haystack, ContainsMethod, Expression.Constant(needle, typeof(string))));
                    break;
                case RunicFilterOperator.Range:
                    if (!_ordered) throw new ArgumentException($"The query field '{Name}' does not support ranges.", nameof(filter));
                    condition = Expression.Constant(true);
                    if (filter.Minimum.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                        condition = Expression.GreaterThanOrEqual(body, Constant(filter.Minimum, "min"));
                    if (filter.Maximum.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    {
                        var upper = Expression.LessThanOrEqual(body, Constant(filter.Maximum, "max"));
                        condition = condition is ConstantExpression ? upper : Expression.AndAlso(condition, upper);
                    }
                    break;
                default:
                    throw new ArgumentException($"The query field '{Name}' does not support {filter.Operator}.", nameof(filter));
            }
            return Expression.Lambda<Func<TRow, bool>>(condition, selector.Parameters);
        }

        private ConstantExpression Constant(JsonElement value, string member)
        {
            try { return Expression.Constant(RunicQueryValues.Read<TValue>(value), typeof(TValue)); }
            catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException or ArgumentException)
            {
                throw new ArgumentException($"The filter {member} of '{Name}' is not a {typeof(TValue).Name}.", nameof(value), error);
            }
        }

        private static bool HasComparison()
        {
            var type = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
            return type != typeof(string) && type != typeof(bool) && type != typeof(Guid) && !type.IsEnum;
        }
    }

    private static readonly System.Reflection.MethodInfo ContainsMethod = new Func<string, bool>(string.Empty.Contains).Method;
#pragma warning disable CA1304, CA1311 // The method that LINQ providers translate.
    private static readonly System.Reflection.MethodInfo ToLowerMethod = new Func<string>(string.Empty.ToLower).Method;
#pragma warning restore CA1304, CA1311
}

// Reads the JSON value of a filter as a field's type.
internal static class RunicQueryValues
{
    internal static bool Supports(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)
            || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint)
            || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double)
            || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan);
    }

    internal static TValue? Read<TValue>(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (default(TValue) is null) return default;
            throw new FormatException("The value is null.");
        }
        var type = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        return (TValue)Read(type, value);
    }

    private static object Read(Type type, JsonElement value)
    {
        var invariant = CultureInfo.InvariantCulture;
        if (type.IsEnum)
            return value.ValueKind == JsonValueKind.String
                ? Enum.Parse(type, value.GetString()!, ignoreCase: false)
                : Enum.ToObject(type, value.GetInt64());
        if (type == typeof(string)) return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Expected a string.");
        if (type == typeof(bool)) return value.GetBoolean();
        if (type == typeof(byte)) return value.GetByte();
        if (type == typeof(sbyte)) return value.GetSByte();
        if (type == typeof(short)) return value.GetInt16();
        if (type == typeof(ushort)) return value.GetUInt16();
        if (type == typeof(int)) return value.GetInt32();
        if (type == typeof(uint)) return value.GetUInt32();
        // int64 values cross as decimal strings (values.md); numbers are accepted too.
        if (type == typeof(long)) return value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, NumberStyles.AllowLeadingSign, invariant) : value.GetInt64();
        if (type == typeof(ulong)) return value.ValueKind == JsonValueKind.String ? ulong.Parse(value.GetString()!, NumberStyles.None, invariant) : value.GetUInt64();
        if (type == typeof(float)) return value.GetSingle();
        if (type == typeof(double)) return value.GetDouble();
        if (type == typeof(decimal)) return value.ValueKind == JsonValueKind.String ? decimal.Parse(value.GetString()!, NumberStyles.Number, invariant) : value.GetDecimal();
        if (type == typeof(Guid)) return value.GetGuid();
        if (type == typeof(DateTime)) return value.GetDateTime();
        if (type == typeof(DateTimeOffset)) return value.GetDateTimeOffset();
        if (type == typeof(DateOnly)) return DateOnly.ParseExact(String(value), "yyyy-MM-dd", invariant);
        if (type == typeof(TimeOnly)) return TimeOnly.Parse(String(value), invariant);
        if (type == typeof(TimeSpan))
        {
            var text = String(value);
            return text.StartsWith('P') || text.StartsWith("-P", StringComparison.Ordinal)
                ? System.Xml.XmlConvert.ToTimeSpan(text) : TimeSpan.ParseExact(text, "c", invariant);
        }
        throw new FormatException($"A query value of type {type.Name} is not supported.");
    }

    private static string String(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Expected a string.");
}
