using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace WebUIToolkit.CommandLine;

/// <summary>Represents one version-1 command response before JSON framing.</summary>
/// <typeparam name="T">The registered payload type.</typeparam>
public sealed class CommandResponse<T>
{
    private static readonly IReadOnlyList<CommandDiagnostic> EmptyDiagnostics =
        Array.AsReadOnly(Array.Empty<CommandDiagnostic>());

    private CommandResponse(
        string requestId,
        string command,
        bool success,
        int exitCode,
        string? payloadType,
        T? payload,
        CommandFault? fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics)
    {
        CommandResponseValidation.ValidateRequestId(requestId, nameof(requestId));
        CommandResponseValidation.ValidateCommand(command, nameof(command));

        if (success)
        {
            if (exitCode != 0)
            {
                throw new ArgumentException("A successful response must use exit code zero.", nameof(exitCode));
            }

            CommandResponseValidation.ValidatePayloadType(payloadType, nameof(payloadType));
            if (fault is not null)
            {
                throw new ArgumentException("A successful response cannot contain a fault.", nameof(fault));
            }
        }
        else
        {
            if (exitCode == 0)
            {
                throw new ArgumentException("A failed response cannot use exit code zero.", nameof(exitCode));
            }

            if (payloadType is not null)
            {
                throw new ArgumentException("A failed response cannot declare a payload type.", nameof(payloadType));
            }

            ArgumentNullException.ThrowIfNull(fault);
        }

        RequestId = requestId;
        Command = command;
        Success = success;
        ExitCode = exitCode;
        PayloadType = payloadType;
        Payload = payload;
        Fault = fault is null ? null : CommandFaultSanitizer.Sanitize(fault);
        Diagnostics = CopyDiagnostics(diagnostics);
    }

    /// <summary>Gets the caller-supplied or generated opaque request identifier.</summary>
    public string RequestId { get; }

    /// <summary>Gets the canonical command path.</summary>
    public string Command { get; }

    /// <summary>Gets whether command execution succeeded.</summary>
    public bool Success { get; }

    /// <summary>Gets the mapped process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the independently versioned payload identity for a successful response.</summary>
    public string? PayloadType { get; }

    /// <summary>Gets the typed success payload.</summary>
    public T? Payload { get; }

    /// <summary>Gets the sanitized command fault for a failed response.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets the ordered, consumer-safe diagnostics.</summary>
    public IReadOnlyList<CommandDiagnostic> Diagnostics { get; }

    internal static CommandResponse<T> Succeeded(
        string requestId,
        string command,
        string payloadType,
        T? payload,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null) =>
        new(requestId, command, true, 0, payloadType, payload, null, diagnostics);

    internal static CommandResponse<T> Failed(
        string requestId,
        string command,
        int exitCode,
        CommandFault fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null) =>
        new(requestId, command, false, exitCode, null, default, fault, diagnostics);

    internal static CommandResponse<T> FromOutcome(
        string requestId,
        string command,
        int exitCode,
        string payloadType,
        CommandOutcome<T> outcome,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        CommandResponseValidation.ValidatePayloadType(payloadType, nameof(payloadType));
        if (outcome.IsSuccess && exitCode != 0)
        {
            throw new ArgumentException("A successful outcome must map to exit code zero.", nameof(exitCode));
        }

        if (!outcome.IsSuccess && exitCode == 0)
        {
            throw new ArgumentException("A failed outcome cannot map to exit code zero.", nameof(exitCode));
        }

        return outcome.IsSuccess
            ? Succeeded(requestId, command, payloadType, outcome.Value, diagnostics)
            : Failed(requestId, command, exitCode, outcome.Fault!, diagnostics);
    }

    internal static CommandResponse<T> Read(
        string requestId,
        string command,
        bool success,
        int exitCode,
        string? payloadType,
        T? payload,
        CommandFault? fault,
        IReadOnlyList<CommandDiagnostic> diagnostics) =>
        new(requestId, command, success, exitCode, payloadType, payload, fault, diagnostics);

    private static IReadOnlyList<CommandDiagnostic> CopyDiagnostics(
        IReadOnlyList<CommandDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return EmptyDiagnostics;
        }

