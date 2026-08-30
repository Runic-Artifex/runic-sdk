using System;

namespace Runic.CommandLine;

/// <summary>Marks a static method as a command handled by the built-in source generator.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>Initializes a command with its canonical catalog name.</summary>
    public CommandAttribute(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Gets the canonical command name.</summary>
    public string Name { get; }
}

/// <summary>Marks one generated command as the root fallback for positional-only invocation.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class DefaultCommandAttribute : Attribute
{
}

/// <summary>Marks a parameter as a positional command argument.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ArgumentAttribute : Attribute
{
    /// <summary>Initializes an argument using its parameter name as the stable identifier.</summary>
    public ArgumentAttribute()
    {
    }

    /// <summary>Initializes an argument with an explicit stable identifier.</summary>
    public ArgumentAttribute(string id) => Id = id ?? throw new ArgumentNullException(nameof(id));

    /// <summary>Gets the optional stable argument identifier.</summary>
    public string? Id { get; }

    /// <summary>
    /// Gets or sets whether this trailing argument accepts all remaining
    /// positional values.
    /// </summary>
    /// <remarks>
    /// This is supported only for a trailing generated
    /// <c>IReadOnlyList&lt;string&gt;</c> argument. The default is one value.
    /// </remarks>
    public bool AllowMultipleValues { get; set; }
}

/// <summary>Marks a parameter as a named command option.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class OptionAttribute : Attribute
{
    /// <summary>Initializes an option with its canonical spelling and optional aliases.</summary>
    public OptionAttribute(string name, params string[] aliases)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
    }

    /// <summary>Gets the canonical option spelling.</summary>
    public string Name { get; }

    /// <summary>Gets alternate option spellings.</summary>
    public string[] Aliases { get; }

    /// <summary>
    /// Gets or sets whether one occurrence may consume all following values up
    /// to the next option.
    /// </summary>
    /// <remarks>
    /// This is supported only for generated <c>IReadOnlyList&lt;string&gt;</c>
    /// parameters. The default is one value for each occurrence.
    /// </remarks>
    public bool AllowMultipleValues { get; set; }

    /// <summary>Gets or sets whether a list option accepts repeated occurrences.</summary>
    /// <remarks>
    /// This applies only to generated <c>IReadOnlyList&lt;string&gt;</c> options.
    /// The default preserves existing behavior: values from repeated occurrences
    /// are appended in encounter order.
    /// </remarks>
    public bool AllowMultipleOccurrences { get; set; } = true;

    /// <summary>Gets or sets whether this option must be supplied.</summary>
    /// <remarks>
    /// A required flag must be present. A required repeated option must have at
    /// least one valid occurrence.
    /// </remarks>
    public bool Required { get; set; }
}

/// <summary>Marks a parameter as an explicitly required invocation-scoped service.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class FromServicesAttribute : Attribute
{
}

/// <summary>Supplies the machine payload identity emitted for a generated command result.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CommandResultAttribute : Attribute
{
    /// <summary>Initializes result metadata with a stable payload identity and source-generated context.</summary>
    public CommandResultAttribute(string payloadType, Type jsonContextType)
    {
        PayloadType = payloadType ?? throw new ArgumentNullException(nameof(payloadType));
        JsonContextType = jsonContextType ?? throw new ArgumentNullException(nameof(jsonContextType));
    }

    /// <summary>Gets the stable payload identity.</summary>
    public string PayloadType { get; }

    /// <summary>Gets the application-owned source-generated JSON context type.</summary>
    public Type JsonContextType { get; }
}
