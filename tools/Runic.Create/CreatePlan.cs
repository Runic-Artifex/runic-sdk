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
    string? TemplateSource,
    IReadOnlyList<TemplateToggle>? EnabledToggles = null)
{
    public string Directory => OutputDirectory ?? Name;

    /// <summary>The Boolean parameters this plan turns on, such as <c>--tests</c>.</summary>
    public IReadOnlyList<TemplateToggle> Toggles => EnabledToggles ?? [];

    public bool IsEnabled(string longName) => Toggles.Any(toggle => toggle.LongName == longName);

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
        .. Toggles.Select(toggle => toggle.Flag),
    ];

    /// <summary>The noninteractive creator invocation that selects the same options.</summary>
    public string CreatorCommand => Format(
    [
        "dnx", $"Runic.Create@{Version}", "--", Name,
        .. OutputDirectory is null ? Array.Empty<string>() : ["--output", OutputDirectory],
        .. Selections.SelectMany(selection => new[] { selection.Option.Flag, selection.Choice.Value }),
        .. Toggles.Select(toggle => toggle.Flag),
    ]);

    public string InstallCommand => Format(["dotnet", .. InstallArguments]);

    public string CreateCommand => Format(["dotnet", .. CreateArguments]);

    public string Summary => string.Join(" · ",
        Selections.Select(selection => selection.Choice.DisplayName).Concat(Toggles.Select(toggle => toggle.DisplayName)));

    public IReadOnlyList<string> NextSteps =>
    [
        Format(["cd", Directory]),
        "dotnet tool restore",
        "dotnet runic dev",
        .. IsEnabled("tests") ? [Format(["dotnet", "test", "--project", Name + ".Tests"])] : Array.Empty<string>(),
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
