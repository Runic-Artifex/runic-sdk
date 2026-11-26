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
/// What doctor knows of the frontend's dependency graph without a build (devtools-library-dx §6): the frontend packages
/// that the page depends on, from <c>RunicViewsGetFrontendDependencies</c>, and the workspace facts of the graph that the
/// query's resolver wrote (<c>obj/runic/frontend-graph.json</c>, <c>rax.frontend-graph/1</c>).
/// </summary>
/// <param name="Dependencies">The packages, dependencies first; empty when there are none or the query failed.</param>
/// <param name="Member">Whether the graph lists the frontend as a member of its workspace; null without a graph.</param>
/// <param name="GraphRoot">The workspace root the resolver used, or null without a graph.</param>
/// <param name="Members">The workspace members' folders, from the graph.</param>
/// <param name="Error">The query's first error, such as <c>RUNICBRIDGE087: …</c>, or null when it ran.</param>
internal sealed partial record DoctorFrontendGraph(
    IReadOnlyList<FrontendDependency> Dependencies,
    bool? Member,
    string? GraphRoot,
    IReadOnlyList<string> Members,
    string? Error)
{
    /// <summary>
    /// Whether the packages come from MSBuild, which knows the .NET project that owns each one and whether its build is
    /// on. False when doctor ran the graph script itself, before the first restore.
    /// </summary>
    internal bool OwnersKnown { get; init; } = true;

    /// <summary>The code of <see cref="Error"/>, such as RUNICBRIDGE087, or null.</summary>
    internal string? ErrorCode => Error is { } error && error.IndexOf(':', StringComparison.Ordinal) is > 0 and var colon
        ? error[..colon]
        : null;

    /// <summary>
    /// Reads the graph with <c>RunicViewsGetFrontendDependencies</c>. When the query cannot run, as before the first
    /// restore (NETSDK1004), and the targets name their resolver (<paramref name="graphScript"/>), doctor runs the
    /// resolver itself, on <paramref name="scriptRuntime"/>, and it writes nothing.
    /// </summary>
    internal static async Task<DoctorFrontendGraph?> ReadAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        string buildConfiguration,
        string graphScript,
        string scriptRuntime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!File.Exists(Path.Combine(configuration.FrontendPackageDirectory, "package.json"))) return null;
        CommandResult result;
        try
        {
            result = await CommandRunner.RunAsync(dotnetHost, configuration.ProjectDirectory,
                FrontendDependencies.CreateQueryArguments(configuration, buildConfiguration), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DevDevelopmentException or IOException or System.ComponentModel.Win32Exception)
        {
            return new DoctorFrontendGraph([], null, null, [], error.Message);
        }
        string? graph = null;
        try
        {
            string path = FrontendDependencies.GraphPath(configuration);
            if (File.Exists(path)) graph = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        DoctorFrontendGraph read = Create(result.ExitCode, result.CombinedOutput, result.StandardOutput, graph, configuration.FrontendPackageDirectory);
        if (read.Error is null || read.ErrorCode is "RUNICBRIDGE087" or "RUNICBRIDGE089" || graphScript.Length == 0 || !File.Exists(graphScript))
        {
            return read;
        }
        string packageManager;
        try { packageManager = configuration.PackageManager.Name; }
        catch (DevDevelopmentException) { return read; }
        CommandResult resolved;
        try
        {
            resolved = await CommandRunner.RunAsync(scriptRuntime.Length != 0 ? scriptRuntime : packageManager == "bun" ? "bun" : "node",
                configuration.ProjectDirectory,
                [graphScript, "--frontend", configuration.FrontendPackageDirectory, "--root", configuration.JavaScriptWorkspaceRoot, "--package-manager", packageManager],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DevDevelopmentException or IOException or System.ComponentModel.Win32Exception)
        {
            return read;
        }
        return resolved.ExitCode == 0 && FromGraph(resolved.StandardOutput, configuration.FrontendPackageDirectory) is { } fromScript
            ? fromScript
            : read;
    }

    /// <summary>The graph that the resolver printed (rax.frontend-graph/1), with its packages; null when it is unreadable.</summary>
    internal static DoctorFrontendGraph? FromGraph(string graph, string frontend)
    {
        (bool? member, string? root, IReadOnlyList<string> members) = ReadWorkspace(graph, frontend);
        if (root is null) return null;
        var dependencies = new List<FrontendDependency>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(graph);
            if (document.RootElement.TryGetProperty("packages", out JsonElement packages) && packages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement package in packages.EnumerateArray())
                {
                    string Text(string name) => package.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : string.Empty;
                    string[] List(string name) => package.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
                        ? [.. value.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.String).Select(static item => item.GetString()!)]
                        : [];
                    if (Text("directory").Length == 0) continue;
                    dependencies.Add(new FrontendDependency(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Text("directory"))), Text("package"),
                        Text("kind") is { Length: > 0 } kind ? kind : "source", null, null, true, Text("build"), Text("watch"))
                    {
                        Exports = List("exports"),
                        Outputs = List("outputs"),
                    });
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return new DoctorFrontendGraph(dependencies, member, root, members, null) { OwnersKnown = false };
    }

    /// <summary>
    /// The graph from the query's exit code and output and the graph file. A failed query keeps the graph only when it
    /// failed after the resolver wrote it (RUNICBRIDGE087 and RUNICBRIDGE089); otherwise the file may be an older build's.
    /// </summary>
    internal static DoctorFrontendGraph Create(int exitCode, string combinedOutput, string targetResult, string? graph, string frontend)
    {
        ArgumentNullException.ThrowIfNull(combinedOutput);
        IReadOnlyList<FrontendDependency> dependencies = [];
        string? error = null;
        if (exitCode == 0)
        {
            try { dependencies = FrontendDependencies.Parse(targetResult, graph); }
            catch (JsonException parse) { error = $"RAPPDEV1003: {parse.Message}"; }
        }
        else
        {
            error = FirstError(combinedOutput) ?? $"MSBuild exited with code {exitCode}.";
        }
        bool graphCurrent = error is null || error.StartsWith("RUNICBRIDGE087", StringComparison.Ordinal) ||
            error.StartsWith("RUNICBRIDGE089", StringComparison.Ordinal);
        (bool? member, string? root, IReadOnlyList<string> members) = graphCurrent ? ReadWorkspace(graph, frontend) : (null, null, []);
        return new DoctorFrontendGraph(dependencies, member, root, members, error);
    }

    private static (bool? Member, string? Root, IReadOnlyList<string> Members) ReadWorkspace(string? graph, string frontend)
    {
        if (string.IsNullOrWhiteSpace(graph)) return (null, null, []);
        try
        {
            using JsonDocument document = JsonDocument.Parse(graph);
            JsonElement root = document.RootElement;
            // Another frontend's graph (an older configuration) says nothing about this one.
            if (!root.TryGetProperty("application", out JsonElement application) ||
                !application.TryGetProperty("directory", out JsonElement directory) || directory.ValueKind != JsonValueKind.String ||
                !SameFolder(directory.GetString()!, frontend))
            {
                return (null, null, []);
            }
            bool? member = root.TryGetProperty("member", out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
            string? workspaceRoot = root.TryGetProperty("workspaceRoot", out JsonElement rootValue) && rootValue.ValueKind == JsonValueKind.String
                ? rootValue.GetString()
                : null;
            var members = new List<string>();
            if (root.TryGetProperty("members", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.TryGetProperty("directory", out JsonElement folder) && folder.ValueKind == JsonValueKind.String)
                        members.Add(folder.GetString()!);
                }
            }
            return (member, workspaceRoot, members);
        }
        catch (JsonException)
        {
            return (null, null, []);
        }
    }

    internal static bool SameFolder(string left, string right)
    {
        try
        {
            return FrontendDependencies.PathComparer.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The first MSBuild error of the output as <c>CODE: message</c>, without the trailing <c>[project]</c>.</summary>
    internal static string? FirstError(string output)
    {
        Match match = ErrorLine().Match(output);
        return match.Success ? $"{match.Groups["code"].Value}: {match.Groups["message"].Value.Trim()}" : null;
    }

    [GeneratedRegex(@":\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>[^\r\n]*?)(?:\s+\[[^\]\r\n]+\])?\r?$", RegexOptions.Multiline)]
    private static partial Regex ErrorLine();
}

/// <summary>
/// The doctor checks of the frontend's JavaScript workspace and the packages that the page depends on
/// (devtools-library-dx §6, S6): <c>frontend-workspace</c>, <c>package-manager-agreement</c> and
/// <c>frontend-dependencies</c> (with <see cref="DoctorModuleChecks"/>' <c>client-modules</c> and <c>views-dedupe</c>), and the <c>windows-targeting</c> advice of split CI (D082 S4).
/// </summary>
internal static class DoctorWorkspaceChecks
{
    private static readonly (string File, string Manager)[] LockFiles =
    [
        ("bun.lock", "bun"), ("bun.lockb", "bun"), ("package-lock.json", "npm"), ("npm-shrinkwrap.json", "npm"), ("pnpm-lock.yaml", "pnpm"),
    ];

    private static bool HasFrontend(DoctorProjectConfiguration project) =>
        project.FrontendPackageDirectory.Length != 0 && File.Exists(Path.Combine(project.FrontendPackageDirectory, "package.json"));

    private static bool InWorkspace(DoctorProjectConfiguration project) =>
        !DoctorFrontendGraph.SameFolder(project.EffectiveWorkspaceRoot, project.FrontendPackageDirectory);

    internal static void Inspect(List<DoctorCheck> checks, DoctorProjectConfiguration project, IDoctorRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!HasFrontend(project)) return;
        checks.Add(CheckWorkspace(project));
        checks.Add(CheckPackageManagerAgreement(project));
        if (project.FrontendGraph is { } graph) checks.Add(CheckDependencies(graph));
        if (DoctorModuleChecks.CheckClientModules(project) is { } modules) checks.Add(modules);
        if (DoctorModuleChecks.CheckViewsDedupe(project) is { } dedupe) checks.Add(dedupe);
    }

    /// <summary>The lock files in <paramref name="folder"/>, with the manager each belongs to.</summary>
    internal static IReadOnlyList<(string Path, string Manager)> FindLockFiles(string folder) =>
        [.. LockFiles.Select(lockFile => (Path.Combine(folder, lockFile.File), lockFile.Manager)).Where(static found => File.Exists(found.Item1))];

    /// <summary>The nearest folder above <paramref name="folder"/> that declares a workspace, or null.</summary>
    private static string? WorkspaceAbove(string folder)
    {
        for (DirectoryInfo? parent = new DirectoryInfo(folder).Parent; parent is not null; parent = parent.Parent)
        {
            if (JavaScriptWorkspace.DeclaresWorkspace(parent.FullName)) return Path.TrimEndingDirectorySeparator(parent.FullName);
        }
        return null;
    }

    private static string MembersFile(string root) =>
        File.Exists(Path.Combine(root, "pnpm-workspace.yaml")) && !JavaScriptWorkspace.DeclaresPackageJsonWorkspaces(root)
            ? Path.Combine(root, "pnpm-workspace.yaml") + "'s packages"
            : "the workspaces of " + Path.Combine(root, "package.json");

    /// <summary>
    /// frontend-workspace: the workspace root that installs the frontend, and that the root lists it. A frontend with
    /// its own lock file below a workspace installs on its own, which warns.
    /// </summary>
    internal static DoctorCheck CheckWorkspace(DoctorProjectConfiguration project)
    {
        const string id = "frontend-workspace";
        string frontend = project.FrontendPackageDirectory;
        string root = project.EffectiveWorkspaceRoot;
        DoctorFrontendGraph? graph = project.FrontendGraph;
        if (!InWorkspace(project))
        {
            bool separate = project.WorkspaceRootSetting.Equals("none", StringComparison.OrdinalIgnoreCase);
            if (!separate && WorkspaceAbove(frontend) is { } above && FindLockFiles(frontend) is [var own, ..])
            {
                return Warn(id,
                    $"The frontend {frontend} has its own lock file {own.Path}, so the build installs it on its own, not with the JavaScript workspace at {above}.",
                    $"Delete {Path.GetFileName(own.Path)} to install the frontend with the workspace at {above}, or set RunicApplicationFrontendWorkspaceRoot=none to keep it on its own.");
            }
            return Pass(id, separate
                ? $"The frontend {frontend} is its own JavaScript workspace root (RunicApplicationFrontendWorkspaceRoot=none)."
                : $"The frontend {frontend} is its own JavaScript workspace root.");
        }
        if (!Directory.Exists(root))
        {
            return Fail(id, $"The JavaScript workspace root {root} that RunicApplicationFrontendWorkspaceRoot names does not exist.",
                "Set RunicApplicationFrontendWorkspaceRoot to the folder of the workspace's package.json, or to none.");
        }
        string manager = project.ResolvePackageManager().Manager.Name;
        if (graph?.Member == false || graph?.ErrorCode == "RUNICBRIDGE087")
        {
            return Fail(id,
                $"The frontend {frontend} is below the {manager} workspace at {root}, but {MembersFile(root)} do not include it, so the workspace's install would not install it (RUNICBRIDGE087).",
                $"Add '{Path.GetRelativePath(root, frontend).Replace('\\', '/')}' to {MembersFile(root)}, or set RunicApplicationFrontendWorkspaceRoot=none to install it on its own with its own lock file.");
        }
        if (graph?.Member == true)
            return Pass(id, $"The frontend {frontend} is a member of the {manager} workspace at {root}, which installs it.");
        return Warn(id,
            $"The frontend {frontend} belongs to the {manager} workspace at {root}, but doctor could not check that the workspace lists it" +
            (graph?.Error is { } error ? $": {error}" : "."),
            "Restore and build the project once ('dotnet build'), which resolves the workspace, and rerun doctor.");
    }

    /// <summary>
    /// package-manager-agreement: the workspace root's packageManager, its lock files, the project's
    /// RunicApplicationFrontendPackageManager and each member's packageManager name one package manager.
    /// </summary>
    internal static DoctorCheck CheckPackageManagerAgreement(DoctorProjectConfiguration project)
    {
        const string id = "package-manager-agreement";
        string root = project.EffectiveWorkspaceRoot;
        bool workspace = InWorkspace(project);
        (JavaScriptPackageManager resolved, string source) = project.ResolvePackageManager();
        // The workspace's own choice, without the project property.
        (JavaScriptPackageManager rootManager, string rootSource) = JavaScriptPackageManager.ResolveWithSource(
            root, workspace ? root : project.FrontendPackageDirectory);
        var failures = new List<(string Message, string Fix)>();
        var warnings = new List<(string Message, string Fix)>();
        string rootManifest = Path.Combine(root, "package.json");
        string? declared = PackageManagerOf(rootManifest);
        IReadOnlyList<(string Path, string Manager)> locks = FindLockFiles(root);
        string place = workspace ? $"the workspace root {root}" : $"the frontend {root}";
        if (declared is not null && locks.Count != 0 && !locks.Any(found => found.Manager == declared))
        {
            string other = locks[0].Manager;
            failures.Add((
                $"{rootManifest} declares {declared} (packageManager), but the lock file {locks[0].Path} is {other}'s",
                $"Run '{declared} install' in {root} and commit its lock file, then delete {Path.GetFileName(locks[0].Path)}; or set packageManager to {other}."));
        }
        else if (locks.Select(static found => found.Manager).Distinct().Count() > 1)
        {
            warnings.Add((
                $"{place} has lock files of several package managers ({string.Join(", ", locks.Select(static found => Path.GetFileName(found.Path)))}); the build uses {rootManager.Name}'s ({rootSource})",
                $"Delete the lock files of the package managers that {(workspace ? "the workspace" : "the frontend")} does not use."));
        }
        if (project.PackageManagerName.Length != 0 && !string.Equals(project.PackageManagerName.Trim(), rootManager.Name, StringComparison.OrdinalIgnoreCase) &&
            (declared is not null || locks.Count != 0))
        {
            warnings.Add((
                $"the project sets RunicApplicationFrontendPackageManager={project.PackageManagerName.Trim()}, but {place} uses {rootManager.Name} ({rootSource})",
                $"Remove RunicApplicationFrontendPackageManager, or make it {rootManager.Name}."));
        }
        var members = new List<string>();
        if (workspace)
        {
            members.Add(project.FrontendPackageDirectory);
            if (project.FrontendGraph is { } graph)
            {
                members.AddRange(graph.Members);
                members.AddRange(graph.Dependencies.Select(static dependency => dependency.Directory));
            }
            var seen = new HashSet<string>(FrontendDependencies.PathComparer);
            foreach (string member in members)
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(member));
                if (!seen.Add(full) || DoctorFrontendGraph.SameFolder(full, root)) continue;
                string manifest = Path.Combine(full, "package.json");
                if (PackageManagerOf(manifest) is { } own && own != resolved.Name)
                {
                    failures.Add((
                        $"{manifest} declares {own} (packageManager), but its workspace {root} uses {resolved.Name} ({source}), so the build installs and builds it with {resolved.Name} (RUNICBRIDGE089)",
                        $"Remove the packageManager of {manifest}, or make it match the workspace's."));
                }
                // The frontend's own lock file makes it its own root, which frontend-workspace reports.
                if (!DoctorFrontendGraph.SameFolder(full, project.FrontendPackageDirectory) && FindLockFiles(full) is [var memberLock, ..])
                {
                    warnings.Add((
                        $"the workspace member {full} has its own lock file {memberLock.Path}, which the workspace's install at {root} does not use",
                        $"Delete {memberLock.Path}; the workspace's {resolved.LockFileName} locks its dependencies."));
                }
            }
        }
        if (failures.Count != 0 || warnings.Count != 0)
        {
            IEnumerable<(string Message, string Fix)> problems = failures.Concat(warnings);
            string message = Sentence(string.Join("; ", problems.Select(static problem => problem.Message)));
            string fix = string.Join(" ", problems.Select(static problem => problem.Fix).Distinct());
            return failures.Count != 0 ? Fail(id, message, fix) : Warn(id, message, fix);
        }
        int count = members.Distinct(FrontendDependencies.PathComparer).Count();
        return Pass(id, workspace
            ? $"The workspace at {root} and its {count} member(s) that doctor read use {resolved.Name} ({source})."
            : $"The frontend uses {resolved.Name} ({source}).");
    }

    /// <summary>
    /// frontend-dependencies: every frontend package that the page depends on is built, or can be. A compiled package
    /// (with a build script) whose exports are missing warns while the build builds it, and fails when its build is
    /// off; a source package whose exports are missing fails, because nothing writes them.
    /// </summary>
    internal static DoctorCheck CheckDependencies(DoctorFrontendGraph graph)
    {
        const string id = "frontend-dependencies";
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Error is { } error && graph.Dependencies.Count == 0)
        {
            return Warn(id, $"Doctor could not read the frontend packages that the page depends on: {error}",
                graph.ErrorCode is "RUNICBRIDGE087" or "RUNICBRIDGE089"
                    ? "Fix the workspace problem that frontend-workspace or package-manager-agreement reports, and rerun doctor."
                    : "Restore the project ('dotnet restore') and rerun doctor; 'dotnet build' reports the same error.");
        }
        if (graph.Dependencies.Count == 0)
            return Pass(id, "The page depends on no library or workspace frontend package.");
        var failures = new List<(string Message, string Fix)>();
        var warnings = new List<(string Message, string Fix)>();
        var fine = new List<string>();
        foreach (FrontendDependency dependency in graph.Dependencies)
        {
            // Before the first restore doctor knows the packages but not the .NET projects that own them.
            string name = graph.OwnersKnown ? $"{dependency.DisplayName} ({dependency.Description})" : dependency.DisplayName;
            string? missing = dependency.MissingExport();
            string relative = missing is null ? string.Empty : $"{dependency.Label}/{Path.GetRelativePath(dependency.Directory, missing).Replace('\\', '/')}";
            if (dependency.Kind == "compiled")
            {
                if (missing is null) { fine.Add($"{dependency.DisplayName} (built)"); continue; }
                if (dependency.IsBuilt)
                {
                    warnings.Add(($"{name} is not built yet: {relative} is missing",
                        $"Run 'dotnet build' or 'dotnet runic dev', which build {dependency.DisplayName} with '{dependency.BuildCommand}'."));
                }
                else if (!dependency.BuildEnabled)
                {
                    failures.Add(($"{name} is not built ({relative} is missing), and its package build is off in {dependency.OwnerName ?? "its project"} (RunicApplicationFrontendBuildEnabled=false)",
                        $"Remove RunicApplicationFrontendBuildEnabled=false from {dependency.OwnerName ?? "the project"}, or run its build script in {dependency.Directory} before each build."));
                }
                else
                {
                    failures.Add(($"{name} is not built ({relative} is missing), and it has no build command",
                        $"Set RunicApplicationFrontendBuildCommand in {dependency.OwnerName ?? "its project"}, or give {dependency.Directory}/package.json a 'build' script."));
                }
            }
            else if (missing is not null)
            {
                failures.Add(($"{name} exports {relative}, which does not exist, and it has no build script that would write it",
                    $"Add a 'build' script to {Path.Combine(dependency.Directory, "package.json")} that writes {relative}, or make its exports name its sources."));
            }
            else
            {
                fine.Add($"{dependency.DisplayName} (source)");
            }
        }
        if (failures.Count != 0 || warnings.Count != 0)
        {
            IEnumerable<(string Message, string Fix)> problems = failures.Concat(warnings);
            string message = Sentence(string.Join("; ", problems.Select(static problem => problem.Message)));
            string fix = string.Join(" ", problems.Select(static problem => problem.Fix).Distinct());
            return failures.Count != 0 ? Fail(id, message, fix) : Warn(id, message, fix);
        }
        return Pass(id, $"The {graph.Dependencies.Count} frontend package(s) that the page depends on are ready: {string.Join(", ", fine)}.");
    }

    /// <summary>
    /// windows-targeting (D082 S4): a project of a Windows target framework, such as a WPF project on net10.0-windows,
    /// builds on Linux and macOS only with EnableWindowsTargeting=true, set before the restore; it runs only on Windows.
    /// </summary>
    internal static void CheckWindowsTargeting(List<DoctorCheck> checks, DoctorProjectConfiguration project, IDoctorRuntime runtime)
    {
        const string id = "windows-targeting";
        bool windowsTarget = project.TargetPlatformIdentifier.Equals("windows", StringComparison.OrdinalIgnoreCase) ||
            project.TargetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase);
        if (!windowsTarget || runtime.Platform.Os == "win") return;
        string os = runtime.Platform.Os switch { "osx" => "macOS", "linux" => "Linux", _ => "this operating system" };
        checks.Add(project.EnableWindowsTargeting
            ? Pass(id, $"{project.TargetFramework} is a Windows target framework, which EnableWindowsTargeting=true lets the .NET SDK build on {os}; the application runs only on Windows.")
            : Fail(id,
                $"{project.TargetFramework} is a Windows target framework, which the .NET SDK builds on {os} only with EnableWindowsTargeting=true; without it the restore and build fail with NETSDK1100.",
                "Add <EnableWindowsTargeting Condition=\"'$(EnableWindowsTargeting)' == '' and !$([MSBuild]::IsOSPlatform('Windows'))\">true</EnableWindowsTargeting> " +
                "to a PropertyGroup of the application's Directory.Build.props (or its project file), so it applies at restore time. The application still runs only on Windows."));
    }

    /// <summary>
    /// web-bundle (split CI F6, <c>doctor --bundle</c>): checks a web bundle offline as the publishing build would before
    /// it compares fingerprints: its manifest, each entry's folders and the hash of each page, and that it has an entry
    /// for <paramref name="assembly"/>. The View fingerprints need the build's contract IR, so only the publish checks them.
    /// </summary>
    internal static DoctorCheck CheckWebBundle(string bundle, string assembly)
    {
        const string id = "web-bundle";
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        (IReadOnlyList<WebBundleEntry> entries, IReadOnlyList<string> problems) = WebBundle.Read(bundle);
        var failures = new List<string>(problems);
        if (problems.Count == 0 && assembly.Length != 0 && !entries.Any(entry => entry.Assembly == assembly))
        {
            failures.Add($"it has no entry for {assembly}, only for {string.Join(", ", entries.Select(static entry => entry.Assembly))}, so its publish would fail with RUNICBRIDGE093");
        }
        if (failures.Count != 0)
        {
            return Fail(id, Sentence($"The web bundle {bundle} cannot be used: {string.Join("; ", failures)}"),
                "Write the bundle again with 'dotnet runic bundle' on the job that builds the frontends, from this commit and for this project or its solution, and download the whole artifact.");
        }
        return Pass(id,
            $"The web bundle {bundle} ({WebBundle.Format}) checks: {string.Join(", ", entries.Select(static entry => $"{entry.Assembly} ({entry.Kind}, {entry.Views} View(s){(entry.Sha256 is null ? string.Empty : $", {entry.Files} files")})"))}. " +
            "The publish compares its View fingerprints with this build's (RUNICBRIDGE094).");
    }

    /// <summary>The package manager a package.json's <c>packageManager</c> names (npm, pnpm or bun), or null.</summary>
    internal static string? PackageManagerOf(string manifest)
    {
        if (!File.Exists(manifest)) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("packageManager", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            string declaration = value.GetString()!;
            int separator = declaration.IndexOf('@', StringComparison.Ordinal);
            string name = (separator > 0 ? declaration[..separator] : declaration).Trim().ToLowerInvariant();
            return name.Length == 0 ? null : name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static DoctorCheck Pass(string name, string message) => new(DoctorStatus.Pass, name, message);
    private static DoctorCheck Warn(string name, string message, string remediation) => new(DoctorStatus.Warning, name, message, remediation);
    private static DoctorCheck Fail(string name, string message, string remediation) => new(DoctorStatus.Failure, name, message, remediation);
}
