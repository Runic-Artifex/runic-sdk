using System;

namespace RunicCommandLine;

/// <summary>Identifies the requested command result presentation.</summary>
public enum CommandOutputMode
{
    /// <summary>Localized presentation intended for a person.</summary>
    Human = 0,

    /// <summary>A single versioned JSON response envelope intended for a machine.</summary>
    Json = 1,
}

/// <summary>Identifies the winning source of an output-mode classification.</summary>
public enum CommandOutputModeSource
{
    /// <summary>The caller-provided default was used.</summary>
    Default = 0,

    /// <summary>The <c>RUNIC_COMMANDLINE_OUTPUT</c> environment value was used.</summary>
    Environment = 1,

    /// <summary>An explicit command-line argument was used.</summary>
    ExplicitArgument = 2,
}

/// <summary>
/// Represents the result of resolving explicit, environment, and default
/// output-mode configuration.
/// </summary>
public readonly record struct CommandOutputClassification
{
    internal CommandOutputClassification(
        bool isValid,
        CommandOutputMode? mode,
        CommandOutputModeSource? source,
        string? invalidEnvironmentValue)
    {
        IsValid = isValid;
        Mode = mode;
        Source = source;
        InvalidEnvironmentValue = invalidEnvironmentValue;
    }

    /// <summary>Gets a value indicating whether classification succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>Gets the selected mode, or null when classification failed.</summary>
    public CommandOutputMode? Mode { get; }

    /// <summary>Gets the winning source, or null when classification failed.</summary>
    public CommandOutputModeSource? Source { get; }

    /// <summary>
    /// Gets the unrecognized environment value when classification failed;
    /// otherwise, null.
    /// </summary>
    public string? InvalidEnvironmentValue { get; }
}

/// <summary>
/// Applies the stable precedence contract for command output presentation.
/// </summary>
public static class CommandOutputClassifier
{
    /// <summary>The environment variable that may supply a default output mode.</summary>
    public const string EnvironmentVariableName = "RUNIC_COMMANDLINE_OUTPUT";

    /// <summary>
    /// Resolves output mode without reading or mutating process-global state.
    /// </summary>
    /// <param name="explicitMode">
    /// The mode supplied explicitly by command syntax, if any. It has highest
    /// precedence and causes <paramref name="environmentValue"/> to be ignored.
    /// </param>
    /// <param name="environmentValue">
    /// The captured value of <see cref="EnvironmentVariableName"/>. Null or an
    /// empty string is treated as absent. Recognized values are <c>human</c>
    /// and <c>json</c>, compared using ordinal case-insensitive comparison.
    /// </param>
    /// <param name="defaultMode">The mode used when neither higher-precedence source is present.</param>
    /// <returns>A successful selection, or an invalid-environment classification.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="explicitMode"/> or <paramref name="defaultMode"/> is not defined.
    /// </exception>
    public static CommandOutputClassification Classify(
        CommandOutputMode? explicitMode,
        string? environmentValue,
        CommandOutputMode defaultMode = CommandOutputMode.Human)
    {
        if (explicitMode is CommandOutputMode selected)
        {
            ValidateMode(selected, nameof(explicitMode));
            return Valid(selected, CommandOutputModeSource.ExplicitArgument);
        }

        if (string.IsNullOrEmpty(environmentValue))
        {
            ValidateMode(defaultMode, nameof(defaultMode));
            return Valid(defaultMode, CommandOutputModeSource.Default);
        }

        if (string.Equals(environmentValue, "human", StringComparison.OrdinalIgnoreCase))
        {
            return Valid(CommandOutputMode.Human, CommandOutputModeSource.Environment);
        }

        if (string.Equals(environmentValue, "json", StringComparison.OrdinalIgnoreCase))
        {
            return Valid(CommandOutputMode.Json, CommandOutputModeSource.Environment);
        }

        return new CommandOutputClassification(false, null, null, environmentValue);
    }

    private static CommandOutputClassification Valid(
        CommandOutputMode mode,
        CommandOutputModeSource source) => new(true, mode, source, null);

    private static void ValidateMode(CommandOutputMode mode, string parameterName)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
