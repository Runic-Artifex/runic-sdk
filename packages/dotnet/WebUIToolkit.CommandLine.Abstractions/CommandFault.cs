using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WebUIToolkit.CommandLine;

/// <summary>
/// Describes a stable, consumer-safe command fault.
/// </summary>
/// <remarks>
/// Faults are suitable for a machine response. Messages and details must not
/// contain stack traces, exception type names, secrets, environment variables,
/// or internal absolute paths.
/// </remarks>
public sealed record CommandFault
{
    private static readonly IReadOnlyDictionary<string, string> EmptyDetails =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(0, StringComparer.Ordinal));

    /// <summary>
    /// Initializes a new stable command fault.
    /// </summary>
    /// <param name="code">A stable, non-empty machine-readable fault code.</param>
    /// <param name="message">A safe, non-empty presentation message.</param>
    /// <param name="details">Optional safe string details. The values are defensively copied.</param>
    /// <param name="retryable">Whether retrying the same logical operation may succeed.</param>
    /// <exception cref="ArgumentException">
    /// A required value or detail key is empty, or a detail value is <see langword="null"/>.
    /// </exception>
    public CommandFault(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        bool retryable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Details = CopyDetails(details);
        Retryable = retryable;
    }

    /// <summary>Gets the stable machine-readable fault code.</summary>
    public string Code { get; }

    /// <summary>Gets the safe presentation message.</summary>
    public string Message { get; }

    /// <summary>Gets the immutable view of safe fault details.</summary>
    public IReadOnlyDictionary<string, string> Details { get; }

    /// <summary>Gets a value indicating whether the operation may succeed when retried.</summary>
    public bool Retryable { get; }

    private static IReadOnlyDictionary<string, string> CopyDetails(
        IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
        {
            return EmptyDetails;
        }

        var copy = new Dictionary<string, string>(details.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> detail in details)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(detail.Key, nameof(details));
            if (detail.Value is null)
            {
                throw new ArgumentException("Fault detail values cannot be null.", nameof(details));
            }

            copy.Add(detail.Key, detail.Value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
