using System;

namespace Runic.CommandLine;

/// <summary>Converts a custom value using a closed, reflection-free implementation.</summary>
public interface ICommandValueConverter<T>
{
    /// <summary>Converts a value or throws when it is invalid. Exception details are not displayed.</summary>
    static abstract T Parse(string value);
}

/// <summary>Validates a converted value using a closed implementation.</summary>
public interface ICommandValueValidator<T>
{
    /// <summary>Returns whether a value is acceptable.</summary>
    static abstract bool IsValid(T value);
}

/// <summary>Selects a custom parameter converter implementing ICommandValueConverter.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ConvertWithAttribute(Type converterType) : Attribute
{
    /// <summary>Gets the converter type.</summary>
    public Type ConverterType { get; } = converterType;
}

/// <summary>Selects a custom parameter validator implementing ICommandValueValidator.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ValidateWithAttribute(Type validatorType) : Attribute
{
    /// <summary>Gets the validator type.</summary>
    public Type ValidatorType { get; } = validatorType;
}
