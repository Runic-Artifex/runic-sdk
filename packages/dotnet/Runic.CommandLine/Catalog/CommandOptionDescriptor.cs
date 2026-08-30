using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Runic.CommandLine;

/// <summary>Defines how repeated occurrences of an option are handled.</summary>
public enum CommandOptionRepeatPolicy
{
    /// <summary>A second occurrence is a parse error.</summary>
    Error = 0,

    /// <summary>Values from every occurrence are appended in encounter order.</summary>
    Append = 1,
}

/// <summary>Describes a parser-neutral command option.</summary>
public sealed class CommandOptionDescriptor
{
    internal CommandOptionDescriptor(
        string id,
        string name,
        IReadOnlyList<string> aliases,
        CommandArity arity,
        CommandOptionRepeatPolicy repeatPolicy,
        bool isRequired,
        bool isSensitive,
        string? descriptionKey)
    {
        Id = id;
        Name = name;
        Aliases = aliases;
        Arity = arity;
        RepeatPolicy = repeatPolicy;
        IsRequired = isRequired;
        IsSensitive = isSensitive;
        DescriptionKey = descriptionKey;
    }

    /// <summary>Gets the stable parameter identifier used by binders.</summary>
    public string Id { get; }

    /// <summary>Gets the canonical option spelling, including its prefix.</summary>
    public string Name { get; }

    /// <summary>Gets alternate spellings in registration order.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Gets the value arity for one occurrence.</summary>
    public CommandArity Arity { get; }

    /// <summary>Gets the repeated-occurrence policy.</summary>
    public CommandOptionRepeatPolicy RepeatPolicy { get; }

    /// <summary>Gets whether this option accepts more than one occurrence.</summary>
    public bool AllowsMultipleOccurrences => RepeatPolicy == CommandOptionRepeatPolicy.Append;

    /// <summary>Gets whether the option must be supplied at least once.</summary>
    public bool IsRequired { get; }

    /// <summary>Gets whether values must be redacted from diagnostics.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets the optional localization key for help text.</summary>
    public string? DescriptionKey { get; }

    internal static IReadOnlyList<string> Freeze(IEnumerable<string> values) =>
        new ReadOnlyCollection<string>([.. values]);
}
