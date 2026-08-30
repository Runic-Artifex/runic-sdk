using System;
using System.Collections.Generic;

namespace Runic.Application.Tool;

internal sealed record DevOptions(
    string? Project,
    string Configuration,
    bool Restore,
    bool GenerateContracts,
    bool WatchFrontend,
    bool WatchHost,
    bool DryRun,
    IReadOnlyList<string> ApplicationArguments)
{ }

internal sealed class DevUsageException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

internal sealed class DevDevelopmentException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
