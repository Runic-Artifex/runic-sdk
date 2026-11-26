using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The web bundle (<c>rax.web-bundle/1</c>, specs/application/web-bundle.md, D082): what a build that has Node.js or
/// Bun writes so that a build on another machine, such as a Windows release job, takes the built frontends without
/// building them. The Views targets run these modes with a request file of <c>key=value</c> lines:
/// <list type="bullet">
/// <item><c>--web-bundle-write</c> (<c>RunicApplicationFrontendBundleDirectory</c>): copies one project's frontend
/// output (<c>www/</c>) and generated clients into <c>projects/&lt;assembly&gt;/</c>, writes its fragment
/// <c>projects/&lt;assembly&gt;.json</c> and, under a lock, <c>web-bundle.json</c> from every fragment.</item>
/// <item><c>--web-bundle-check</c> (<c>RunicApplicationFrontendPrebuiltDirectory</c>): finds the project's entry,
/// checks that its <c>www/</c> is the one written, and compares its Views' fingerprints with the contract IR that the
/// generator has just written. A changed, added or removed View is RUNICBRIDGE094.</item>
/// </list>
/// Both run on .NET alone, so the build that consumes a bundle needs neither Node.js nor Bun.
/// </summary>
internal static class WebBundle
{
    internal const string Format = "rax.web-bundle/1";
    internal const string ManifestName = "web-bundle.json";
    internal const string FingerprintFormat = "runic.views.fingerprint/1";
    private const string ProjectsFolder = "projects";
    private const string ViewsCheckProperty = "RunicApplicationFrontendPrebuiltCheck";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // The package managers' lock files.
    private static readonly string[] LockFiles = ["pnpm-lock.yaml", "bun.lock", "bun.lockb", "package-lock.json", "npm-shrinkwrap.json"];

