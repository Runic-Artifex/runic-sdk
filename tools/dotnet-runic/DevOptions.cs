using System;
using System.Collections.Generic;

namespace Runic.Application.Tool;

internal sealed record DevOptions(
    string? Project,
    string Configuration,
    bool Restore,
    bool WatchFrontend,
    bool WatchHost,
    bool DryRun,
    IReadOnlyList<string> ApplicationArguments);

/// <summary>A usage failure.</summary>
/// <remarks>
/// The message reaches the JSON fault and must not contain absolute paths.
/// <see cref="LocalDetail"/> carries paths and hints for human output only.
/// </remarks>
internal sealed class DevUsageException(string code, string message, string? localDetail = null) : Exception(message)
{
    internal string Code { get; } = code;
    internal string? LocalDetail { get; } = localDetail;
}

/// <summary>A development failure; see <see cref="DevUsageException"/> for the message rules.</summary>
internal sealed class DevDevelopmentException(string code, string message, string? localDetail = null) : Exception(message)
{
    internal string Code { get; } = code;
    internal string? LocalDetail { get; } = localDetail;
}
