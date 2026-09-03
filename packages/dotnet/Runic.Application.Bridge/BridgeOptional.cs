using System;

namespace Runic.Application.Bridge;

/// <summary>
/// Represents a wire property that may be missing. A present value may itself be
/// <see langword="null"/> when <typeparamref name="T"/> permits it.
/// </summary>
public readonly struct BridgeOptional<T> : IEquatable<BridgeOptional<T>>
{
    private readonly T? value;

    /// <summary>Creates a present property value.</summary>
    public BridgeOptional(T? value)
    {
        this.value = value;
        HasValue = true;
    }

    /// <summary>Gets whether the property was present on the wire.</summary>
    public bool HasValue { get; }

    /// <summary>Gets the present value.</summary>
    public T? Value => HasValue
        ? value
        : throw new InvalidOperationException("The optional bridge property is missing.");

    /// <summary>Gets the present value or <see langword="default"/> when missing.</summary>
    public T? GetValueOrDefault() => HasValue ? value : default;

    /// <inheritdoc />
    public bool Equals(BridgeOptional<T> other) =>
        HasValue == other.HasValue &&
        (!HasValue || EqualityComparer<T?>.Default.Equals(value, other.value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is BridgeOptional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HasValue
        ? HashCode.Combine(true, value)
        : 0;

    /// <summary>
    /// Converts a value to an optional property. A <see langword="null"/> value
    /// becomes a missing property; use the constructor to represent an explicit
    /// wire <see langword="null"/>.
    /// </summary>
    public static implicit operator BridgeOptional<T>(T? value) => value is null ? default : new(value);

    /// <summary>Gets the present value or the default value when the property is missing.</summary>
    public static implicit operator T?(BridgeOptional<T> optional) => optional.GetValueOrDefault();

    /// <summary>Compares two optional values.</summary>
    public static bool operator ==(BridgeOptional<T> left, BridgeOptional<T> right) => left.Equals(right);

    /// <summary>Compares two optional values.</summary>
    public static bool operator !=(BridgeOptional<T> left, BridgeOptional<T> right) => !left.Equals(right);
}
