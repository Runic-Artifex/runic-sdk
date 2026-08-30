using System;
using System.Collections.Generic;
using System.Globalization;

namespace Runic.CommandLine;

/// <summary>Reads source-generator supported values from a parser-neutral invocation.</summary>
public static class GeneratedCommandBinding
{
    /// <summary>Gets a required positional value.</summary>
    public static string Argument(ParsedInvocation invocation, string id) => Get(invocation.Arguments, id);

    /// <summary>Gets all trailing positional values in encounter order.</summary>
    public static IReadOnlyList<string> Arguments(ParsedInvocation invocation, string id)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return GetValues(invocation.Arguments, id);
    }

    /// <summary>Gets a required option value.</summary>
    public static string Option(ParsedInvocation invocation, string id) => Get(invocation.Options, id);

    /// <summary>Gets all repeated option values in encounter order.</summary>
    public static IReadOnlyList<string> Options(ParsedInvocation invocation, string id)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return GetValues(invocation.Options, id);
    }

    private static IReadOnlyList<string> GetValues(IReadOnlyList<CommandValueBinding> bindings, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (CommandValueBinding binding in bindings)
        {
            if (string.Equals(binding.Id, id, StringComparison.Ordinal)) return binding.Values;
        }
        return Array.Empty<string>();
    }

    /// <summary>Gets whether a Boolean flag was specified.</summary>
    public static bool Flag(ParsedInvocation invocation, string id)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (CommandValueBinding binding in invocation.Options)
        {
            if (string.Equals(binding.Id, id, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Converts an invariant integral command value.</summary>
    public static int ParseInt32(string value, string id) => Parse(value, id, static text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? (true, result) : (false, 0));

    /// <summary>Converts an invariant integral command value.</summary>
    public static long ParseInt64(string value, string id) => Parse(value, id, static text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? (true, result) : (false, 0));

    /// <summary>Converts an invariant decimal command value.</summary>
    public static decimal ParseDecimal(string value, string id) => Parse(value, id, static text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result) ? (true, result) : (false, 0));

    /// <summary>Converts an invariant floating-point command value.</summary>
    public static double ParseDouble(string value, string id) => Parse(value, id, static text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? (true, result) : (false, 0));

    /// <summary>Converts a GUID command value.</summary>
    public static Guid ParseGuid(string value, string id) => Parse(value, id, static text => System.Guid.TryParse(text, out Guid result) ? (true, result) : (false, default));

    private static string Get(IReadOnlyList<CommandValueBinding> bindings, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        foreach (CommandValueBinding binding in bindings)
        {
            if (string.Equals(binding.Id, id, StringComparison.Ordinal) && binding.Values.Count == 1) return binding.Values[0];
        }

        throw new GeneratedCommandBindingException(id, "A required value is missing.");
    }

    private static T Parse<T>(string value, string id, TryParser<T> parser)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (parser(value) is (true, T result)) return result;
        throw new GeneratedCommandBindingException(id, "The value is not valid for its declared type.");
    }

    private delegate (bool Success, T Value) TryParser<T>(string value);
}

/// <summary>Represents a safe source-generated binding failure.</summary>
public sealed class GeneratedCommandBindingException : Exception
{
    /// <summary>Initializes a binding failure.</summary>
    public GeneratedCommandBindingException(string parameterId, string message) : base(message)
    {
        ParameterId = parameterId;
    }

    /// <summary>Gets the stable catalog parameter identifier.</summary>
    public string ParameterId { get; }
}
