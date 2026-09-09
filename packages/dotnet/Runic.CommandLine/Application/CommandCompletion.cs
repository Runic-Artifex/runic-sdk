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
        var paths = new Dictionary<string, CommandPathKind>(StringComparer.Ordinal);
        CollectPaths(catalog.Commands, paths);
        string function = "_runic_" + executable.Replace('-', '_').Replace('.', '_');
        string cases = string.Join("\n", paths.Select(pair => $"    {Quote(pair.Key)}) kind={(pair.Value == CommandPathKind.Directory ? "d" : "f")} ;;"));
        string pathOptions = string.Join(' ', paths.Keys.Select(Quote));

        return shell switch
        {
            "bash" => $"# Generated from the {executable} command catalog.\n{function}() {{\n  local cur=\"${{COMP_WORDS[COMP_CWORD]}}\" prev=\"${{COMP_WORDS[COMP_CWORD-1]}}\" kind= item prefix=\n  if [[ \"$cur\" == --*=* ]]; then prev=\"${{cur%%=*}}\"; prefix=\"$prev=\"; cur=\"${{cur#*=}}\"; fi\n  case \"$prev\" in\n{cases}\n  esac\n  COMPREPLY=()\n  if [[ -n \"$kind\" ]]; then\n    while IFS= read -r item; do COMPREPLY+=(\"$prefix$item\"); done < <(compgen -\"$kind\" -- \"$cur\")\n    type compopt &>/dev/null && compopt -o filenames 2>/dev/null || true\n  else\n    while IFS= read -r item; do COMPREPLY+=(\"$item\"); done < <(compgen -W {Quote(candidates)} -- \"$cur\")\n  fi\n}}\ncomplete -o default -F {function} {Quote(executable)}\n",
            "zsh" => $"#compdef {executable}\n{function}() {{\n  local prev=\"${{words[CURRENT-1]}}\" kind=\n  case \"$prev\" in\n{cases}\n  esac\n  if [[ \"$kind\" == d ]]; then _files -/; elif [[ \"$kind\" == f ]]; then _files; else compadd -- {string.Join(' ', words.Select(Quote))}; _files; fi\n}}\ncompdef {function} {Quote(executable)}\n",
            "fish" => string.Concat(words.Select(word => $"complete -c {Quote(executable)} -a {Quote(word)}" + (paths.Count > 0 ? $" -n {Quote("not __fish_prev_arg_in " + pathOptions)}" : "") + "\n")) +
                string.Concat(paths.Select(pair => $"complete -c {Quote(executable)} -n {Quote("__fish_prev_arg_in " + Quote(pair.Key))} -r " +
                    (pair.Value == CommandPathKind.Directory ? "-f -a '(__fish_complete_directories)'" : "-F") + "\n")),
            "powershell" or "pwsh" => $"Register-ArgumentCompleter -Native -CommandName '{executable}' -ScriptBlock {{ param($wordToComplete, $commandAst, $cursorPosition)\n  $previous = @($commandAst.CommandElements | Where-Object {{ $_.Extent.EndOffset -lt $cursorPosition -and $_.Extent.Text -ne $wordToComplete }}) | Select-Object -Last 1\n  $paths = @{{ {string.Join("; ", paths.Select(pair => PowerShellQuote(pair.Key) + " = " + PowerShellQuote(pair.Value == CommandPathKind.Directory ? "d" : "f")))} }}\n  $selector = if ($previous) {{ $previous.Extent.Text }} else {{ '' }}\n  $prefix = ''\n  if ($wordToComplete -match '^(--[^=]+)=(.*)$') {{ $selector = $Matches[1]; $prefix = $selector + '='; $wordToComplete = $Matches[2] }}\n  if ($paths.ContainsKey($selector)) {{\n    [System.Management.Automation.CompletionCompleters]::CompleteFilename($wordToComplete) | Where-Object {{ $paths[$selector] -ne 'd' -or $_.ResultType -eq 'ProviderContainer' }} | ForEach-Object {{ [System.Management.Automation.CompletionResult]::new(($prefix + $_.CompletionText), $_.ListItemText, $_.ResultType, $_.ToolTip) }}\n    return\n  }}\n  @({string.Join(", ", words.Select(PowerShellQuote))}) | Where-Object {{ $_.StartsWith($wordToComplete, [StringComparison]::OrdinalIgnoreCase) }} | ForEach-Object {{ [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }}\n}}\n",
            _ => throw new ArgumentException("Choose bash, zsh, fish or powershell.", nameof(shell)),
        };
    }

    private static void Add(IReadOnlyList<CommandDescriptor> commands, SortedSet<string> words)
    {
        foreach (CommandDescriptor command in commands)
        {
            if (command.Help.Hidden) continue;
            words.Add(command.Name);
            foreach (string alias in command.Aliases) words.Add(alias);
            foreach (CommandOptionDescriptor option in command.Options)
            {
                if (option.Help.Hidden) continue;
                words.Add(option.Name);
                foreach (string alias in option.Aliases) words.Add(alias);
                if (!option.IsSensitive) foreach (string choice in option.Help.Choices) AddChoice(choice, words);
            }
            foreach (CommandArgumentDescriptor argument in command.Arguments)
                if (!argument.IsSensitive && !argument.Help.Hidden) foreach (string choice in argument.Help.Choices) AddChoice(choice, words);
            Add(command.Subcommands, words);
        }
    }
    private static void CollectPaths(IReadOnlyList<CommandDescriptor> commands, Dictionary<string, CommandPathKind> paths)
    {
        foreach (CommandDescriptor command in commands)
        {
            if (command.Help.Hidden) continue;
            foreach (CommandOptionDescriptor option in command.Options)
            {
                if (option.Help.Hidden || option.IsSensitive || option.Help.PathKind == CommandPathKind.None) continue;
                foreach (string name in new[] { option.Name }.Concat(option.Aliases))
                    // Mixed uses of an option spelling permit files and directories.
                    paths[name] = paths.TryGetValue(name, out var previous) && previous != option.Help.PathKind ? CommandPathKind.File : option.Help.PathKind;
            }
            CollectPaths(command.Subcommands, paths);
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