    /// <summary>The request file: one <c>key=value</c> per line, as the targets write it.</summary>
    private static Dictionary<string, string> ReadRequest(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0) values[line[..separator]] = line[(separator + 1)..].Trim();
        }
        return values;
    }

    private static string Value(Dictionary<string, string> request, string key) => request.TryGetValue(key, out var value) ? value : "";

    private static string Required(Dictionary<string, string> request, string key) =>
        Value(request, key) is { Length: > 0 } value ? value : throw new ArgumentException($"The web bundle request has no {key}.");

    // ---------------------------------------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------------------------------------

    internal static void Write(string requestPath)
    {
        var request = ReadRequest(requestPath);
        var bundle = Path.GetFullPath(Required(request, "bundle"));
        var assembly = Required(request, "assembly");
        var kind = Required(request, "kind");
        var projectFile = Path.GetFullPath(Required(request, "project"));
        var ir = Required(request, "ir");
        var source = SourceRoot(Path.GetDirectoryName(projectFile)!);
        string Relative(string path) => source.Root is { } root ? PortableRelative(root, path) : Path.GetFileName(path);

        var entryFolder = Path.Combine(bundle, ProjectsFolder, assembly);
        // The entry is this build's, whole: what an earlier write left there goes.
        if (Directory.Exists(entryFolder)) Directory.Delete(entryFolder, recursive: true);
        Directory.CreateDirectory(entryFolder);

        var entry = new JsonObject
        {
            ["assembly"] = assembly,
            ["project"] = Relative(projectFile),
            ["kind"] = kind,
            ["configuration"] = Value(request, "configuration"),
            ["targetFramework"] = Value(request, "targetFramework"),
        };
        if (Value(request, "host") is { Length: > 0 } host) entry["host"] = host;
        if (Value(request, "nodeEnv") is { Length: > 0 } nodeEnv) entry["nodeEnv"] = nodeEnv;

        // The page: the frontend build's output, as build and publish copy it to www/. A client library's frontend
        // is a package that applications bundle, so it has none.
        JsonObject? files = null;
        if (kind != "clientLibrary")
        {
            var output = Required(request, "www");
            var copied = CopyTree(output, Path.Combine(entryFolder, "www"));
            if (copied.Count == 0)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.WebBundleWrite,
                    $"{assembly}'s frontend build wrote no files to {Path.GetFullPath(output)}, so the web bundle would give it an empty www/. Check RunicApplicationFrontendOutputDirectory and the frontend's build script.");
            entry["www"] = $"{ProjectsFolder}/{assembly}/www";
            files = FilesSummary(copied);
        }

        // The generated clients, which a job that tests the frontend alone copies into the frontend's generated folder.
        var clientsRoot = Value(request, "clients");
        var clientsRecord = Value(request, "clientsRecord");
        var clientFiles = new List<(string Source, string Path)>();
        if (clientsRecord.Length > 0 && File.Exists(clientsRecord))
        {
            foreach (var line in File.ReadAllLines(clientsRecord))
            {
                // "<sha256> <path>", as the emitter's launcher writes runic-bridge/typescript.outputs.
                if (line.Length <= 65 || !File.Exists(line[65..])) continue;
                var file = Path.GetFullPath(line[65..]);
                var relative = clientsRoot.Length > 0 ? Path.GetRelativePath(clientsRoot, file) : Path.GetFileName(file);
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) relative = Path.GetFileName(file);
                clientFiles.Add((file, relative.Replace('\\', '/')));
            }
        }
        foreach (var (file, relative) in clientFiles)
        {
            var target = Path.Combine(entryFolder, "clients", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        if (clientFiles.Count > 0) entry["clients"] = $"{ProjectsFolder}/{assembly}/clients";

        entry["contracts"] = Contracts(ir);

        var frontend = Value(request, "frontend");
        if (frontend.Length > 0 && Directory.Exists(frontend))
            entry["frontend"] = Frontend(request, frontend, Relative);

        if (files is not null) entry["files"] = files;

        var fragment = Path.Combine(bundle, ProjectsFolder, assembly + ".json");
        WriteAtomically(fragment, entry.ToJsonString(JsonOptions) + "\n");
        WriteManifest(bundle, request, source);
        Console.WriteLine($"Web bundle: {assembly} -> {entryFolder}{(files is null ? "" : $" ({files["count"]} files)")}, {((JsonArray)entry["contracts"]!["views"]!).Count} Views.");
    }

    private static JsonObject Contracts(string irPath)
    {
        var (unit, views) = ReadContractIr(irPath);
        var array = new JsonArray();
        foreach (var (id, fingerprint) in views.OrderBy(view => view.Key, StringComparer.Ordinal))
            array.Add(new JsonObject { ["id"] = id, ["fingerprint"] = fingerprint });
        return new JsonObject
        {
            ["unit"] = unit,
            ["fingerprintFormat"] = FingerprintFormat,
            ["irSha256"] = Sha256(File.ReadAllBytes(irPath)),
            ["views"] = array,
        };
    }

    /// <summary>The unit and every View's <c>{id: fingerprint}</c> of a <c>runic.views.contract-ir/1</c> document.</summary>
    private static (string Unit, Dictionary<string, string> Views) ReadContractIr(string path)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"The contract IR {path} does not exist; the Bridge generator writes it in every build that generates Views.");
        var document = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new ArgumentException($"{path} is not a JSON object.");
        if ((string?)document["format"] != ContractIr.Format)
            throw new ArgumentException($"{path} has the format '{(string?)document["format"]}', not {ContractIr.Format}.");
        var views = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in document["views"]?.AsArray() ?? [])
            if ((string?)view?["id"] is { } id && (string?)view["fingerprint"] is { } fingerprint) views[id] = fingerprint;
        return ((string?)document["producer"]?["unit"] ?? "", views);
    }

    private static JsonObject Frontend(Dictionary<string, string> request, string frontend, Func<string, string> relative)
    {
        var directory = Path.GetFullPath(frontend);
        var installRoot = Value(request, "installDirectory") is { Length: > 0 } install ? Path.GetFullPath(install) : directory;
        var result = new JsonObject { ["directory"] = relative(directory) };
        if (Value(request, "packageManager") is { Length: > 0 } manager) result["packageManager"] = manager;
        foreach (var file in LockFiles)
        {
            var lockFile = Path.Combine(installRoot, file);
            if (!File.Exists(lockFile)) continue;
            result["lockFile"] = relative(lockFile);
            result["lockSha256"] = Sha256(File.ReadAllBytes(lockFile));
            break;
        }
        // The @runic-artifex/views that the page bundled: the frontend's installed copy, found as Node.js resolves it.
        for (var folder = directory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var manifest = Path.Combine(folder, "node_modules", "@runic-artifex", "views", "package.json");
            if (File.Exists(manifest))
            {
                var version = (string?)JsonNode.Parse(File.ReadAllText(manifest))?["version"];
                if (version is not null) result["runtimePackage"] = new JsonObject { ["name"] = "@runic-artifex/views", ["version"] = version };
                break;
            }
        }
        if (Value(request, "buildCommand") is { Length: > 0 } command) result["buildCommand"] = command;
        return result;
    }

    /// <summary>Rewrites <c>web-bundle.json</c> from every fragment, under a lock, so that parallel projects add theirs.</summary>
    private static void WriteManifest(string bundle, Dictionary<string, string> request, (string? Root, string? Commit, bool? Dirty) source)
    {
        // The lock lives in the temporary folder, keyed by the bundle's path, so the bundle holds only its files.
        var key = Sha256(Encoding.UTF8.GetBytes(OperatingSystem.IsLinux() ? bundle : bundle.ToUpperInvariant()))[..16];
        using var held = Lock(Path.Combine(Path.GetTempPath(), $"runic-web-bundle-{key}.lock"));
        var projects = new JsonArray();
        foreach (var fragment in Directory.GetFiles(Path.Combine(bundle, ProjectsFolder), "*.json").Order(StringComparer.Ordinal))
        {
            var entry = JsonNode.Parse(File.ReadAllText(fragment))
                ?? throw new ArgumentException($"The web bundle fragment {fragment} is empty.");
            projects.Add(entry);
        }
        var createdBy = new JsonObject { ["os"] = RuntimeInformation.RuntimeIdentifier };
        if (Value(request, "dotnetSdk") is { Length: > 0 } sdk) createdBy["dotnet"] = sdk;
        // The Node.js of the build that wrote the bundle, when it has one (a Bun-only build has none).
        if (Run("node", bundle, "--version") is { Length: > 0 } node) createdBy["node"] = node.TrimStart('v');
        var manifest = new JsonObject
        {
            ["format"] = Format,
            ["producer"] = new JsonObject { ["name"] = "Runic.Application.Views", ["version"] = ProducerVersion() },
            ["createdBy"] = createdBy,
        };
        if (source.Commit is not null)
        {
            var sourceObject = new JsonObject { ["commit"] = source.Commit };
            if (source.Dirty is { } dirty) sourceObject["dirty"] = dirty;
            manifest["source"] = sourceObject;
        }
        manifest["projects"] = projects;
        WriteAtomically(Path.Combine(bundle, ManifestName), manifest.ToJsonString(JsonOptions) + "\n");
    }

    private static string ProducerVersion()
    {
        var version = typeof(WebBundle).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        // The informational version carries the commit after '+'; the manifest records it in source.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Checking
    // ---------------------------------------------------------------------------------------------------------------

    internal static void Check(string requestPath)
    {
        var request = ReadRequest(requestPath);
        var bundle = Path.GetFullPath(Required(request, "bundle"));
        var assembly = Required(request, "assembly");
        var ir = Required(request, "ir");
        var mode = Value(request, "check");
        if (mode.Length == 0) mode = "error";
        if (!mode.Equals("error", StringComparison.OrdinalIgnoreCase) && !mode.Equals("warning", StringComparison.OrdinalIgnoreCase))
            throw Unusable($"{ViewsCheckProperty} is '{mode}', which is not error or warning. Set error (the default) or warning.");
        var severity = mode.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "error";

        var manifestPath = Path.Combine(bundle, ManifestName);
        if (!File.Exists(manifestPath))
            throw Unusable($"RunicApplicationFrontendPrebuiltDirectory is {bundle}, which has no {ManifestName}. Point it at a web bundle that a build with RunicApplicationFrontendBundleDirectory wrote, such as a downloaded CI artifact.");
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new JsonException("not an object");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw Unusable($"{manifestPath} is not a valid web bundle manifest: {error.Message}");
        }
        if ((string?)manifest["format"] != Format)
            throw Unusable($"{manifestPath} has the format '{(string?)manifest["format"]}', which this release does not read; it reads {Format}. Write the bundle with the same Runic release as this build.");

        var entries = manifest["projects"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
        var entry = entries.FirstOrDefault(candidate => (string?)candidate["assembly"] == assembly);
        if (entry is null)
            throw Unusable($"The web bundle in {bundle} has no entry for {assembly}, which declares Runic Views; it has {(entries.Length == 0 ? "none" : string.Join(", ", entries.Select(candidate => (string?)candidate["assembly"])))}. Write the bundle with a build of the solution or project that builds {assembly}.");

        // The page: the files that the writer recorded, so that a truncated or edited download does not publish.
        if ((string?)entry["www"] is { } www)
        {
            var folder = Path.GetFullPath(Path.Combine(bundle, www));
            var expected = entry["files"]?.AsObject();
            var actual = FilesSummary(ListTree(folder));
            if (!Directory.Exists(folder) || expected is null
                || (string?)expected["sha256"] != (string?)actual["sha256"] || (int?)expected["count"] != (int?)actual["count"])
                throw Unusable($"The page of {assembly} in the web bundle, {folder}, is not the one the bundle recorded: {(Directory.Exists(folder) ? $"{actual["count"]} files with SHA-256 {actual["sha256"]}" : "the folder is missing")}, but web-bundle.json lists {expected?["count"]} files with SHA-256 {expected?["sha256"]}. Download the whole artifact again, or write the bundle again.");
        }
        else if (Required(request, "kind") != "clientLibrary")
        {
            throw Unusable($"The web bundle's entry for {assembly} has no www/, but {assembly} copies a frontend. Write the bundle with a build in which {assembly}'s frontend build is on.");
        }

        var problems = 0;
        // The Views: the contract IR that this build's generator has just written against the bundle's.
        var (_, built) = ReadContractIr(ir);
        var contracts = entry["contracts"]?.AsObject();
        var format = (string?)contracts?["fingerprintFormat"];
        var bundled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in contracts?["views"]?.AsArray() ?? [])
            if ((string?)view?["id"] is { } id && (string?)view["fingerprint"] is { } fingerprint) bundled[id] = fingerprint;
        var advice = severity == "error"
            ? $"Build the bundle from the same commit, or set {ViewsCheckProperty}=warning to publish anyway."
            : $"Build the bundle from the same commit; {ViewsCheckProperty}=warning publishes it anyway.";
        if (format != FingerprintFormat)
        {
            Report(severity, BridgeDiagnosticCodes.WebBundleContract,
                $"The prebuilt frontend of {assembly} in {bundle} records fingerprints of the format '{format}', and this build computes {FingerprintFormat}, so its Views cannot be compared. {advice}");
            problems++;
        }
        else
        {
            foreach (var (id, fingerprint) in built.OrderBy(view => view.Key, StringComparer.Ordinal))
            {
                if (!bundled.TryGetValue(id, out var other))
                {
                    Report(severity, BridgeDiagnosticCodes.WebBundleContract,
                        $"The prebuilt frontend in {bundle} was built from other ViewModels: {id} is {fingerprint} in this build and missing from the bundle's {assembly}. {advice}");
                    problems++;
                }
                else if (!string.Equals(fingerprint, other, StringComparison.OrdinalIgnoreCase))
                {
                    Report(severity, BridgeDiagnosticCodes.WebBundleContract,
                        $"The prebuilt frontend in {bundle} was built from other ViewModels: {id} is {fingerprint} in this build and {other} in the bundle. {advice}");
                    problems++;
                }
            }
            foreach (var (id, fingerprint) in bundled.Where(view => !built.ContainsKey(view.Key)).OrderBy(view => view.Key, StringComparer.Ordinal))
            {
                Report(severity, BridgeDiagnosticCodes.WebBundleContract,
                    $"The prebuilt frontend in {bundle} was built from other ViewModels: {id} is {fingerprint} in the bundle's {assembly} and not in this build. {advice}");
                problems++;
            }
        }

        // The host the page was built for (RUNIC_APPLICATION_HOST).
        var host = Value(request, "host");
        var bundledHost = (string?)entry["host"] ?? "";
        if (!string.Equals(host, bundledHost, StringComparison.OrdinalIgnoreCase))
        {
            Report(severity, BridgeDiagnosticCodes.WebBundleHost,
                $"The prebuilt frontend of {assembly} in {bundle} was built for the host '{(bundledHost.Length == 0 ? "none" : bundledHost)}' (RUNIC_APPLICATION_HOST), but this build runs it on '{(host.Length == 0 ? "none" : host)}'. Write the bundle with the same RunicApplicationHost{(severity == "error" ? $", or set {ViewsCheckProperty}=warning to publish anyway" : "")}.");
            problems++;
        }

        // An optimized build that takes a development page. A client library has no page of its own.
        var nodeEnv = (string?)entry["nodeEnv"] ?? "";
        if (entry["www"] is not null && Value(request, "optimize").Equals("true", StringComparison.OrdinalIgnoreCase) && nodeEnv != "production")
            Diagnostics.WarnAt(BridgeDiagnosticCodes.WebBundleDevelopment, Required(request, "project"),
                $"The prebuilt frontend of {assembly} in {bundle} is a development build (NODE_ENV {(nodeEnv.Length == 0 ? "unset" : $"'{nodeEnv}'")}, configuration {(string?)entry["configuration"]}), and this build is optimized ({Value(request, "configuration")}). Write the bundle with -c Release, as the publish runs.");

        if (problems == 0)
            Console.WriteLine($"Web bundle: {assembly} takes its frontend from {bundle} ({built.Count} {(built.Count == 1 ? "View matches" : "Views match")}).");
    }

    private static BridgeDiagnosticException Unusable(string message) => new(BridgeDiagnosticCodes.WebBundleInput, message);

    private static void Report(string severity, string code, string message)
    {
        if (severity == "error") Environment.ExitCode = 1;
        Console.Error.WriteLine($"{severity} {code}: {Diagnostics.WithHelpLink(code, message)}");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Files
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Every file below <paramref name="folder"/> with its path relative to it (with /) and its SHA-256.</summary>
    private static List<(string Path, string Sha256)> ListTree(string folder)
    {
        var files = new List<(string, string)>();
        if (!Directory.Exists(folder)) return files;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            files.Add((Path.GetRelativePath(folder, file).Replace('\\', '/'), Sha256(File.ReadAllBytes(file))));
        return files;
    }

    private static List<(string Path, string Sha256)> CopyTree(string source, string target)
    {
        var files = ListTree(source);
        foreach (var (relative, _) in files)
        {
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination, overwrite: true);
        }
        return files;
    }

    /// <summary>
    /// <c>{count, sha256}</c>: the SHA-256 of the lines <c>&lt;sha256&gt;  &lt;path&gt;\n</c> (sha256sum's format)
    /// sorted by path in ordinal order.
    /// </summary>
    private static JsonObject FilesSummary(List<(string Path, string Sha256)> files)
    {
        var text = new StringBuilder();
        foreach (var (path, sha256) in files.OrderBy(file => file.Path, StringComparer.Ordinal)) text.Append(sha256).Append("  ").Append(path).Append('\n');
        return new JsonObject { ["count"] = files.Count, ["sha256"] = Sha256(Encoding.UTF8.GetBytes(text.ToString())) };
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string PortableRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." ? Path.GetFileName(path) : relative;
    }

    private static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>An exclusive lock on <paramref name="path"/>, waiting up to five minutes for another build's write.</summary>
    private static FileStream Lock(string path)
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>
    /// The Git checkout of the project, which the manifest's paths are relative to, with its commit and whether tracked
    /// files differ from it. Without Git, paths are file names and no source is recorded.
    /// </summary>
    private static (string? Root, string? Commit, bool? Dirty) SourceRoot(string directory)
    {
        var root = Run("git", directory, "rev-parse", "--show-toplevel");
        if (root is null) return (null, null, null);
        var commit = Run("git", directory, "rev-parse", "HEAD");
        var status = Run("git", directory, "status", "--porcelain", "--untracked-files=no");
        return (Path.GetFullPath(root), commit, status is null ? null : status.Length > 0);
    }

    /// <summary>A command's trimmed standard output, or null when it does not run or fails.</summary>
    private static string? Run(string command, string directory, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(command) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0) return null;
            return output.Trim();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
