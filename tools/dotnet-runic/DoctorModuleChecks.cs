using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Runic.Application.Tool;

/// <summary>A <c>RunicApplicationFrontendClientModule</c> item: a client library's assembly and the specifier the page imports.</summary>
internal sealed record DoctorClientModule(string Assembly, string Specifier);

/// <summary>
/// The doctor checks of how the page's imports resolve (devtools-library-dx §6, S6b): <c>client-modules</c>, that each
/// specifier the page imports from a library or workspace package resolves from the frontend, and <c>views-dedupe</c>,
/// that the Vite configuration dedupes <c>@runic-artifex/views</c> when a linked package imports it, so the page loads one
/// copy. They read package.json files, tsconfig and the Vite configuration's text; they run nothing.
/// </summary>
internal static partial class DoctorModuleChecks
{
    internal const string ViewsPackage = "@runic-artifex/views";

    // The conditions Vite resolves an export with for the page, in a development or a production build.
    private static readonly HashSet<string> Conditions = new(StringComparer.Ordinal)
    {
        "import", "module", "browser", "development", "production", "default",
    };

    private static readonly string[] ViteConfigurationFiles =
        ["vite.config.ts", "vite.config.mts", "vite.config.js", "vite.config.mjs", "vite.config.cts", "vite.config.cjs"];

