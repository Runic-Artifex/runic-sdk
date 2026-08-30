using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Runic.CommandLine;

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
    private const int MaximumHumanOutputBytes = 1_048_576;
    private static readonly IReadOnlyList<CommandDiagnostic> NoDiagnostics =
        new ReadOnlyCollection<CommandDiagnostic>(Array.Empty<CommandDiagnostic>());
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal CommandOutcome(
        bool isSuccess,
        CommandExitCategory exitCategory,
        T? value,
        CommandFault? fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics,
        string? humanOutput)
    {
        IsSuccess = isSuccess;
        ExitCategory = exitCategory;
        Value = value;
        Fault = fault;
        Diagnostics = CopyDiagnostics(diagnostics, isSuccess);
        HumanOutput = CopyHumanOutput(humanOutput, isSuccess);
    }

    /// <summary>Gets a value indicating whether the command succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the semantic category used to select a process exit code.</summary>
    public CommandExitCategory ExitCategory { get; }

    /// <summary>Gets the typed value for a successful outcome.</summary>
    public T? Value { get; }

    /// <summary>Gets the safe fault for a failed outcome.</summary>
    public CommandFault? Fault { get; }

    /// <summary>
    /// Gets the ordered, immutable diagnostics produced while binding or
    /// executing this outcome.
    /// </summary>
    /// <remarks>
    /// An outcome holds at most 32 diagnostics so a successful binding outcome
    /// and a handler outcome can be combined without exceeding the response
    /// protocol's 64-diagnostic bound.
    /// </remarks>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Gets application-owned failure text that is written only to human
    /// standard output before diagnostics and the fault.
    /// </summary>
    /// <remarks>
    /// This value is never included in the <c>runic.commandline/1</c> JSON
    /// envelope. It is preserved exactly, including control characters, and is
    /// limited to one mebibyte of valid UTF-8 text.
    /// </remarks>
    public string? HumanOutput { get; }

    private static IReadOnlyList<CommandDiagnostic> CopyDiagnostics(
        IReadOnlyList<CommandDiagnostic>? diagnostics,
        bool isSuccess)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return NoDiagnostics;
        }

        const int maximumDiagnosticCount = 32;
        if (diagnostics.Count > maximumDiagnosticCount)
        {
            throw new ArgumentException(
                $"A command outcome cannot contain more than {maximumDiagnosticCount} diagnostics.",
                nameof(diagnostics));
        }

        var copy = new CommandDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            CommandDiagnostic diagnostic = diagnostics[index] ??
                throw new ArgumentException("Diagnostics cannot contain null entries.", nameof(diagnostics));
            if (isSuccess && diagnostic.Severity == CommandDiagnosticSeverity.Error)
            {
                throw new ArgumentException(
                    "A successful command outcome cannot contain error diagnostics.",
                    nameof(diagnostics));
            }

            copy[index] = diagnostic;
        }

        return new ReadOnlyCollection<CommandDiagnostic>(copy);
    }

    private static string? CopyHumanOutput(string? humanOutput, bool isSuccess)
    {
        if (string.IsNullOrEmpty(humanOutput))
        {
            return null;
        }

        if (isSuccess)
        {
            throw new ArgumentException(
                "A successful command outcome cannot contain failure-only human output.",
                nameof(humanOutput));
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(humanOutput);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Human output must contain valid Unicode text.",
                nameof(humanOutput),
                exception);
        }

        if (byteCount > MaximumHumanOutputBytes)
        {
            throw new ArgumentException(
                $"Human output cannot exceed {MaximumHumanOutputBytes} UTF-8 bytes.",
                nameof(humanOutput));
        }

        return new string(humanOutput.AsSpan());
    }
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
    public static CommandOutcome<T> Success<T>(T value) => Success<T>(value, null);

    /// <summary>
    /// Creates a successful outcome with warning or information diagnostics.
    /// </summary>
    /// <typeparam name="T">The command result type.</typeparam>
    /// <param name="value">The typed command result.</param>
    /// <param name="diagnostics">
    /// Optional warning or information diagnostics to present with the value.
    /// </param>
    /// <returns>A successful outcome.</returns>
    public static CommandOutcome<T> Success<T>(
        T value,
        IReadOnlyList<CommandDiagnostic>? diagnostics) =>
        new(true, CommandExitCategory.Success, value, null, diagnostics, null);

    /// <summary>Creates a failed outcome with a safe fault.</summary>
    /// <param name="category">A failure category.</param>
    /// <param name="fault">The stable, consumer-safe fault.</param>
    /// <returns>A failed outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is <see cref="CommandExitCategory.Success"/>
    /// or is not a defined category.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="fault"/> is null.</exception>
    public static CommandOutcome<T> Failure<T>(
        CommandExitCategory category,
        CommandFault fault) => Failure<T>(category, fault, null, null);

    /// <summary>Creates a failed outcome with a safe fault and diagnostics.</summary>
    /// <param name="category">A failure category.</param>
    /// <param name="fault">The stable, consumer-safe fault.</param>
    /// <param name="diagnostics">
    /// Optional binding or execution diagnostics to present with the fault.
    /// </param>
    /// <returns>A failed outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is <see cref="CommandExitCategory.Success"/>
    /// or is not a defined category.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="fault"/> is null.</exception>
    public static CommandOutcome<T> Failure<T>(
        CommandExitCategory category,
        CommandFault fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics) => Failure<T>(category, fault, diagnostics, null);

    /// <summary>
    /// Creates a failed outcome with a safe fault, diagnostics, and human-only
    /// standard-output text.
    /// </summary>
    /// <param name="category">A failure category.</param>
    /// <param name="fault">The stable, consumer-safe fault.</param>
    /// <param name="diagnostics">
    /// Optional binding or execution diagnostics to present with the fault.
    /// </param>
    /// <param name="humanOutput">
    /// Optional application-owned text for human standard output only. It is
    /// never serialized into machine output.
    /// </param>
    /// <returns>A failed outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is <see cref="CommandExitCategory.Success"/>
    /// or is not a defined category.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="humanOutput"/> is invalid.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fault"/> is null.</exception>
    public static CommandOutcome<T> Failure<T>(
        CommandExitCategory category,
        CommandFault fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics,
        string? humanOutput)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (category == CommandExitCategory.Success || !Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new CommandOutcome<T>(false, category, default, fault, diagnostics, humanOutput);
    }
}