        if (diagnostics.Count > 64)
        {
            throw new ArgumentException("A command response cannot contain more than 64 diagnostics.", nameof(diagnostics));
        }

        var copy = new CommandDiagnostic[diagnostics.Count];
        for (int index = 0; index < diagnostics.Count; index++)
        {
            copy[index] = diagnostics[index] ??
                throw new ArgumentException("Diagnostics cannot contain null entries.", nameof(diagnostics));
        }

        return new ReadOnlyCollection<CommandDiagnostic>(copy);
    }

}

internal static class CommandResponseValidation
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static void ValidateRequestId(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A request ID must contain valid Unicode text.", parameterName, exception);
        }

        if (byteCount > 128)
        {
            throw new ArgumentException("A request ID cannot exceed 128 UTF-8 bytes.", parameterName);
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw new ArgumentException("A request ID cannot contain control or whitespace characters.", parameterName);
            }
        }

        foreach (Rune rune in value.EnumerateRunes())
        {
            int scalar = rune.Value;
            if (scalar is >= 0xFDD0 and <= 0xFDEF || (scalar & 0xFFFE) == 0xFFFE)
            {
                throw new ArgumentException("A request ID cannot contain a Unicode noncharacter.", parameterName);
            }
        }
    }

    internal static void ValidateCommand(string command, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command, parameterName);
        if (StrictUtf8.GetByteCount(command) > 512)
        {
            throw new ArgumentException("A command path cannot exceed 512 UTF-8 bytes.", parameterName);
        }

        bool previousWasSpace = true;
        for (int index = 0; index < command.Length; index++)
        {
            char character = command[index];
            if (character == ' ')
            {
                if (previousWasSpace || index == command.Length - 1)
                {
                    throw new ArgumentException("A command path must use canonical space-separated names.", parameterName);
                }

                previousWasSpace = true;
                continue;
            }

            bool valid = previousWasSpace
                ? character is >= 'a' and <= 'z'
                : character is '-' or >= 'a' and <= 'z' or >= '0' and <= '9';
            if (!valid)
            {
                throw new ArgumentException("A command path must use canonical space-separated names.", parameterName);
            }

            previousWasSpace = false;
        }
    }

    internal static void ValidatePayloadType(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Encoding.UTF8.GetByteCount(value) > 128)
        {
            throw new ArgumentException("A payload type cannot exceed 128 UTF-8 bytes.", parameterName);
        }

        int slash = value.LastIndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || !IsName(value.AsSpan(0, slash)) ||
            !IsPositiveMajor(value.AsSpan(slash + 1)))
        {
            throw new ArgumentException(
                "A payload type must match <name>/<positive-major> with a lower-case invariant name.",
                parameterName);
        }
    }

    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (name[0] is < 'a' or > 'z')
        {
            return false;
        }

        for (int index = 1; index < name.Length; index++)
        {
            char character = name[index];
            if (character is not ('.' or '-' or >= 'a' and <= 'z' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPositiveMajor(ReadOnlySpan<char> major)
    {
        if (major.Length == 0 || major[0] is < '1' or > '9')
        {
            return false;
        }

        for (int index = 1; index < major.Length; index++)
        {
            if (major[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Creates invariant-preserving typed command responses.</summary>
public static class CommandResponse
{
    /// <summary>Creates a successful response.</summary>
    public static CommandResponse<T> Succeeded<T>(
        string requestId,
        string command,
        string payloadType,
        T? payload,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null) =>
        CommandResponse<T>.Succeeded(requestId, command, payloadType, payload, diagnostics);

    /// <summary>Creates a failed response.</summary>
    public static CommandResponse<T> Failed<T>(
        string requestId,
        string command,
        int exitCode,
        CommandFault fault,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null) =>
        CommandResponse<T>.Failed(requestId, command, exitCode, fault, diagnostics);

    /// <summary>Maps a frozen semantic outcome to a protocol response.</summary>
    public static CommandResponse<T> FromOutcome<T>(
        string requestId,
        string command,
        int exitCode,
        string payloadType,
        CommandOutcome<T> outcome,
        IReadOnlyList<CommandDiagnostic>? diagnostics = null) =>
        CommandResponse<T>.FromOutcome(
            requestId,
            command,
            exitCode,
            payloadType,
            outcome,
            diagnostics);
}
