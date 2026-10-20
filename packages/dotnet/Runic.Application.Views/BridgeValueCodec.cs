using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>Marks a public data member as deliberately included in a generated bridge contract.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false, Inherited = true)]
public sealed class RunicIncludeAttribute : Attribute;

/// <summary>Sets a stable JSON field or enum-case name without exposing CLR naming as protocol.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Enum,
    AllowMultiple = false, Inherited = false)]
public sealed class RunicAliasAttribute(string name) : Attribute
{
    /// <summary>The stable wire name.</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A Runic wire alias is required.", nameof(name));
}

/// <summary>Declares the complete derived-type set allowed at a polymorphic bridge boundary.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class RunicUnionAttribute(params Type[] cases) : Attribute
{
    /// <summary>The concrete case types.</summary>
    public Type[] Cases { get; } = cases is { Length: > 0 } && cases.All(type => type is not null)
        ? [.. cases]
        : throw new ArgumentException("A Runic union needs at least one concrete case.", nameof(cases));
}

/// <summary>Sets the stable discriminator used for one case of a Runic union.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RunicUnionCaseAttribute(string name) : Attribute
{
    /// <summary>The stable wire name.</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A Runic union case name is required.", nameof(name));
}

/// <summary>Associates a contract type or member with an explicit AOT-safe bridge codec.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class RunicCodecAttribute(Type codecType) : Attribute
{
    /// <summary>The codec type, which implements <see cref="IRunicCodec{T}"/>.</summary>
    public Type CodecType { get; } = codecType ?? throw new ArgumentNullException(nameof(codecType));
}

/// <summary>
/// Supplies the TypeScript representation and decoder expression for an
/// explicit bridge codec. The expression must contain <c>$value</c>, which the
/// generator replaces with the untrusted JSON value at the call site.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class RunicCodecShapeAttribute(string typeScriptType, string decoderExpression, string encoderExpression = "$value") : Attribute
{
    /// <summary>The public TypeScript type of the value.</summary>
    public string TypeScriptType { get; } = !string.IsNullOrWhiteSpace(typeScriptType)
        ? typeScriptType
        : throw new ArgumentException("A TypeScript codec shape is required.", nameof(typeScriptType));

    /// <summary>Frontend conversion from the JSON wire value; <c>$value</c> is replaced with the JSON value.</summary>
    public string DecoderExpression { get; } = !string.IsNullOrWhiteSpace(decoderExpression)
        && decoderExpression.Contains("$value", StringComparison.Ordinal)
        ? decoderExpression
        : throw new ArgumentException("A codec decoder expression must contain the $value placeholder.", nameof(decoderExpression));

    /// <summary>
    /// Frontend conversion from the public TypeScript value to its JSON wire
    /// value. The generator replaces <c>$value</c> with the source expression.
    /// </summary>
    public string EncoderExpression { get; } = !string.IsNullOrWhiteSpace(encoderExpression)
        && encoderExpression.Contains("$value", StringComparison.Ordinal)
        ? encoderExpression
        : throw new ArgumentException("A codec encoder expression must contain the $value placeholder.", nameof(encoderExpression));
}

/// <summary>
/// Implement this interface with public static methods to define an explicit
/// bridge representation for a type the generated standard codec does not
/// cover. The generator verifies the closed generic type and emits direct
/// static calls, so no codec discovery occurs at runtime.
/// </summary>
public interface IRunicCodec<T>
{
    /// <summary>Decodes one JSON value.</summary>
    static abstract T Read(JsonElement value);
    /// <summary>Writes one JSON value.</summary>
    static abstract void Write(Utf8JsonWriter writer, T value);
}
