using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Runic.CommandLine;

/// <summary>Generates static shell completions from the same catalog used for help and parsing.</summary>
public static class CommandCompletion
{
    /// <summary>Generates a bash, zsh, fish, or PowerShell completion script.</summary>
    public static string Generate(CommandCatalog catalog, string executable, string shell)
        => Generate(catalog, executable, shell, "--output");

    /// <summary>Generates completions using the application's configured output selector.</summary>
    public static string Generate(CommandCatalog catalog, string executable, string shell, string outputOptionName)
    {
        _ = new ParseSettings(transportOutputOptionName: outputOptionName);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("Completion requires a single executable name containing letters, digits, dots, underscores or hyphens.", nameof(executable));
        var words = new SortedSet<string>(StringComparer.Ordinal) { "--help", "--version", outputOptionName, "human", "json", "help", "completion" };
        Add(catalog.Commands, words);
        string candidates = string.Join(' ', words);
        return shell switch
        {
            "bash" => $"# Generated from the {executable} command catalog.\ncomplete -o default -W {Quote(candidates)} {Quote(executable)}\n",
            "zsh" => $"#compdef {executable}\ncompadd -- {string.Join(' ', words.Select(Quote))}\n",
            "fish" => string.Concat(words.Select(word => $"complete -c {Quote(executable)} -a {Quote(word)}\n")),
            "powershell" or "pwsh" => $"Register-ArgumentCompleter -Native -CommandName '{executable}' -ScriptBlock {{ param($wordToComplete, $commandAst, $cursorPosition)\n  @({string.Join(", ", words.Select(PowerShellQuote))}) | Where-Object {{ $_.StartsWith($wordToComplete, [StringComparison]::OrdinalIgnoreCase) }} | ForEach-Object {{ [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }}\n}}\n",
            _ => throw new ArgumentException("Choose bash, zsh, fish or powershell.", nameof(shell)),
        };
    }

    private static void Add(IReadOnlyList<CommandDescriptor> commands, SortedSet<string> words)
    {
        foreach (CommandDescriptor command in commands)
        {
            words.Add(command.Name);
            foreach (string alias in command.Aliases) words.Add(alias);
            foreach (CommandOptionDescriptor option in command.Options)
            {
                words.Add(option.Name);
                foreach (string alias in option.Aliases) words.Add(alias);
                if (!option.IsSensitive) foreach (string choice in option.Help.Choices) AddChoice(choice, words);
            }
            foreach (CommandArgumentDescriptor argument in command.Arguments)
                if (!argument.IsSensitive) foreach (string choice in argument.Help.Choices) AddChoice(choice, words);
            Add(command.Subcommands, words);
        }
    }
    private static void AddChoice(string choice, SortedSet<string> words)
    {
        // Static word-list completion cannot represent whitespace-containing values.
        if (choice.Length > 0 && choice.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/')) words.Add(choice);
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
