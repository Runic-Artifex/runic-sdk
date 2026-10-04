using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Runic.Create;

internal sealed record OptionSelection(TemplateOption Option, TemplateChoice Choice);

/// <summary>The commands that create one project, and the commands that reproduce it.</summary>
internal sealed record CreatePlan(
    string Name,
    string? OutputDirectory,
    string Version,
    IReadOnlyList<OptionSelection> Selections,
    string? TemplateSource)
{
    public string Directory => OutputDirectory ?? Name;

    public IReadOnlyList<string> InstallArguments =>
    [
        "new", "install", $"{TemplateOptionModel.TemplatePackage}@{Version}",
        .. TemplateSource is null ? Array.Empty<string>() : ["--nuget-source", TemplateSource],
    ];

    public IReadOnlyList<string> CreateArguments =>
    [
        "new", TemplateOptionModel.TemplateShortName, "--name", Name,
        .. OutputDirectory is null ? Array.Empty<string>() : ["--output", OutputDirectory],
        .. Selections.SelectMany(selection => new[] { selection.Option.Flag, selection.Choice.Value }),
    ];

    /// <summary>The noninteractive creator invocation that selects the same options.</summary>
    public string CreatorCommand => Format(
    [
        "dnx", $"Runic.Create@{Version}", "--", Name,
        .. OutputDirectory is null ? Array.Empty<string>() : ["--directory", OutputDirectory],
        .. Selections.SelectMany(selection => new[] { selection.Option.Flag, selection.Choice.Value }),
    ]);

    public string InstallCommand => Format(["dotnet", .. InstallArguments]);

    public string CreateCommand => Format(["dotnet", .. CreateArguments]);

    public string Summary => string.Join(" · ", Selections.Select(selection => selection.Choice.DisplayName));

    public IReadOnlyList<string> NextSteps =>
    [
        Format(["cd", Directory]),
        "dotnet tool restore",
        "dotnet runic dev",
    ];

    /// <summary>
    /// Formats arguments for copying into a shell. Single quotes suit POSIX
    /// shells and PowerShell; only an apostrophe needs POSIX-specific escaping.
    /// </summary>
    internal static string Format(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':' or '@' or '=' or '+' or ','))
        {
            return argument;
        }
        var quoted = new StringBuilder("'");
        foreach (char character in argument)
        {
            quoted.Append(character == '\'' ? "'\"'\"'" : character.ToString());
        }
        return quoted.Append('\'').ToString();
    }
}
