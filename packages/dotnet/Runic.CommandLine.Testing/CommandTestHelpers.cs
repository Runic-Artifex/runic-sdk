using System;
using System.Text.Json;
using System.Threading;

namespace Runic.CommandLine.Testing;

/// <summary>Creates deterministic parsing inputs without mutating process environment.</summary>
public static class CommandTestEnvironment
{
    /// <summary>Creates parsing settings with the supplied captured output value.</summary>
    public static ParseSettings ParseSettings(string? output = null) => new(output);
}

/// <summary>Creates deterministic parsed invocations for command tests.</summary>
public static class CommandTestInvocation
{
    /// <summary>Parses arguments and returns the successful invocation.</summary>
    public static ParsedInvocation Parse(CommandCatalog catalog, ParseSettings settings, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(args);
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, settings);
        return outcome.Invocation ?? throw new InvalidOperationException("The supplied test arguments did not produce an invocation.");
    }

    /// <summary>Parses arguments with the default captured settings.</summary>
    public static ParsedInvocation Parse(CommandCatalog catalog, params string[] args) => Parse(catalog, ParseSettings.Default, args);
}

/// <summary>Creates already-cancelled tokens for deterministic cancellation tests.</summary>
public static class CommandTestCancellation
{
    /// <summary>Gets a token that is already cancelled.</summary>
    public static CancellationToken CancelledToken { get; } = new(canceled: true);
}

/// <summary>Reads the stable command response envelope in tests.</summary>
public static class CommandTestEnvelope
{
    /// <summary>Parses exactly one JSON command response frame.</summary>
    public static JsonDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        int lineFeedCount = 0;
        foreach (char character in json) if (character == '\n') lineFeedCount++;
        if (json.Length < 2 || !json.EndsWith('\n') || lineFeedCount != 1 || char.IsWhiteSpace(json[0]) || char.IsWhiteSpace(json[^2])) throw new ArgumentException("A command JSON frame must be one unpadded JSON value with exactly one terminal LF.", nameof(json));
        return JsonDocument.Parse(json);
    }
}
