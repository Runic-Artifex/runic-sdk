using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Runic.Create;

internal sealed record OptionSelection(TemplateOption Option, TemplateChoice Choice);

/// <summary>A value given for one of the template's text parameters.</summary>
internal sealed record TextSelection(TemplateText Text, string Value);

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

    /// <summary>The text parameters given a value, such as <c>--application-id</c>.</summary>
    public IReadOnlyList<TextSelection> Texts { get; init; } = [];

    /// <summary>Passes <c>--force</c> to <c>dotnet new</c>, which creates the project in a directory that is not empty.</summary>
    public bool Force { get; init; }

    /// <summary>Skips <c>dotnet new install</c> and uses the installed template package.</summary>
    public bool NoInstall { get; init; }

    public IReadOnlyList<string> InstallArguments =>
    [
        "new", "install", $"{TemplateOptionModel.TemplatePackage}@{Version}",
        .. TemplateSource is null ? Array.Empty<string>() : ["--nuget-source", TemplateSource],
    ];

    public IReadOnlyList<string> CreateArguments =>
    [
        "new", TemplateOptionModel.TemplateShortName, "--name", Name,
        .. OutputDirectory is null ? Array.Empty<string>() : ["--output", OutputDirectory],
        .. TemplateArguments,
        .. Force ? ["--force"] : Array.Empty<string>(),
    ];

    /// <summary>The noninteractive creator invocation that selects the same options.</summary>
    public string CreatorCommand => Format(
    [
        "dnx", $"Runic.Create@{Version}", "--", Name,
        .. OutputDirectory is null ? Array.Empty<string>() : ["--output", OutputDirectory],
        .. TemplateArguments,
        .. Force ? ["--force"] : Array.Empty<string>(),
        .. NoInstall ? ["--no-install"] : Array.Empty<string>(),
    ]);

    // The template's options, which the creator and dotnet new spell the same way.
    private IEnumerable<string> TemplateArguments =>
    [
        .. Selections.SelectMany(selection => new[] { selection.Option.Flag, selection.Choice.Value }),
        .. Toggles.Select(toggle => toggle.Flag),
        .. Texts.SelectMany(text => new[] { text.Text.Flag, text.Value }),
    ];

    /// <summary>The commands the creator runs: the template install, unless <see cref="NoInstall"/>, then dotnet new.</summary>
    public IReadOnlyList<string> Commands => NoInstall ? [CreateCommand] : [InstallCommand, CreateCommand];

    public string InstallCommand => Format(["dotnet", .. InstallArguments]);

    public string CreateCommand => Format(["dotnet", .. CreateArguments]);

    public string Summary => string.Join(" · ",
        Selections.Select(selection => selection.Choice.DisplayName)
            .Concat(Toggles.Select(toggle => toggle.DisplayName))
            .Concat(Texts.Select(text => $"{text.Text.DisplayName} {text.Value}")));

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
