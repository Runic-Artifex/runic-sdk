using System;
using System.Collections.Generic;

namespace RunicCommandLine;

/// <summary>Represents one deterministic command-catalog validation issue.</summary>
public sealed record CommandCatalogIssue(string Code, string Location, string Message);

/// <summary>Thrown when a catalog cannot be frozen because its definitions are invalid.</summary>
public sealed class CommandCatalogValidationException : Exception
{
    internal CommandCatalogValidationException(IReadOnlyList<CommandCatalogIssue> issues)
        : base($"Command catalog validation failed with {issues.Count} issue(s).")
    {
        Issues = issues;
    }

    /// <summary>Gets issues in deterministic definition order.</summary>
    public IReadOnlyList<CommandCatalogIssue> Issues { get; }
}