    private static readonly string[] TsConfigFiles = ["tsconfig.json", "tsconfig.app.json"];

    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".mts", ".js", ".mjs", ".jsx", ".d.ts"];

    /// <summary>The package name of a bare specifier: <c>@acme/shared-ui</c> of <c>@acme/shared-ui/generated</c>; null for a relative or absolute one.</summary>
    internal static (string Name, string Subpath)? SplitSpecifier(string specifier)
    {
        ArgumentNullException.ThrowIfNull(specifier);
        if (specifier.Length == 0 || specifier[0] is '.' or '/' || Path.IsPathRooted(specifier) || specifier.Contains(':', StringComparison.Ordinal))
            return null;
        string[] parts = specifier.Split('/');
        int nameParts = specifier[0] == '@' ? 2 : 1;
        if (parts.Length < nameParts || parts.Take(nameParts).Any(static part => part.Length == 0)) return null;
        string name = string.Join('/', parts.Take(nameParts));
        string rest = string.Join('/', parts.Skip(nameParts));
        return (name, rest.Length == 0 ? "." : "./" + rest);
    }

    /// <summary>
    /// The folder of the package <paramref name="name"/> as Node and Vite find it from <paramref name="from"/>:
    /// <c>node_modules/&lt;name&gt;</c> in that folder or the nearest one above that has it; null when none has.
    /// </summary>
    internal static string? FindPackage(string from, string name)
    {
        for (DirectoryInfo? folder = new(from); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The path with every symbolic link and junction resolved, as Vite's <c>preserveSymlinks: false</c> sees it.</summary>
    internal static string RealPath(string path) => RealPath(Path.GetFullPath(path), 0);

    private static string RealPath(string full, int depth)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (depth > 32) continue;
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            string? target;
            try { target = info.LinkTarget; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { target = null; }
            if (target is null) continue;
            current = RealPath(Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root), depth + 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>How a specifier resolves.</summary>
    /// <param name="Status">Pass, Warning or Failure.</param>
    /// <param name="Message">What doctor found, without a trailing period.</param>
    /// <param name="Fix">The fix, or null for a pass.</param>
    internal sealed record Resolution(DoctorStatus Status, string Message, string? Fix);

    /// <summary>
    /// The target of <paramref name="subpath"/> (<c>.</c> or <c>./generated</c>) in a package.json's <c>exports</c>, with
    /// Vite's conditions; <c>Exported</c> is false when no key matches, and <c>Target</c> null when the key is blocked.
    /// </summary>
    internal static (bool Exported, string? Target) ResolveExport(JsonElement exports, string subpath)
    {
        bool subpathMap = exports.ValueKind == JsonValueKind.Object &&
            exports.EnumerateObject().Any(static property => property.Name.StartsWith('.'));
        if (!subpathMap) return subpath == "." ? (true, ResolveTarget(exports, null)) : (false, null);
        if (exports.TryGetProperty(subpath, out JsonElement exact)) return (true, ResolveTarget(exact, null));
        // A pattern such as "./*" or "./features/*.js": the longest prefix wins, as in Node.
        JsonProperty? best = null;
        string? star = null;
        foreach (JsonProperty property in exports.EnumerateObject())
        {
            int index = property.Name.IndexOf('*', StringComparison.Ordinal);
            if (index < 0) continue;
            string prefix = property.Name[..index];
            string suffix = property.Name[(index + 1)..];
            if (!subpath.StartsWith(prefix, StringComparison.Ordinal) || !subpath.EndsWith(suffix, StringComparison.Ordinal) ||
                subpath.Length < prefix.Length + suffix.Length)
            {
                continue;
            }
            if (best is { } current && current.Name.IndexOf('*', StringComparison.Ordinal) >= prefix.Length) continue;
            best = property;
            star = subpath[prefix.Length..^suffix.Length];
        }
        return best is { } match ? (true, ResolveTarget(match.Value, star)) : (false, null);
    }

    private static string? ResolveTarget(JsonElement target, string? star)
    {
        switch (target.ValueKind)
        {
            case JsonValueKind.String:
                string value = target.GetString()!;
                return star is null ? value : value.Replace("*", star, StringComparison.Ordinal);
            case JsonValueKind.Array:
                foreach (JsonElement item in target.EnumerateArray())
                {
                    if (ResolveTarget(item, star) is { } found) return found;
                }
                return null;
            case JsonValueKind.Object:
                foreach (JsonProperty condition in target.EnumerateObject())
                {
                    if (Conditions.Contains(condition.Name) && ResolveTarget(condition.Value, star) is { } found) return found;
                }
                return null;
            default:
                return null;
        }
    }

    private static JsonDocument? ReadJson(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a package.json lists <paramref name="name"/> in its dependencies, devDependencies or peerDependencies.</summary>
    internal static bool Declares(string manifest, string name)
    {
        using JsonDocument? document = ReadJson(manifest);
        if (document?.RootElement.ValueKind != JsonValueKind.Object) return false;
        foreach (string field in new[] { "dependencies", "devDependencies", "peerDependencies" })
        {
            if (document.RootElement.TryGetProperty(field, out JsonElement list) && list.ValueKind == JsonValueKind.Object &&
                list.TryGetProperty(name, out _))
            {
                return true;
            }
        }
        return false;
    }

    private static string Show(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative).Replace('\\', '/');
    }

    /// <summary>
    /// client-modules: each specifier the page imports from a library or workspace package resolves from the frontend,
    /// as Vite resolves it: every <c>RunicApplicationFrontendClientModule</c> specifier, to an exported file that exists,
    /// and every package of the graph that the frontend's package.json declares, to that package's folder. Null when the
    /// page imports none.
    /// </summary>
    internal static DoctorCheck? CheckClientModules(DoctorProjectConfiguration project)
    {
        const string id = "client-modules";
        ArgumentNullException.ThrowIfNull(project);
        string frontend = project.FrontendPackageDirectory;
        string manifest = Path.Combine(frontend, "package.json");
        IReadOnlyList<FrontendDependency> dependencies = project.FrontendGraph?.Dependencies ?? [];
        var specifiers = new List<(string Specifier, FrontendDependency? Dependency, string? Assembly)>();
        foreach (DoctorClientModule module in project.ClientModules)
        {
            string specifier = module.Specifier.Trim();
            if (specifier.Length == 0) continue; // RUNICBRIDGE082 at build time.
            FrontendDependency? dependency = dependencies.FirstOrDefault(found => found.Specifier == specifier) ??
                (SplitSpecifier(specifier) is { } split ? dependencies.FirstOrDefault(found => found.PackageName == split.Name) : null);
            specifiers.Add((specifier, dependency, module.Assembly));
        }
        foreach (FrontendDependency dependency in dependencies)
        {
            if (dependency.Specifier is { } specifier && !specifiers.Any(found => found.Specifier == specifier))
                specifiers.Add((specifier, dependency, dependency.OwnerName));
            if (dependency.PackageName.Length != 0 && Declares(manifest, dependency.PackageName) &&
                !specifiers.Any(found => found.Specifier == dependency.PackageName))
            {
                specifiers.Add((dependency.PackageName, dependency, null));
            }
        }
        if (specifiers.Count == 0) return null;

        string root = project.EffectiveWorkspaceRoot;
        bool installed = Directory.Exists(Path.Combine(frontend, "node_modules")) || Directory.Exists(Path.Combine(root, "node_modules"));
        string? viteConfiguration = FindViteConfiguration(project);
        string viteText = viteConfiguration is null ? string.Empty : ReadText(viteConfiguration);
        string manager;
        try { manager = project.ResolvePackageManager().Manager.Name; }
        catch (DevDevelopmentException) { manager = "npm"; }
        var results = new List<Resolution>();
        foreach ((string specifier, FrontendDependency? dependency, string? assembly) in specifiers)
        {
            results.Add(Resolve(project, specifier, dependency, assembly, installed, manager, viteConfiguration, viteText));
        }
        IEnumerable<Resolution> failures = results.Where(static result => result.Status == DoctorStatus.Failure);
        IEnumerable<Resolution> warnings = results.Where(static result => result.Status == DoctorStatus.Warning);
        if (failures.Any() || warnings.Any())
        {
            Resolution[] problems = [.. failures, .. warnings];
            string message = Sentence(string.Join("; ", problems.Select(static problem => problem.Message)));
            string fix = string.Join(" ", problems.Select(static problem => problem.Fix).OfType<string>().Distinct());
            return new DoctorCheck(failures.Any() ? DoctorStatus.Failure : DoctorStatus.Warning, id, message, fix);
        }
        return new DoctorCheck(DoctorStatus.Pass, id,
            $"The {results.Count} specifier(s) that the page imports from library and workspace packages resolve from the frontend: " +
            string.Join(", ", results.Select(static result => result.Message)) + ".");
    }

    private static Resolution Resolve(DoctorProjectConfiguration project, string specifier, FrontendDependency? dependency, string? assembly,
        bool installed, string manager, string? viteConfiguration, string viteText)
    {
        string frontend = project.FrontendPackageDirectory;
        string root = project.EffectiveWorkspaceRoot;
        string who = assembly is { Length: > 0 } ? $"{specifier} (the client module of {assembly})" : specifier;
        if (SplitSpecifier(specifier) is not { } split)
        {
            return new(DoctorStatus.Failure, $"{who} is not a package specifier (RUNICBRIDGE082)",
                "Set the RunicApplicationFrontendClientModule's Specifier to a package export, such as @acme/shared-ui/generated, or a tsconfig paths alias.");
        }
        bool bare = split.Subpath == "." && assembly is null;
        string? folder = FindPackage(frontend, split.Name);
        if (folder is null)
        {
            if (FindTsConfigPath(frontend, specifier) is { } alias)
            {
                bool viteAlias = viteText.Contains(split.Name, StringComparison.Ordinal) ||
                    viteText.Contains(split.Name.Replace("/", "\\/", StringComparison.Ordinal), StringComparison.Ordinal);
                if (alias.File is null)
                {
                    return new(DoctorStatus.Failure,
                        $"{who} resolves only through the paths of {alias.TsConfig}, whose target {alias.Target} does not exist",
                        $"Correct the paths entry of {alias.TsConfig}, or install the package: add \"{split.Name}\" to the dependencies of {Path.Combine(frontend, "package.json")} and run '{manager} install' in {root}.");
                }
                return viteAlias || viteConfiguration is null
                    ? new(DoctorStatus.Pass, $"{specifier} → {Show(root, alias.File)} (tsconfig paths and a Vite alias)", null)
                    : new(DoctorStatus.Warning,
                        $"{who} resolves through the paths of {alias.TsConfig} to {Show(root, alias.File)}, but {viteConfiguration} has no alias for {split.Name}, so Vite would not resolve it",
                        $"Add a resolve.alias for {split.Name} to {viteConfiguration} that matches the tsconfig paths, or install the package in the frontend.");
            }
            string declaration = manager == "npm" ? "\"*\"" : "\"workspace:*\"";
            if (!installed)
            {
                return new(DoctorStatus.Warning,
                    $"{who} does not resolve yet: the frontend's dependencies are not installed (no node_modules in {frontend} or {root})",
                    $"Run 'dotnet build' or 'dotnet runic dev', which install them, or '{manager} install' in {root}.");
            }
            return Declares(Path.Combine(frontend, "package.json"), split.Name)
                ? new(DoctorStatus.Failure,
                    $"{who} does not resolve from {frontend}: {frontend}/package.json declares {split.Name}, but no node_modules folder there or above has it",
                    $"Run '{manager} install' in {root}, which links {split.Name}.")
                : new(DoctorStatus.Failure,
                    $"{who} does not resolve from {frontend}: no node_modules folder there or above has {split.Name}, and the frontend's package.json does not declare it",
                    dependency is not null
                        ? $"Add \"{split.Name}\": {declaration} to the dependencies of {Path.Combine(frontend, "package.json")} and run '{manager} install' in {root}."
                        : $"Add {split.Name} to the dependencies of {Path.Combine(frontend, "package.json")} and run '{manager} install' in {root}, or map the specifier with tsconfig paths and a matching Vite resolve.alias.");
        }
        string real = RealPath(folder);
        if (dependency is not null && !DoctorFrontendGraph.SameFolder(real, RealPath(dependency.Directory)))
        {
            return new(DoctorStatus.Warning,
                $"{who} resolves to {real}, not to {dependency.DisplayName} at {dependency.Directory}, which {(dependency.OwnerName is { } owner ? $"{owner} builds" : "the workspace holds")}",
                $"Declare \"{split.Name}\": {(manager == "npm" ? "\"*\"" : "\"workspace:*\"")} in {Path.Combine(frontend, "package.json")} and run '{manager} install' in {root}, so the frontend links the workspace's package.");
        }
        if (bare) return new(DoctorStatus.Pass, $"{specifier} → {Show(root, real)}", null);

        using JsonDocument? package = ReadJson(Path.Combine(folder, "package.json"));
        string packageManifest = Path.Combine(real, "package.json");
        string? target;
        if (package?.RootElement.ValueKind == JsonValueKind.Object && package.RootElement.TryGetProperty("exports", out JsonElement exports))
        {
            (bool exported, target) = ResolveExport(exports, split.Subpath);
            if (!exported || target is null)
            {
                return new(DoctorStatus.Failure,
                    $"{who}: {packageManifest} {(exported ? "blocks" : "exports no")} '{split.Subpath}' for the page, so Vite cannot import it",
                    $"Add \"{split.Subpath}\" to the exports of {packageManifest}, pointing at the generated runic-clients file, or set the Specifier to a subpath that it exports.");
            }
            string file = Path.GetFullPath(target, real);
            if (File.Exists(file)) return new(DoctorStatus.Pass, $"{specifier} → {Show(root, file)}", null);
            return Missing(who, dependency, packageManifest, target);
        }
        // No exports: Node's legacy resolution of a subpath (with extensions) or of main/module.
        string start = split.Subpath == "."
            ? (package?.RootElement.ValueKind == JsonValueKind.Object && TextOf(package.RootElement, "module") is { } module ? module
                : package?.RootElement.ValueKind == JsonValueKind.Object && TextOf(package.RootElement, "main") is { } main ? main
                : "./index.js")
            : split.Subpath;
        if (ProbeFile(Path.GetFullPath(start, real)) is { } found) return new(DoctorStatus.Pass, $"{specifier} → {Show(root, found)}", null);
        return Missing(who, dependency, packageManifest, start);
    }

    private static Resolution Missing(string who, FrontendDependency? dependency, string manifest, string target)
    {
        if (dependency is { IsBuilt: true })
        {
            return new(DoctorStatus.Warning, $"{who} resolves to {target}, which {dependency.DisplayName}'s build has not written yet",
                $"Run 'dotnet build' or 'dotnet runic dev', which build {dependency.DisplayName} with '{dependency.BuildCommand}'.");
        }
        return new(DoctorStatus.Failure, $"{who} resolves through {manifest} to {target}, which does not exist",
            $"Make {manifest} point at a file that exists, or give the package a build script that writes {target}.");
    }

    private static string? TextOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string? ProbeFile(string path)
    {
        if (File.Exists(path)) return path;
        foreach (string extension in SourceExtensions)
        {
            if (File.Exists(path + extension)) return path + extension;
        }
        foreach (string extension in SourceExtensions)
        {
            string index = Path.Combine(path, "index" + extension);
            if (File.Exists(index)) return index;
        }
        return null;
    }

    /// <summary>The frontend tsconfig whose <c>compilerOptions.paths</c> maps <paramref name="specifier"/>, its target and the file it finds (null when none exists).</summary>
    internal static (string TsConfig, string Target, string? File)? FindTsConfigPath(string frontend, string specifier)
    {
        foreach (string name in TsConfigFiles)
        {
            string tsconfig = Path.Combine(frontend, name);
            using JsonDocument? document = ReadJson(tsconfig);
            if (document?.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("compilerOptions", out JsonElement options) || options.ValueKind != JsonValueKind.Object ||
                !options.TryGetProperty("paths", out JsonElement paths) || paths.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string baseDirectory = TextOf(options, "baseUrl") is { } baseUrl ? Path.GetFullPath(baseUrl, frontend) : frontend;
            foreach (JsonProperty entry in paths.EnumerateObject())
            {
                string? star = null;
                int index = entry.Name.IndexOf('*', StringComparison.Ordinal);
                if (index < 0 ? entry.Name != specifier
                    : !specifier.StartsWith(entry.Name[..index], StringComparison.Ordinal) || !specifier.EndsWith(entry.Name[(index + 1)..], StringComparison.Ordinal))
                {
                    continue;
                }
                if (index >= 0) star = specifier[index..^(entry.Name.Length - index - 1)];
                if (entry.Value.ValueKind != JsonValueKind.Array) continue;
                string? first = null;
                foreach (JsonElement target in entry.Value.EnumerateArray())
                {
                    if (target.ValueKind != JsonValueKind.String) continue;
                    string mapped = star is null ? target.GetString()! : target.GetString()!.Replace("*", star, StringComparison.Ordinal);
                    first ??= mapped;
                    if (ProbeFile(Path.GetFullPath(mapped, baseDirectory)) is { } file) return (tsconfig, mapped, file);
                }
                if (first is not null) return (tsconfig, first, null);
            }
        }
        return null;
    }

    private static string? FindViteConfiguration(DoctorProjectConfiguration project)
    {
        if (project.ViteConfigurationPath.Length != 0 && File.Exists(project.ViteConfigurationPath)) return project.ViteConfigurationPath;
        return ViteConfigurationFiles.Select(file => Path.Combine(project.FrontendPackageDirectory, file)).FirstOrDefault(File.Exists);
    }

    private static string ReadText(string file)
    {
        try { return File.ReadAllText(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return string.Empty; }
    }

    /// <summary>Whether a Vite configuration's text lists @runic-artifex/views in <c>resolve.dedupe</c>.</summary>
    internal static bool DedupesViews(string configuration) => DedupeList().IsMatch(configuration);

    [GeneratedRegex("""dedupe\s*:\s*\[[^\]]*["'`]@runic-artifex/views["'`]""")]
    private static partial Regex DedupeList();

    /// <summary>
    /// views-dedupe: a package that the page depends on is linked into the frontend (a workspace or folder package) and
    /// imports @runic-artifex/views. Vite resolves the package's imports from its real folder, which can hold its own copy
    /// of the runtime, so the page would load two; <c>resolve.dedupe</c> makes Vite take the frontend's. Null when no
    /// linked package imports the runtime, or the frontend has no Vite configuration.
    /// </summary>
    internal static DoctorCheck? CheckViewsDedupe(DoctorProjectConfiguration project)
    {
        const string id = "views-dedupe";
        ArgumentNullException.ThrowIfNull(project);
        FrontendDependency[] linked = [.. (project.FrontendGraph?.Dependencies ?? [])
            .Where(static dependency => Declares(Path.Combine(dependency.Directory, "package.json"), ViewsPackage))];
        if (linked.Length == 0 || FindViteConfiguration(project) is not { } configuration) return null;
        string names = string.Join(", ", linked.Select(static dependency => dependency.DisplayName));
        if (DedupesViews(ReadText(configuration)))
        {
            return new DoctorCheck(DoctorStatus.Pass, id,
                $"{configuration} dedupes {ViewsPackage}, so the page loads one copy for the linked package(s) {names}.");
        }
        string? pageCopy = FindPackage(project.FrontendPackageDirectory, ViewsPackage) is { } page ? RealPath(page) : null;
        var copies = new List<string>();
        foreach (FrontendDependency dependency in linked)
        {
            if (FindPackage(RealPath(dependency.Directory), ViewsPackage) is { } own && RealPath(own) is var real &&
                (pageCopy is null || !DoctorFrontendGraph.SameFolder(real, pageCopy)))
            {
                copies.Add($"{dependency.DisplayName} resolves {real}");
            }
        }
        string fix = $"Add resolve: {{ dedupe: [\"{ViewsPackage}\"] }} to {configuration}, as in export default defineConfig({{ resolve: {{ dedupe: [\"{ViewsPackage}\"] }} }}).";
        return copies.Count != 0
            ? new DoctorCheck(DoctorStatus.Warning, id,
                $"The page would load two copies of {ViewsPackage}: the frontend resolves {pageCopy ?? "none"}, and {string.Join(", ", copies)}; {configuration} does not dedupe it.",
                fix)
            : new DoctorCheck(DoctorStatus.Warning, id,
                $"The linked package(s) {names} import {ViewsPackage}, which Vite resolves from each package's own folder, and {configuration} does not dedupe it, so an install that gives a package its own copy loads two copies on the page.",
                fix);
    }

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";
}
