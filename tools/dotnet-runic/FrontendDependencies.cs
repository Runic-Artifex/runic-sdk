using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// A frontend package that the served frontend depends on (devtools-library-dx §2.1): a client library's package, or a
/// workspace package that no .NET project owns. <see cref="Kind"/> is <c>compiled</c> when its package.json has a
/// <c>build</c> script, else <c>source</c>, which Vite serves from its sources.
/// </summary>
internal sealed record FrontendDependency(
    string Directory,
    string PackageName,
    string Kind,
    string? Owner,
    string? OwnerName,
    bool BuildEnabled,
    string BuildCommand,
    string WatchCommand)
{
    /// <summary>The specifier a <c>RunicApplicationFrontendClientModule</c> maps to this package, or null.</summary>
    internal string? Specifier { get; init; }

    /// <summary>The files its package.json exports, relative to <see cref="Directory"/> (<c>./dist/index.js</c>), from the graph.</summary>
    internal IReadOnlyList<string> Exports { get; init; } = [];

    /// <summary>The folders its build writes, relative to <see cref="Directory"/> (<c>./dist</c>), from the graph.</summary>
    internal IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>The prefix of its output lines: the folder name, as in <c>[shared-ui]</c>.</summary>
    internal string Label => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory)) is { Length: > 0 } name ? name : PackageName;

    /// <summary>The package's name, or its folder when package.json has none.</summary>
    internal string DisplayName => PackageName.Length != 0 ? PackageName : Directory;

    /// <summary>A compiled package that dev rebuilds or watches: its build is on and has a command.</summary>
    internal bool IsBuilt => Kind == "compiled" && BuildEnabled && BuildCommand.Length != 0;

    /// <summary>"the frontend package of SharedUi" or "a workspace package that no .NET project owns".</summary>
    internal string Description => OwnerName is { Length: > 0 } owner
        ? $"the frontend package of {owner}"
        : "a workspace package that no .NET project owns";

    /// <summary>The first exported file that does not exist, as an absolute path, or null.</summary>
    internal string? MissingExport() =>
        Exports.Select(export => Path.GetFullPath(export, Directory)).FirstOrDefault(static file => !File.Exists(file));
}

/// <summary>
/// Reads the served project's frontend dependencies from MSBuild (<c>RunicViewsGetFrontendDependencies</c>, which builds
/// nothing) and adds each package's exports and outputs from the frontend graph the build wrote
/// (<c>obj/runic/frontend-graph.json</c>, <c>rax.frontend-graph/1</c>).
/// </summary>
internal static partial class FrontendDependencies
{
    internal const string TargetName = "RunicViewsGetFrontendDependencies";

    /// <summary>RAPPDEV1018: a frontend package that the page depends on failed to build, or its exports are missing.</summary>
    internal const string FailureCode = "RAPPDEV1018";

    /// <summary>The file the build writes with the dependency package folders, one per line (S2's M5).</summary>
    internal static string ListPath(DevProjectConfiguration configuration) =>
        Path.Combine(configuration.SessionDirectory, "frontend-dependencies.txt");

    internal static string GraphPath(DevProjectConfiguration configuration) =>
        Path.Combine(configuration.SessionDirectory, "frontend-graph.json");

