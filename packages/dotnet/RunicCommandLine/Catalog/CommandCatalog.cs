using System;
using System.Collections.Generic;

namespace RunicCommandLine;

/// <summary>An immutable, thread-safe collection of command definitions.</summary>
public sealed class CommandCatalog
{
    private readonly Dictionary<string, CommandDescriptor> _commandsByName;

    internal CommandCatalog(IReadOnlyList<CommandDescriptor> commands)
    {
        Commands = commands;
        _commandsByName = new Dictionary<string, CommandDescriptor>(StringComparer.Ordinal);
        foreach (CommandDescriptor command in commands)
        {
            _commandsByName.Add(command.Name, command);
            foreach (string alias in command.Aliases)
            {
                _commandsByName.Add(alias, command);
            }
        }
    }

    /// <summary>Gets root commands in registration order.</summary>
    public IReadOnlyList<CommandDescriptor> Commands { get; }

    /// <summary>Resolves a root command by canonical name or alias.</summary>
    public bool TryGetCommand(string token, out CommandDescriptor? command)
    {
        ArgumentNullException.ThrowIfNull(token);
        return _commandsByName.TryGetValue(token, out command);
    }

    /// <summary>Resolves the longest leading command path.</summary>
    /// <param name="tokens">Already-tokenized command-line arguments.</param>
    /// <param name="command">The deepest resolved command, when any command matched.</param>
    /// <param name="consumed">The number of consumed command tokens.</param>
    /// <returns>True when at least one command matched.</returns>
    public bool TryResolve(ReadOnlySpan<string> tokens, out CommandDescriptor? command, out int consumed)
    {
        command = null;
        consumed = 0;
        if (tokens.IsEmpty || !_commandsByName.TryGetValue(tokens[0], out CommandDescriptor? current))
        {
            return false;
        }

        command = current;
        consumed = 1;
        while (consumed < tokens.Length && current!.TryGetSubcommand(tokens[consumed], out CommandDescriptor? child))
        {
            current = child;
            command = current;
            consumed++;
        }

        return true;
    }
}
