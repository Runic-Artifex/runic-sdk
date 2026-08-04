using System;

namespace RunicCommandLine;

/// <summary>
/// Represents a typed semantic outcome returned by a command handler.
/// </summary>
/// <typeparam name="T">The command result type.</typeparam>
/// <remarks>
/// This contract deliberately contains no parser or serializer types. A host
/// maps <see cref="ExitCategory"/> through an <see cref="IExitCodePolicy"/>
/// and presents either <see cref="Value"/> or <see cref="Fault"/>.
/// </remarks>
public sealed class CommandOutcome<T>
{
    internal CommandOutcome(
        bool isSuccess,
        CommandExitCategory exitCategory,
        T? value,
        CommandFault? fault)
    {
        IsSuccess = isSuccess;
        ExitCategory = exitCategory;
        Value = value;
        Fault = fault;
    }

    /// <summary>Gets a value indicating whether the command succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the semantic category used to select a process exit code.</summary>
    public CommandExitCategory ExitCategory { get; }

    /// <summary>Gets the typed value for a successful outcome.</summary>
    public T? Value { get; }

    /// <summary>Gets the safe fault for a failed outcome.</summary>
    public CommandFault? Fault { get; }

}

/// <summary>Creates invariant-preserving typed command outcomes.</summary>
public static class CommandOutcome
{
    /// <summary>
    /// Creates a successful outcome. A nullable result type may use a
    /// <see langword="null"/> value.
    /// </summary>
    /// <typeparam name="T">The command result type.</typeparam>
    /// <param name="value">The typed command result.</param>
    /// <returns>A successful outcome.</returns>
    public static CommandOutcome<T> Success<T>(T value) =>
        new(true, CommandExitCategory.Success, value, null);

    /// <summary>Creates a failed outcome with a safe fault.</summary>
    /// <param name="category">A failure category.</param>
    /// <param name="fault">The stable, consumer-safe fault.</param>
    /// <returns>A failed outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is <see cref="CommandExitCategory.Success"/>
    /// or is not a defined category.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="fault"/> is null.</exception>
    public static CommandOutcome<T> Failure<T>(CommandExitCategory category, CommandFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (category == CommandExitCategory.Success || !Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new CommandOutcome<T>(false, category, default, fault);
    }
}
