using System;
using System.Collections.Generic;

namespace Runic.CommandLine;

/// <summary>Presentation and value metadata shared by parsing, help and completion.</summary>
public sealed class CommandHelp
{
    /// <summary>Initializes immutable metadata. Sensitive parameters never display defaults.</summary>
    public CommandHelp(string? description = null, string? valueName = null, string? defaultValue = null,
        IEnumerable<string>? choices = null, IEnumerable<string>? examples = null,
        bool acceptsNegativeNumbers = false, string? environmentVariable = null)
    {
        Description = description;
        ValueName = valueName;
        DefaultValue = defaultValue;
        Choices = CommandDescriptor.Freeze(choices ?? Array.Empty<string>());
        Examples = CommandDescriptor.Freeze(examples ?? Array.Empty<string>());
        AcceptsNegativeNumbers = acceptsNegativeNumbers;
        EnvironmentVariable = environmentVariable;
    }
    /// <summary>Gets descriptive help text.</summary>
    public string? Description { get; }
    /// <summary>Gets the value placeholder.</summary>
    public string? ValueName { get; }
    /// <summary>Gets the display default, never a runtime default.</summary>
    public string? DefaultValue { get; }
    /// <summary>Gets allowed values in display order.</summary>
    public IReadOnlyList<string> Choices { get; }
    /// <summary>Gets runnable invocation examples.</summary>
    public IReadOnlyList<string> Examples { get; }
    /// <summary>Gets whether separated negative numeric values are accepted.</summary>
    public bool AcceptsNegativeNumbers { get; }
    /// <summary>Gets the environment variable used when an option is absent.</summary>
    public string? EnvironmentVariable { get; }
    /// <summary>Gets whether this entry is omitted from discovery, help listings and suggestions. Explicit invocation remains supported.</summary>
    public bool Hidden { get; init; }
    /// <summary>Gets extended command help shown after the summary.</summary>
    public string? LongDescription { get; init; }
    /// <summary>Gets the path completion and validation kind.</summary>
    public CommandPathKind PathKind { get; init; }
    /// <summary>Gets whether a path must already exist. Checked during execution, not hosted classification.</summary>
    public bool MustExist { get; init; }
    /// <summary>Gets the inclusive numeric minimum.</summary>
    public double? Minimum { get; init; }
    /// <summary>Gets the inclusive numeric maximum.</summary>
    public double? Maximum { get; init; }
    private IReadOnlyList<string> _requires = Array.Empty<string>();
    private IReadOnlyList<string> _conflictsWith = Array.Empty<string>();
    /// <summary>Gets option IDs required when this option is supplied, including captured environment values.</summary>
    public IReadOnlyList<string> Requires { get => _requires; init => _requires = CommandDescriptor.Freeze(value); }
    /// <summary>Gets option IDs forbidden when this option is supplied.</summary>
    public IReadOnlyList<string> ConflictsWith { get => _conflictsWith; init => _conflictsWith = CommandDescriptor.Freeze(value); }
    /// <summary>Gets empty metadata.</summary>
    public static CommandHelp Empty { get; } = new();
}

/// <summary>Describes filesystem completion and validation without selecting a UI toolkit.</summary>
public enum CommandPathKind
{
    /// <summary>No filesystem semantics.</summary>
    None = 0,
    /// <summary>A file path.</summary>
    File = 1,
    /// <summary>A directory path.</summary>
    Directory = 2,
}
