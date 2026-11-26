using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Runic.CommandLine;

namespace Runic.Application.Tool;

/// <summary>The project a command runs on, and the solution it was chosen from, if any.</summary>
internal sealed record ProjectSelection(string Project, string? Solution = null);

internal static partial class ProjectDiscovery
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];

    /// <summary>
    /// The project of a command that takes any project (size, package): <paramref name="requestedProject"/>, a
    /// directory's single .csproj, or the working directory's.
    /// </summary>
    internal static string Find(string workingDirectory, string? requestedProject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string root = Path.GetFullPath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(requestedProject))
        {
            string selected = Path.GetFullPath(requestedProject, root);
            if (Directory.Exists(selected))
            {
                return FindSingleProject(selected);
            }

            RequireFile(selected);
            if (!IsProjectFile(selected))
            {
                throw new DevDevelopmentException(
                    "RAPPDEV1002",
                    "The selected project must be a .csproj file.",
                    $"Project: {selected}\n", category: CommandExitCategory.Unavailable);
            }

            return selected;
        }

        return FindSingleProject(root);
    }

    /// <summary>
    /// The Window project of dev and doctor (devtools-library-dx F9): as <see cref="Find"/>, and from a solution
    /// (.sln or .slnx) its single Window project (<c>RunicApplicationFrontendWindowProject=true</c>). A directory with
    /// one .csproj is that project, as before; otherwise its one solution decides. <paramref name="isWindowProject"/>
    /// evaluates a project: true or false, or null when MSBuild could not evaluate it.
    /// </summary>
    internal static async Task<ProjectSelection> FindWindowProjectAsync(
        string workingDirectory,
        string? requestedProject,
        string command,
        Func<string, CancellationToken, Task<bool?>> isWindowProject,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(isWindowProject);
        string root = Path.GetFullPath(workingDirectory);
        string directory = root;
        if (!string.IsNullOrWhiteSpace(requestedProject))
        {
            string selected = Path.GetFullPath(requestedProject, root);
            if (!Directory.Exists(selected))
            {
                RequireFile(selected);
                if (IsSolutionFile(selected))
                {
                    return await FromSolutionAsync(root, selected, command, isWindowProject, cancellationToken).ConfigureAwait(false);
                }
                if (!IsProjectFile(selected))
                {
                    throw new DevDevelopmentException(
                        "RAPPDEV1002",
                        "The selected project must be a .csproj file, or a .sln or .slnx solution.",
                        $"Project: {selected}\n", category: CommandExitCategory.Unavailable);
                }
                return new ProjectSelection(selected);
            }
            directory = selected;
        }

        string[] projects = ProjectFiles(directory);
        if (projects.Length == 1) return new ProjectSelection(projects[0]);
        string[] solutions = SolutionFiles(directory);
        if (solutions.Length == 1)
        {
            return await FromSolutionAsync(root, solutions[0], command, isWindowProject, cancellationToken).ConfigureAwait(false);
        }
        if (solutions.Length > 1)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1002",
                $"Multiple solution files were found in the directory: {string.Join(", ", solutions.Select(Path.GetFileName))}. " +
                $"Choose one with --project, for example 'dotnet runic {command} --project {Relative(root, solutions[0])}'.",
                $"Directory: {directory}\n", category: CommandExitCategory.Unavailable);
        }
        return new ProjectSelection(FindSingleProject(directory));
    }

    /// <summary>The single Window project of <paramref name="solution"/>, or RAPPDEV1002 that lists the candidates.</summary>
    private static async Task<ProjectSelection> FromSolutionAsync(
        string root,
        string solution,
        string command,
        Func<string, CancellationToken, Task<bool?>> isWindowProject,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projects = ReadSolutionProjects(solution);
        string name = Path.GetFileName(solution);
        if (projects.Count == 0)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1002",
                $"The solution {name} lists no .csproj project. Use --project to choose the Window project.",
                $"Solution: {solution}\n", category: CommandExitCategory.Unavailable);
        }
        // Evaluating is the slow part; four at a time keeps a large solution quick without overloading the machine.
        using var gate = new SemaphoreSlim(4);
        bool?[] results = await Task.WhenAll(projects.Select(async project =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await isWindowProject(project, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        string[] windows = [.. projects.Where((_, index) => results[index] == true)];
        string[] failed = [.. projects.Where((_, index) => results[index] is null)];
        if (windows.Length == 1) return new ProjectSelection(windows[0], solution);
        if (windows.Length == 0)
        {
            string detail = $"Solution: {solution}\nProjects: {string.Join(", ", projects.Select(project => Relative(root, project)))}\n" +
                (failed.Length == 0 ? string.Empty : $"MSBuild could not evaluate: {string.Join(", ", failed.Select(project => Relative(root, project)))}\n");
            throw new DevDevelopmentException(
                "RAPPDEV1002",
                $"The solution {name} has no Window project: none of its {projects.Count} .csproj projects sets RunicApplicationFrontendWindowProject=true" +
                (failed.Length == 0 ? "." : $", and MSBuild could not evaluate {failed.Length} of them.") +
                " Use --project to choose the Window project.",
                detail, category: CommandExitCategory.Unavailable);
        }
        throw new DevDevelopmentException(
            "RAPPDEV1002",
            $"The solution {name} has {windows.Length} Window projects: {string.Join(", ", windows.Select(project => Relative(root, project)))}. " +
            $"Choose one with --project, for example 'dotnet runic {command} --project {Relative(root, windows[0])}'.",
            $"Solution: {solution}\n", category: CommandExitCategory.Unavailable);
    }

    /// <summary>
    /// Evaluates <c>RunicApplicationFrontendWindowProject</c> of a project with MSBuild, without a restore or build: a
    /// Window project sets it in its project file. Null when MSBuild could not evaluate the project.
    /// </summary>
    internal static Func<string, CancellationToken, Task<bool?>> EvaluateWindowProject(string dotnetHost) =>
        async (project, cancellationToken) =>
        {
            CommandResult result;
            try
            {
                result = await CommandRunner.RunAsync(dotnetHost, Path.GetDirectoryName(project) ?? Environment.CurrentDirectory,
                    ["msbuild", project, "-nologo", "-getProperty:RunicApplicationFrontendWindowProject"], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is DevDevelopmentException or IOException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
            return result.ExitCode == 0 ? ParseWindowProjectValue(result.StandardOutput) : null;
        };

    /// <summary>The output of <c>-getProperty</c>: the bare value for one property, or JSON for several.</summary>
    internal static bool ParseWindowProjectValue(string output)
    {
        string value = output.Trim();
        if (value.StartsWith('{'))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                value = document.RootElement.TryGetProperty("Properties", out JsonElement properties) &&
                    properties.TryGetProperty("RunicApplicationFrontendWindowProject", out JsonElement property)
                        ? property.GetString() ?? string.Empty
                        : string.Empty;
            }
            catch (JsonException)
            {
                return false;
            }
        }
        return value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The .csproj projects that a .slnx (<c>&lt;Project Path="…" /&gt;</c>) or .sln (<c>Project("{…}") = "Name",
    /// "path", "{…}"</c>) lists and that exist, as full paths in the solution's order.
    /// </summary>
    internal static IReadOnlyList<string> ReadSolutionProjects(string solution)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(solution)) ?? Environment.CurrentDirectory;
        IEnumerable<string> paths;
        try
        {
            paths = Path.GetExtension(solution).Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? XDocument.Load(solution).Descendants()
                    .Where(static element => element.Name.LocalName == "Project")
                    .Select(static element => (string?)element.Attribute("Path"))
                    .OfType<string>()
                    .ToArray()
                : SolutionProject().Matches(File.ReadAllText(solution)).Select(static match => match.Groups["path"].Value).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1002",
                $"The solution {Path.GetFileName(solution)} could not be read: {error.Message}",
                $"Solution: {solution}\n", category: CommandExitCategory.Unavailable);
        }
        var projects = new List<string>();
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), folder);
            if (IsProjectFile(full) && File.Exists(full) && !projects.Contains(full, StringComparer.Ordinal)) projects.Add(full);
        }
        return projects;
    }

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex SolutionProject();

    private static string[] ProjectFiles(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)];

    private static string[] SolutionFiles(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Where(IsSolutionFile).Order(StringComparer.Ordinal)];

    private static bool IsProjectFile(string path) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path), ".csproj");

    private static bool IsSolutionFile(string path) =>
        SolutionExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void RequireFile(string selected)
    {
        if (!File.Exists(selected))
        {
            throw new DevDevelopmentException(
                "RAPPDEV1002",
                "The selected project does not exist.",
                $"Project: {selected}\n", category: CommandExitCategory.Unavailable);
        }
    }

    private static string FindSingleProject(string directory)
    {
        string[] projects = ProjectFiles(directory);
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new DevDevelopmentException(
                "RAPPDEV1002",
                "No .csproj was found in the directory. Use --project.",
                $"Directory: {directory}\n", category: CommandExitCategory.Unavailable),
            _ => throw new DevDevelopmentException(
                "RAPPDEV1002",
                "Multiple .csproj files were found in the directory. Use --project.",
                $"Directory: {directory}\n", category: CommandExitCategory.Unavailable),
        };
    }
}
