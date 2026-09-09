using System;
using System.Text;

namespace Runic.CommandLine;

/// <summary>Formats discoverable help directly from the command catalog.</summary>
public static class CommandHelpFormatter
{
    /// <summary>Formats help for a root or resolved command path.</summary>
    public static string Format(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName = "--output")
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(path);
        CommandDescriptor? command = null;
        foreach (string segment in path.Segments)
        {
            if (command is null) catalog.TryGetCommand(segment, out command);
            else command.TryGetSubcommand(segment, out command);
        }
        var text = new StringBuilder("Usage: ").Append(applicationName);
        if (path.Count > 0) text.Append(' ').Append(path);
        else if (catalog.Commands.Count > 0) text.Append(" <command>");
        if (command is not null)
            foreach (CommandArgumentDescriptor argument in command.Arguments)
                text.Append(' ').Append(argument.Arity.Minimum == 0 ? '[' : '<').Append(argument.Help.ValueName ?? argument.Name)
                    .Append(argument.Arity.Maximum != 1 ? "..." : "").Append(argument.Arity.Minimum == 0 ? ']' : '>');
        text.Append(" [options]\n");
        if (command?.Help.Description is { } description) text.Append('\n').Append(description).Append('\n');
        var children = command?.Subcommands ?? catalog.Commands;
        if (children.Count > 0)
        {
            text.Append("\nCommands:\n");
            foreach (CommandDescriptor child in children)
                text.Append("  ").Append(child.Name).Append(ReferenceEquals(child, catalog.DefaultCommand) ? " (default)" : "").Append(child.Aliases.Count > 0 ? " (" + string.Join(", ", child.Aliases) + ")" : "")
                    .Append("  ").Append(child.Help.Description ?? child.DescriptionKey).Append('\n');
        }
        if (path.Count == 0 && !catalog.TryGetCommand("completion", out _)) text.Append("  completion <shell>  Generate bash, zsh, fish or PowerShell completions\n");
        if (command?.Arguments.Count > 0)
        {
            text.Append("\nArguments:\n");
            foreach (CommandArgumentDescriptor argument in command.Arguments)
                Parameter(text, argument.Name, argument.Help, argument.DescriptionKey, argument.IsSensitive, argument.Arity.Minimum > 0);
        }
        text.Append("\nOptions:\n");
        if (command is not null)
            foreach (CommandOptionDescriptor option in command.Options)
                Parameter(text, string.Join(", ", new[] { option.Name }.ConcatAliases(option.Aliases)) +
                    (option.Arity.Maximum != 0 ? " <" + (option.Help.ValueName ?? option.Id) + (option.Arity.Maximum != 1 ? "..." : "") + ">" : ""),
                    option.Help, option.DescriptionKey, option.IsSensitive, option.IsRequired);
        text.Append("  -h, --help  Show help\n  --version  Show version\n  ").Append(outputOptionName).Append(" <human|json>  Select output format\n");
        if (command?.Help.Examples.Count > 0)
        {
            text.Append("\nExamples:\n");
            foreach (string example in command.Help.Examples) text.Append("  ").Append(example).Append('\n');
        }
        return text.ToString();
    }

    private static void Parameter(StringBuilder text, string name, CommandHelp help, string? fallback, bool sensitive, bool required)
    {
        text.Append("  ").Append(name).Append("  ").Append(help.Description ?? fallback);
        if (required) text.Append(" [required]");
        if (!sensitive && help.DefaultValue is { } value) text.Append(" [default: ").Append(value).Append(']');
        if (!sensitive && help.Choices.Count > 0) text.Append(" [choices: ").AppendJoin(", ", help.Choices).Append(']');
        if (help.EnvironmentVariable is { } environment) text.Append(" [env: ").Append(environment).Append(']');
        text.Append('\n');
    }

    private static System.Collections.Generic.IEnumerable<string> ConcatAliases(this string[] names, System.Collections.Generic.IReadOnlyList<string> aliases)
    {
        foreach (string name in names) yield return name;
        foreach (string alias in aliases) yield return alias;
    }
}
