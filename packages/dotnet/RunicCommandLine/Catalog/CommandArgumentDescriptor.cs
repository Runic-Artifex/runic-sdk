namespace RunicCommandLine;

/// <summary>Describes a parser-neutral positional command argument.</summary>
public sealed class CommandArgumentDescriptor
{
    internal CommandArgumentDescriptor(
        string id,
        string name,
        CommandArity arity,
        bool isSensitive,
        string? descriptionKey)
    {
        Id = id;
        Name = name;
        Arity = arity;
        IsSensitive = isSensitive;
        DescriptionKey = descriptionKey;
    }

    /// <summary>Gets the stable parameter identifier used by binders.</summary>
    public string Id { get; }

    /// <summary>Gets the help-facing argument name.</summary>
    public string Name { get; }

    /// <summary>Gets the accepted value count.</summary>
    public CommandArity Arity { get; }

    /// <summary>Gets whether values must be redacted from diagnostics.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets the optional localization key for help text.</summary>
    public string? DescriptionKey { get; }
}
