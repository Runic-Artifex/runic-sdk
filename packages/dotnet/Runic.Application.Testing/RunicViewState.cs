using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>A generated wire state of a <typeparamref name="TModel"/> route, read by ViewModel member.</summary>
public sealed class RunicViewState<TModel> where TModel : class
{
    internal RunicViewState(JsonElement state, long revision, JsonElement? fields)
    {
        if (state.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"The Bridge state is not an object: {state.GetRawText()}");
        Json = state.Clone();
        Revision = revision;
        Fields = fields?.Clone();
    }

    /// <summary>The wire state, including <c>revision</c>.</summary>
    public JsonElement Json { get; }

    /// <summary>The revision of the state.</summary>
    public long Revision { get; }

    /// <summary>The versions of the checked fields, <c>{member: {version}}</c>, or <see langword="null"/> when the View has none.</summary>
    public JsonElement? Fields { get; }

    /// <summary>A wire field by name.</summary>
    public JsonElement this[string field] => Json.TryGetProperty(field, out var value)
        ? value : throw new KeyNotFoundException($"The {typeof(TModel).Name} state has no field '{field}': {Json.GetRawText()}");

    /// <summary>The wire value of a state property.</summary>
    public JsonElement Get<TValue>(Expression<Func<TModel, TValue>> property) => this[RunicMembers.WireName(RunicMembers.Property(property))];

    /// <summary>
    /// Reads a scalar state property: a string, Boolean, Int32, Int64, double, decimal, GUID or
    /// enum, or a nullable one. Use <see cref="Get{TValue}"/> for other values, such as records,
    /// collections and content.
    /// </summary>
    /// <exception cref="NotSupportedException">The property is not a scalar. The message names
    /// the property and the call to use instead.</exception>
    public TValue Read<TValue>(Expression<Func<TModel, TValue>> property)
    {
        var member = RunicMembers.Property(property);
        if (!IsScalar(typeof(TValue)))
            throw new NotSupportedException(
                $"Read cannot convert {typeof(TModel).Name}.{member.Name} ({typeof(TValue).Name}): it reads only a string, Boolean, Int32, Int64, double, decimal, GUID or enum. " +
                $"Read a record or collection as JSON with Get(vm => vm.{member.Name}), for example Get(vm => vm.{member.Name}).GetProperty(\"field\"), or assert on the ViewModel itself.");
        return (TValue)Convert(this[RunicMembers.WireName(member)], typeof(TValue))!;
    }

    /// <summary>The content reference a ViewModel content property presents, or null.</summary>
    public PageReference? Reference(Expression<Func<TModel, object?>> property)
    {
        var value = Get(property);
        return value.ValueKind == JsonValueKind.Null ? null : ReferenceOf(value);
    }

    /// <summary>The content references a ViewModel collection presents.</summary>
    public IReadOnlyList<PageReference> References(Expression<Func<TModel, object?>> property) =>
        Get(property) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray().Select(ReferenceOf).ToArray() : [];

    /// <summary>The row keys of a <see cref="RunicCollectionAttribute"/> collection, in order.</summary>
    public IReadOnlyList<string> Keys(Expression<Func<TModel, object?>> collection)
    {
        var field = RunicMembers.WireName(RunicMembers.Property(collection));
        var key = RunicMembers.CollectionKey(typeof(TModel), field);
        return this[field].EnumerateArray().Select(item => RunicMembers.KeyOf(item, key)).ToArray();
    }

    /// <summary>Whether an argument-free command is available (<c>can{Command}</c>).</summary>
    public bool CanExecute(Expression<Func<TModel, object?>> command) =>
        this[$"can{RunicMembers.CommandName(RunicMembers.Property(command))}"].GetBoolean();

    /// <summary>Whether an asynchronous command is running (<c>is{Command}Executing</c>).</summary>
    public bool IsExecuting(Expression<Func<TModel, object?>> command) =>
        this[$"is{RunicMembers.CommandName(RunicMembers.Property(command))}Executing"].GetBoolean();

    /// <summary>The version of a checked field, which a checked write must name as its baseline.</summary>
    public long FieldVersion<TValue>(Expression<Func<TModel, TValue>> property)
    {
        var field = RunicMembers.WireName(RunicMembers.Property(property));
        return Fields is { } fields && fields.TryGetProperty(field, out var metadata)
            ? metadata.GetProperty("version").GetInt64()
            : throw new InvalidOperationException($"{typeof(TModel).Name}.{field} is not a checked field.");
    }

    /// <inheritdoc />
    public override string ToString() => Json.GetRawText();

    private static PageReference ReferenceOf(JsonElement value) =>
        new(value.GetProperty("kind").GetString()!, value.GetProperty("id").GetString()!);

    private static bool IsScalar(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return target == typeof(string) || target == typeof(bool) || target == typeof(int) || target == typeof(double)
            || target == typeof(long) || target == typeof(decimal) || target == typeof(Guid) || target.IsEnum;
    }

    private static object? Convert(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string)) return value.GetString();
        if (target == typeof(bool)) return value.GetBoolean();
        if (target == typeof(int)) return value.GetInt32();
        if (target == typeof(double)) return value.GetDouble();
        if (target == typeof(long)) return value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt64();
        if (target == typeof(decimal)) return value.ValueKind == JsonValueKind.String ? decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetDecimal();
        if (target == typeof(Guid)) return value.GetGuid();
        if (target.IsEnum && value.ValueKind == JsonValueKind.String)
        {
            var name = value.GetString()!;
            var field = target.GetFields(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(candidate => (candidate.GetCustomAttribute<RunicAliasAttribute>()?.Name ?? candidate.Name) == name);
            return field?.GetValue(null) ?? throw new FormatException($"'{name}' is not a {target.Name} wire name.");
        }
        throw new NotSupportedException($"Read cannot convert {type.Name}; use Get and read the JSON value.");
    }
}
