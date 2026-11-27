using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Runic.Application.Tool;

/// <summary>
/// One diagnostic of a failure, as the session file's <c>failure.diagnostics[]</c> lists it: the file (absolute), the
/// 1-based line and column (null when the diagnostic names no position, such as a project's own error), the severity
/// (<c>error</c> or <c>warning</c>), the code and the message.
/// </summary>
internal sealed record DevDiagnostic(string File, int? Line, int? Column, string Severity, string Code, string Message);

/// <summary>
/// Why a <c>dotnet runic dev</c> stopped on a failure (D087), as the session file's <c>failure</c> member: dev's own code
/// and message, the .NET project and the frontend package that failed, and the diagnostics of the step that failed with
/// dev's own line last. An IDE shows them after dev has ended, whichever way it was started: VS Code removes a background
/// task's problems when its process ends.
/// </summary>
internal sealed partial record DevFailure(string Code, string Message, string? Project, string? Package, IReadOnlyList<DevDiagnostic> Diagnostics)
{
    /// <summary>The diagnostics the file lists at most, dev's own line included; a failed build can print thousands.</summary>
    internal const int MaxDiagnostics = 100;

    /// <summary>The characters of a message at most; a longer one ends with <c>…</c>.</summary>
    internal const int MaxMessageLength = 2000;

    /// <summary>
    /// The failure of an exception that stops dev: its diagnostics (errors first, then warnings, within
    /// <see cref="MaxDiagnostics"/>) and dev's own line on <paramref name="servedProject"/>, as
    /// <see cref="DevApplication.DiagnosticLine"/> prints it.
    /// </summary>
    internal static DevFailure Create(
        string code,
        string message,
        string servedProject,
        string? failedProject = null,
        string? package = null,
        IReadOnlyList<DevDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(servedProject);
        string served = Path.GetFullPath(servedProject);
        string bounded = Bound(message);
        DevDiagnostic own = new(served, null, null, "error", code, bounded);
        List<DevDiagnostic> listed = [.. (diagnostics ?? [])
            .Where(diagnostic => diagnostic != own)
            .OrderBy(static diagnostic => diagnostic.Severity == "error" ? 0 : 1)
            .Take(MaxDiagnostics - 1)
            .Select(static diagnostic => diagnostic with { Message = Bound(diagnostic.Message) }),
            own];
        return new(code, bounded, failedProject is null ? served : Path.GetFullPath(failedProject),
            package is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(package)), listed);
    }

    /// <summary>One line of at most <see cref="MaxMessageLength"/> characters.</summary>
    internal static string Bound(string message)
    {
        string line = message.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxMessageLength ? line : line[..(MaxMessageLength - 1)] + "…";
    }

    /// <summary>
    /// The MSBuild-form diagnostics of a step's output (tools/dotnet-runic/README.md, "Diagnostics"), each once: MSBuild
    /// repeats a build's diagnostics in its summary. A relative file resolves against <paramref name="directory"/> (the
    /// project's), except a frontend source, which a package's build tool writes relative to the package (tsc's own echo);
    /// the Views targets report it again with its full path, so it is left out. A line whose file is not a path is too.
    /// </summary>
    internal static IReadOnlyList<DevDiagnostic> Parse(string output, string directory)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(directory);
        var diagnostics = new List<DevDiagnostic>();
        var seen = new HashSet<DevDiagnostic>();
        foreach (Match match in Matches(output))
        {
            string file = match.Groups["file"].Value.Trim();
            if (!Path.IsPathRooted(file) && FrontendSource().IsMatch(file)) continue;
            string full;
            try
            {
                full = Path.GetFullPath(file, directory);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            var diagnostic = new DevDiagnostic(full,
                match.Groups["line"].Success ? int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                match.Groups["column"].Success ? int.Parse(match.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim());
            if (seen.Add(diagnostic)) diagnostics.Add(diagnostic);
        }
        return diagnostics;
    }

    /// <summary>
    /// The project in brackets after the first error of a step's output (MSBuild's <c>[…/SharedUi.csproj]</c>), or null:
    /// the .NET project whose build failed, such as a library's.
    /// </summary>
    internal static string? FirstErrorProject(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (Match match in Matches(output))
        {
            if (match.Groups["severity"].Value != "error") continue;
            string file = match.Groups["file"].Value.Trim();
            if (!Path.IsPathRooted(file) && FrontendSource().IsMatch(file)) continue;
            if (match.Groups["project"].Success) return match.Groups["project"].Value.Trim();
            if (file.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(file)) return file;
            return null;
        }
        return null;
    }

    /// <summary>
    /// The frontend package of a source file: the nearest folder above it with a <c>package.json</c>, or null.
    /// </summary>
    internal static string? PackageOf(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            for (string? folder = Path.GetDirectoryName(Path.GetFullPath(file)); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (File.Exists(Path.Combine(folder, "package.json"))) return folder;
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>The diagnostic lines of an output, one line at a time, so no match spans two lines.</summary>
    private static IEnumerable<Match> Matches(string output) =>
        output.Split('\n').Select(static line => Diagnostic().Match(line.TrimEnd('\r'))).Where(static match => match.Success);

    // The README's pattern for dev's lines and the build's own, with the project in brackets kept. The file holds no ": "
    // and the subcategory no colon, so a sentence that quotes a located error is not a diagnostic in a file it names.
    [GeneratedRegex(@"^(?:\[[A-Za-z][\w-]*\] )?\s*(?:\d+>)?(?<file>\S(?:(?!:\s).)*?)(?:\((?<line>\d+)(?:,(?<column>\d+))?(?:,\d+,\d+)?\))?\s*:\s+(?:[^\s:]+\s+)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\]]+)\])?$")]
    private static partial Regex Diagnostic();

    [GeneratedRegex(@"\.(?:[cm]?[jt]sx?|svelte|vue|s?css)$", RegexOptions.IgnoreCase)]
    private static partial Regex FrontendSource();
}
