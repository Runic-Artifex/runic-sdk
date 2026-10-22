using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed record SizeOptions(string? Project, string Runtime,
    string Configuration, string Report, bool Aot, string Verify, IReadOnlyList<string> VerifyArguments)
{
    /// <summary>False with <c>--no-self-contained</c>, which publishes framework-dependent.</summary>
    internal bool SelfContained { get; init; } = true;

    /// <summary>The <c>--property</c> values, passed after size's own properties so that they override its defaults.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> Properties { get; init; } = [];

    /// <summary>The MSBuild verbosity of the publish, from <c>--verbosity</c>; null leaves the dotnet default.</summary>
    internal string? Verbosity { get; init; }
}
internal sealed record SizeFile(string Path, string Category, long Bytes, string Sha256);
internal sealed record SizeVerification(string Status, int? ExitCode, string? Executable, IReadOnlyList<string> Arguments);
internal sealed record SizeInstalledRuntime(string Name, string Version, string Directory);

/// <summary>
/// The <c>runic.size/2</c> report that <c>dotnet runic size</c> writes. README.md documents its schema; keep both in
/// sync. Paths of files end in <c>Path</c>, directories in <c>Directory</c>; <c>createdAt</c> is UTC, ending in <c>Z</c>.
/// </summary>
internal sealed record SizeReport(string Schema, DateTime CreatedAt, string Project, string Runtime,
    string Sdk, string BuildOperatingSystem, string BuildArchitecture, IReadOnlyList<SizeInstalledRuntime> InstalledRuntimes,
    string? SourceRevision, bool? SourceDirty, IReadOnlyDictionary<string, string> EffectiveProperties,
    IReadOnlyDictionary<string, string> Libraries, string PublishDirectory, int PublishExitCode,
    string PublishLogPath, string ArchivePath, long TotalBytes, long CompressedBytes,
    IReadOnlyList<SizeFile> Files, SizeVerification Verification, IReadOnlyList<string> Notes)
{
    internal const string SchemaId = "runic.size/2";
}

internal sealed record SizeRun(int ExitCode, SizeCommandResult? Result);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SizeReport))]
internal partial class SizeJsonContext : JsonSerializerContext;

internal static class SizeApplication
{
    private static readonly string[] Properties = ["AssemblyName", "TargetFramework", "RuntimeIdentifier",
        "PublishAot", "PublishTrimmed", "TrimMode", "OptimizationPreference", "SelfContained",
        "InvariantGlobalization", "StripSymbols", "DebugType", "DebugSymbols", "StackTraceSupport",
        "EventSourceSupport", "UseSystemResourceKeys", "HttpActivityPropagationSupport", "EnableUnsafeBinaryFormatterSerialization",
        "ProjectAssetsFile", "RuntimeFrameworkVersion", "TargetLatestRuntimePatch"];

