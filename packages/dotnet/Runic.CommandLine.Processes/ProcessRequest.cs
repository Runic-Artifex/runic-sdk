using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine.Processes;

/// <summary>Describes one shell-free local process invocation.</summary>
public sealed class ProcessRequest
{
    /// <summary>Initializes a process request.</summary>
    /// <param name="fileName">Executable name or path. A shell command line is not accepted.</param>
    /// <param name="arguments">Arguments passed as distinct tokens.</param>
    /// <param name="workingDirectory">Optional child working directory.</param>
    /// <param name="environment">Optional child-only environment overrides; a null value removes a variable.</param>
    /// <param name="options">Bounded execution options.</param>
    /// <exception cref="ArgumentException">A required value is empty or contains a NUL character.</exception>
    public ProcessRequest(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        ProcessExecutionOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ThrowIfContainsNul(fileName, nameof(fileName));

        if (workingDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            ThrowIfContainsNul(workingDirectory, nameof(workingDirectory));
        }

        FileName = fileName;
        Arguments = CopyArguments(arguments);
        WorkingDirectory = workingDirectory;
        Environment = CopyEnvironment(environment);
        Options = options ?? new ProcessExecutionOptions();
    }

    /// <summary>Gets the executable name or path.</summary>
    public string FileName { get; }

    /// <summary>Gets the immutable argument-token view.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the optional working directory.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>Gets the immutable child environment override view.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; }

    /// <summary>Gets bounded execution options.</summary>
    public ProcessExecutionOptions Options { get; }

    private static IReadOnlyList<string> CopyArguments(IEnumerable<string>? arguments)
    {
        if (arguments is null)
        {
            return Array.Empty<string>();
        }

        var copy = new List<string>();
        foreach (string argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            ThrowIfContainsNul(argument, nameof(arguments));
            copy.Add(argument);
        }

        return copy.AsReadOnly();
    }

    private static ReadOnlyDictionary<string, string?> CopyEnvironment(
        IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null || environment.Count == 0)
        {
            return new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(0, GetEnvironmentComparer()));
        }

        var copy = new Dictionary<string, string?>(environment.Count, GetEnvironmentComparer());
        foreach (KeyValuePair<string, string?> pair in environment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key, nameof(environment));
            ThrowIfContainsNul(pair.Key, nameof(environment));
            if (pair.Key.Contains('=', StringComparison.Ordinal))
            {
                throw new ArgumentException("Environment variable names cannot contain '='.", nameof(environment));
            }
            if (pair.Value is not null)
            {
                ThrowIfContainsNul(pair.Value, nameof(environment));
            }

            copy.Add(pair.Key, pair.Value);
        }

        return new ReadOnlyDictionary<string, string?>(copy);
    }

    private static StringComparer GetEnvironmentComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static void ThrowIfContainsNul(string value, string parameterName)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Process values cannot contain NUL characters.", parameterName);
        }
    }
}
