using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WebUIToolkit.CommandLine;

/// <summary>Describes one immutable parser-neutral command node.</summary>
public sealed class CommandDescriptor
{
    private readonly Dictionary<string, CommandDescriptor> _subcommandsByName;
    private readonly Dictionary<string, CommandOptionDescriptor> _optionsByName;

    internal CommandDescriptor(
        string name,
        IReadOnlyList<string> aliases,
        string? descriptionKey,
        IReadOnlyList<CommandOptionDescriptor> options,
        IReadOnlyList<CommandArgumentDescriptor> arguments,
        IReadOnlyList<CommandDescriptor> subcommands,
        CommandRegistration registration)
    {
        Name = name;
        Aliases = aliases;
        DescriptionKey = descriptionKey;
        Options = options;
        Arguments = arguments;
        Subcommands = subcommands;
        Registration = registration;

        _subcommandsByName = new Dictionary<string, CommandDescriptor>(StringComparer.Ordinal);
        foreach (CommandDescriptor command in subcommands)
        {
            _subcommandsByName.Add(command.Name, command);
            foreach (string alias in command.Aliases)
            {
                _subcommandsByName.Add(alias, command);
            }
        }

        _optionsByName = new Dictionary<string, CommandOptionDescriptor>(StringComparer.Ordinal);
        foreach (CommandOptionDescriptor option in options)
        {
            _optionsByName.Add(option.Name, option);
            foreach (string alias in option.Aliases)
            {
                _optionsByName.Add(alias, option);
            }
        }
    }

    /// <summary>Gets the canonical command name.</summary>
    public string Name { get; }

    /// <summary>Gets command aliases in registration order.</summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>Gets the optional localization key for command help text.</summary>
    public string? DescriptionKey { get; }

    /// <summary>Gets command-local options in registration order.</summary>
    public IReadOnlyList<CommandOptionDescriptor> Options { get; }

    /// <summary>Gets positional arguments in binding order.</summary>
    public IReadOnlyList<CommandArgumentDescriptor> Arguments { get; }

    /// <summary>Gets child commands in registration order.</summary>
    public IReadOnlyList<CommandDescriptor> Subcommands { get; }

    internal CommandRegistration Registration { get; }

    /// <summary>Resolves a child command by canonical name or alias.</summary>
    public bool TryGetSubcommand(string token, out CommandDescriptor? command)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _subcommandsByName.TryGetValue(token, out command);
    }

    /// <summary>Resolves an option by canonical spelling or alias.</summary>
    public bool TryGetOption(string token, out CommandOptionDescriptor? option)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _optionsByName.TryGetValue(token, out option);
    }

    internal static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>([.. values]);
}