    /// <summary>
    /// Whether the last build found dependency packages. Without the list (no build yet, older targets, or no packages),
    /// dev runs no query, so a project without libraries starts as fast as before.
    /// </summary>
    internal static bool Listed(DevProjectConfiguration configuration)
    {
        try
        {
            string list = ListPath(configuration);
            return File.Exists(list) && File.ReadAllLines(list).Any(static line => line.Trim().Length != 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> CreateQueryArguments(DevProjectConfiguration configuration, string buildConfiguration) =>
    [
        "msbuild",
        configuration.ProjectPath,
        "-nologo",
        $"-property:Configuration={buildConfiguration}",
        $"-getTargetResult:{TargetName}",
    ];

    /// <summary>The dependencies, or none when the project lists none or the query fails (dev then watches nothing).</summary>
    internal static async Task<IReadOnlyList<FrontendDependency>> ReadAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        string buildConfiguration,
        CancellationToken cancellationToken)
    {
        if (!Listed(configuration)) return [];
        CommandResult result = await CommandRunner.RunAsync(dotnetHost, configuration.ProjectDirectory,
            CreateQueryArguments(configuration, buildConfiguration), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"[dev] Could not read the frontend dependencies ({TargetName} exited with code {result.ExitCode}); " +
                "dev does not watch the frontend packages that the page depends on.");
            return [];
        }
        string? graph = null;
        try
        {
            if (File.Exists(GraphPath(configuration))) graph = await File.ReadAllTextAsync(GraphPath(configuration), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            return Parse(result.StandardOutput, graph);
        }
        catch (JsonException error)
        {
            Console.Error.WriteLine($"[dev] Could not read the frontend dependencies: {error.Message}");
            return [];
        }
    }

    /// <summary>
    /// Parses <c>-getTargetResult</c>'s JSON, without the application's own frontend (<c>Kind</c> <c>application</c>),
    /// and takes exports and outputs from the graph's packages with the same directory.
    /// </summary>
    internal static IReadOnlyList<FrontendDependency> Parse(string targetResult, string? graph)
    {
        ArgumentNullException.ThrowIfNull(targetResult);
        int start = targetResult.IndexOf('{', StringComparison.Ordinal);
        if (start < 0) return [];
        using JsonDocument document = JsonDocument.Parse(targetResult.AsMemory(start));
        if (!document.RootElement.TryGetProperty("TargetResults", out JsonElement results) ||
            !results.TryGetProperty(TargetName, out JsonElement target) ||
            !target.TryGetProperty("Items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        Dictionary<string, (IReadOnlyList<string> Exports, IReadOnlyList<string> Outputs)> files = ReadGraph(graph);
        var dependencies = new List<FrontendDependency>();
        var seen = new HashSet<string>(PathComparer);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string Metadata(string name) => item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim()
                : string.Empty;
            string kind = Metadata("Kind");
            string directory = Metadata("Identity");
            if (kind == "application" || directory.Length == 0) continue;
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!seen.Add(directory)) continue;
            string owner = Metadata("Owner");
            (IReadOnlyList<string> exports, IReadOnlyList<string> outputs) = files.TryGetValue(directory, out var found) ? found : ([], []);
            dependencies.Add(new FrontendDependency(
                directory,
                Metadata("PackageName"),
                kind.Length == 0 ? "source" : kind,
                owner.Length == 0 ? null : owner,
                Metadata("OwnerName") is { Length: > 0 } ownerName ? ownerName : null,
                !string.Equals(Metadata("BuildEnabled"), "false", StringComparison.OrdinalIgnoreCase),
                Metadata("BuildCommand"),
                Metadata("WatchCommand"))
            {
                Specifier = Metadata("Specifier") is { Length: > 0 } specifier ? specifier : null,
                Exports = exports,
                Outputs = outputs,
            });
        }
        return dependencies;
    }

    private static Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)> ReadGraph(string? graph)
    {
        var files = new Dictionary<string, (IReadOnlyList<string>, IReadOnlyList<string>)>(PathComparer);
        if (string.IsNullOrWhiteSpace(graph)) return files;
        try
        {
            using JsonDocument document = JsonDocument.Parse(graph);
            if (!document.RootElement.TryGetProperty("packages", out JsonElement packages) || packages.ValueKind != JsonValueKind.Array) return files;
            foreach (JsonElement package in packages.EnumerateArray())
            {
                if (!package.TryGetProperty("directory", out JsonElement directory) || directory.ValueKind != JsonValueKind.String) continue;
                files[Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.GetString()!))] = (Strings(package, "exports"), Strings(package, "outputs"));
            }
        }
        catch (JsonException)
        {
        }
        return files;
    }

    private static string[] Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement values) && values.ValueKind == JsonValueKind.Array
            ? [.. values.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String).Select(static value => value.GetString()!)]
            : [];

    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The start-up check (D081.6): every compiled package that the page depends on must have its exports, or dev stops
    /// with RAPPDEV1018 before it starts the development server, instead of Vite failing to resolve the import.
    /// </summary>
    internal static void RequireBuilt(IReadOnlyList<FrontendDependency> dependencies, bool built)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        foreach (FrontendDependency dependency in dependencies)
        {
            if (dependency.Kind != "compiled" || dependency.MissingExport() is not { } missing) continue;
            string relative = Path.GetRelativePath(dependency.Directory, missing).Replace('\\', '/');
            string why = !built
                ? "--no-build ran no build; build the project once (dotnet build) or omit --no-build."
                : !dependency.BuildEnabled
                    ? $"its package build is off in {dependency.OwnerName} (RunicApplicationFrontendBuildEnabled=false), so build it yourself, or turn the build on."
                    : dependency.BuildCommand.Length == 0
                        ? "it has no build command."
                        : $"its build '{dependency.BuildCommand}' did not write it. Check the package's build script and its package.json exports.";
            throw new DevDevelopmentException(FailureCode,
                $"{dependency.DisplayName} is {dependency.Description}, and the page imports it, but it has not been built: " +
                $"{dependency.Label}/{relative} is missing; {why}",
                $"Package directory: {dependency.Directory}\n");
        }
    }

    /// <summary>
    /// A failed initial build that failed in a dependency package, as the build's first such error line: a client
    /// library's package build (RUNICBRIDGE016 of another project, or the located error of its build tool, such as
    /// <c>src/index.ts(3,1): error TS1005</c>, that the targets report for it), a workspace package's build that the served
    /// project ran, or a package export check (RUNICBRIDGE088). Null when the build failed elsewhere, such as in C#.
    /// </summary>
    internal static string? FindPackageFailure(string buildOutput, string servedProject, string servedFrontend)
    {
        ArgumentNullException.ThrowIfNull(buildOutput);
        string served = Path.GetFullPath(servedProject);
        string frontend = Path.TrimEndingDirectorySeparator(Path.GetFullPath(servedFrontend)) + Path.DirectorySeparatorChar;
        foreach (Match match in BuildError().Matches(buildOutput))
        {
            string file = match.Groups["file"].Value.Trim();
            string code = match.Groups["code"].Value;
            string message = match.Groups["message"].Value.Trim();
            // The project that reported it: the file itself for a project's own error, else the line's [project].
            string project = file.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? file
                : match.Groups["project"].Success ? match.Groups["project"].Value.Trim()
                : served;
            bool otherProject;
            string fullFile;
            try
            {
                otherProject = !PathComparer.Equals(Path.GetFullPath(project), served);
                fullFile = Path.GetFullPath(file);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            // A build tool's own relative line (tsc's echo) is not located yet; the targets report it again with its path.
            bool frontendSource = FrontendSourceExtensions.Contains(Path.GetExtension(file)) && Path.IsPathRooted(file);
            bool package = code == "RUNICBRIDGE088" ||
                (code == "RUNICBRIDGE016" && (otherProject || message.Contains("of the frontend package", StringComparison.Ordinal))) ||
                (frontendSource && (otherProject || !fullFile.StartsWith(frontend, PathComparison)));
            if (!package) continue;
            string location = match.Groups["location"].Success ? $"({match.Groups["location"].Value})" : string.Empty;
            return frontendSource ? $"{fullFile}{location}: error {code}: {message}" : $"{code}: {message}";
        }
        return null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly HashSet<string> FrontendSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs", ".svelte", ".vue", ".css", ".scss",
    };

    // MSBuild's canonical error form: <file>[(<location>)] : error <code>: <message> [<project>]
    [GeneratedRegex(@"^\s*(?:\d+>)?(?<file>[^\r\n]*?)(?:\((?<location>[\d,]+)\))?\s*:\s+error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>[^\r\n]*?)(?:\s+\[(?<project>[^\]\r\n]+)\])?\r?$", RegexOptions.Multiline)]
    private static partial Regex BuildError();

    /// <summary>
    /// The package a Vite error names: for <c>Failed to resolve import "@acme/shared-ui/generated"</c>, the dependency
    /// whose name is the specifier or its package part, or whose <see cref="FrontendDependency.Specifier"/> it is.
    /// </summary>
    internal static FrontendDependency? FindImported(IReadOnlyList<FrontendDependency> dependencies, string error)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(error);
        Match match = UnresolvedImport().Match(error);
        if (!match.Success) return null;
        string specifier = match.Groups["specifier"].Value;
        return dependencies.FirstOrDefault(dependency =>
            dependency.Specifier == specifier ||
            (dependency.PackageName.Length != 0 &&
             (specifier == dependency.PackageName || specifier.StartsWith(dependency.PackageName + "/", StringComparison.Ordinal))));
    }

    [GeneratedRegex(@"(?:Failed to resolve import|failed to resolve import|Cannot find module|Could not resolve)\s+[""'](?<specifier>[^""']+)[""']")]
    private static partial Regex UnresolvedImport();

    /// <summary>The sentence that a fail-fast Vite error gains when it names a dependency package.</summary>
    internal static string? Explain(IReadOnlyList<FrontendDependency> dependencies, string error)
    {
        if (FindImported(dependencies, error) is not { } dependency) return null;
        string state = dependency.Kind != "compiled"
            ? $"it is a source package in {dependency.Directory}; check its package.json exports and that the workspace install linked it"
            : dependency.MissingExport() is { } missing
                ? $"it has not been built ({dependency.Label}/{Path.GetRelativePath(dependency.Directory, missing).Replace('\\', '/')} is missing)"
                : $"its build '{dependency.BuildCommand}' wrote its exports; check its package.json exports";
        return $"{dependency.DisplayName} is {dependency.Description}: {state}.";
    }
}