    internal static async Task<SizeRun> RunAsync(SizeOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Runtime))
            throw new DevUsageException("RAPPSIZE1001", "Specify --runtime explicitly, for example linux-x64.");
        if (options.VerifyArguments.Count != 0 && string.IsNullOrWhiteSpace(options.Verify))
            throw new DevUsageException("RAPPSIZE1002", "--verify-argument requires --verify.");
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
        string workingDirectory = Path.GetDirectoryName(project)!;
        // An explicit --report never replaces earlier evidence. Without one, each run gets its own report name, so a
        // second run in the same directory measures again instead of failing.
        string reportPath = string.IsNullOrWhiteSpace(options.Report)
            ? DefaultReportPath(Environment.CurrentDirectory, options.Runtime, DateTimeOffset.UtcNow)
            : Path.GetFullPath(options.Report);
        if (File.Exists(reportPath))
            throw new DevUsageException("RAPPSIZE1003", "The report already exists. Select a new --report path to preserve prior evidence.");
        string dotnet = DevApplication.ResolveDotNetHost();
        IReadOnlyList<string> settings = CreateSettings(options);
        string[] evaluated = [.. Properties, .. options.Properties.Select(static property => property.Key)
            .Where(name => !Properties.Contains(name, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];
        async Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments) =>
            await CommandRunner.RunAsync(executable, workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        async Task<Dictionary<string, string>> Evaluate()
        {
            CommandResult result = await Run(dotnet, ["msbuild", project, "-nologo", .. settings,
                $"-getProperty:{string.Join(',', evaluated)}"]);
            if (result.ExitCode != 0)
                throw new DevDevelopmentException("RAPPSIZE1004", "Could not evaluate publish settings: " + result.CombinedOutput);
            using var json = JsonDocument.Parse(result.StandardOutput);
            return json.RootElement.GetProperty("Properties").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }
        Dictionary<string, string> effective = await Evaluate();
        string directory = Path.Combine(Path.GetDirectoryName(reportPath)!,
            $"{Path.GetFileNameWithoutExtension(reportPath)}-{Guid.NewGuid():N}");
        string publishDirectory = Path.Combine(directory, "publish");
        Directory.CreateDirectory(publishDirectory);
        string logPath = Path.Combine(directory, "publish.log");
        string archive = Path.Combine(directory, "distribution.zip");
        string main = effective["AssemblyName"] + (options.Runtime.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");
        string sdk = (await Run(dotnet, ["--version"])).StandardOutput.Trim();
        IReadOnlyList<SizeInstalledRuntime> runtimes = ParseInstalledRuntimes((await Run(dotnet, ["--list-runtimes"])).StandardOutput);
        string? revision = null;
        bool? dirty = null;
        try
        {
            var git = await Run("git", ["rev-parse", "HEAD"]);
            if (git.ExitCode == 0)
            {
                revision = git.StandardOutput.Trim();
                var status = await Run("git", ["status", "--porcelain"]);
                if (status.ExitCode == 0) dirty = status.StandardOutput.Length != 0;
            }
        }
        catch (DevUsageException) { /* Source provenance is unavailable outside Git installations. */ }
        Console.WriteLine($"Publishing application for {options.Runtime}...");
        CommandResult publish = await Run(dotnet, ["publish", project, "--nologo", .. settings, "--output", publishDirectory,
            .. options.Verbosity is null ? [] : new[] { "--verbosity", options.Verbosity }]);
        await File.WriteAllTextAsync(logPath, publish.CombinedOutput, cancellationToken);
        Console.Write(publish.CombinedOutput);
        effective = await Evaluate();
        var libraries = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string assetsPath = Path.GetFullPath(effective["ProjectAssetsFile"], workingDirectory);
        if (File.Exists(assetsPath))
        {
            using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath, cancellationToken));
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
                libraries[library.Name] = library.Value.GetProperty("type").GetString()!;
        }
        var files = Inventory(publishDirectory, main, options.Runtime);
        ZipFile.CreateFromDirectory(publishDirectory, archive, CompressionLevel.SmallestSize, false);
        var verification = new SizeVerification("not-run", null, null, []);
        if (publish.ExitCode == 0 && !string.IsNullOrWhiteSpace(options.Verify))
        {
            string[] arguments = [.. options.VerifyArguments, publishDirectory, Path.Combine(publishDirectory, main)];
            CommandResult check;
            try { check = await Run(options.Verify, arguments); }
            catch (DevUsageException error) { check = new(-1, "", error.Message); }
            await File.WriteAllTextAsync(Path.Combine(directory, "verification.log"), check.CombinedOutput, cancellationToken);
            Console.Write(check.CombinedOutput);
            verification = new(check.ExitCode == 0 ? "passed" : "failed", check.ExitCode, options.Verify, arguments);
        }
        var report = new SizeReport(SizeReport.SchemaId, DateTime.UtcNow, project, options.Runtime,
            sdk, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), runtimes, revision, dirty, effective, libraries,
            publishDirectory, publish.ExitCode, logPath, archive, files.Sum(file => file.Bytes),
            new FileInfo(archive).Length, files, verification,
            ["All publish files are counted. File categories are heuristic and never remove files.",
             "Embedded frontend assets are counted inside their containing executable or assembly.",
             "NativeAOT library inventory does not attribute bytes inside the linked executable.",
             "Installed browsers and OS libraries are excluded. This does not measure startup or memory.",
             "Command stdout and stderr are each retained up to 4 Mi characters; longer logs are marked truncated.",
             "Empty evaluated properties mean unspecified. Application targets may further customize publishing."]);
        await using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(output, report, SizeJsonContext.Default.SizeReport, cancellationToken);
        Console.WriteLine($"{files.Sum(file => file.Bytes):N0} bytes on disk; {new FileInfo(archive).Length:N0} ZIP bytes; verification {verification.Status}.");
        Console.WriteLine($"Report: {reportPath}");
        var result = new SizeCommandResult(reportPath, options.Runtime, publish.ExitCode, report.TotalBytes, report.CompressedBytes,
            new SizeCommandVerification(verification.Status, verification.ExitCode));
        return new SizeRun(publish.ExitCode == 0 && verification.Status != "failed" ? Program.Success : Program.DevelopmentFailure, result);
    }

    /// <summary>
    /// The global properties of the evaluation and the publish: size's own, then each <c>--property</c>, which can
    /// override <c>OptimizationPreference</c> but not the properties that have their own options.
    /// </summary>
    internal static IReadOnlyList<string> CreateSettings(SizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = new List<string> { $"-p:Configuration={options.Configuration}",
            $"-p:RuntimeIdentifier={options.Runtime}", $"-p:PublishAot={options.Aot.ToString().ToLowerInvariant()}",
            $"-p:SelfContained={options.SelfContained.ToString().ToLowerInvariant()}", "-p:OptimizationPreference=Size" };
        settings.AddRange(options.Properties.Select(ToolOptionValues.PropertyArgument));
        return settings;
    }

    /// <summary>
    /// Reads <c>dotnet --list-runtimes</c>, one <c>Name Version [Directory]</c> line per runtime. A line in another
    /// shape is kept whole as the name, so nothing the SDK prints is lost.
    /// </summary>
    internal static IReadOnlyList<SizeInstalledRuntime> ParseInstalledRuntimes(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static line =>
            {
                string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                return parts is [string name, string version, string directory] && directory.StartsWith('[') && directory.EndsWith(']')
                    ? new SizeInstalledRuntime(name, version, directory[1..^1])
                    : new SizeInstalledRuntime(line, "", "");
            })
            .ToArray();

    /// <summary>
    /// The report a run without <c>--report</c> writes: <c>runic-size-&lt;runtime&gt;-&lt;UTC time&gt;.json</c> in
    /// <paramref name="directory"/>, with a counter when that name is taken.
    /// </summary>
    internal static string DefaultReportPath(string directory, string runtime, DateTimeOffset now)
    {
        string stem = $"runic-size-{runtime}-{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        string path = Path.Combine(directory, stem + ".json");
        for (int counter = 2; File.Exists(path); counter++)
            path = Path.Combine(directory, $"{stem}-{counter}.json");
        return Path.GetFullPath(path);
    }

    internal static IReadOnlyList<SizeFile> Inventory(string directory, string main, string runtime) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new SizeFile(Path.GetRelativePath(directory, path).Replace('\\', '/'),
                Classify(Path.GetRelativePath(directory, path).Replace('\\', '/'), main, runtime),
                new FileInfo(path).Length, Hash(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static string Classify(string path, string main, string runtime)
    {
        if (path == main) return "main-executable";
        if (path.StartsWith("runtimes/", StringComparison.Ordinal))
        {
            string rid = path.Split('/')[1];
            if (rid != runtime && !runtime.StartsWith(rid + "-", StringComparison.Ordinal)) return "other-rid";
        }
        if (path.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) || path.Contains(".dSYM/", StringComparison.Ordinal)) return "debug-symbols";
        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "documentation";
        if (path.EndsWith(".so", StringComparison.OrdinalIgnoreCase) || path.Contains(".so.", StringComparison.Ordinal) || path.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) || path.Contains("/native/", StringComparison.Ordinal) || path.EndsWith("WebView2Loader.dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith("webui-2.dll", StringComparison.OrdinalIgnoreCase)) return "native-dependency";
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return "managed-or-native-library";
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.StartsWith("www/", StringComparison.Ordinal)) return "frontend-assets";
        if (path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) return "runtime-metadata";
        return "other";
    }
}
