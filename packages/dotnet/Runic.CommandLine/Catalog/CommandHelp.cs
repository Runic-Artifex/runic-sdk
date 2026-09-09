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
    /// <summary>Gets empty metadata.</summary>
    public static CommandHelp Empty { get; } = new();
}
