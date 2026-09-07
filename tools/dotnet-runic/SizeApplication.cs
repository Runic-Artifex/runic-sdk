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

internal sealed record SizeOptions(string? Project, string Runtime, string Host, string Profile,
    string Configuration, string Report, bool Aot, string Verify, IReadOnlyList<string> VerifyArguments);
internal sealed record SizeFile(string Path, string Category, long Bytes, string Sha256);
internal sealed record SizeVerification(string Status, int? ExitCode, string? Executable, IReadOnlyList<string> Arguments);
internal sealed record SizeReport(string Schema, DateTimeOffset CreatedUtc, string Project, string Runtime,
    string Host, string Profile, string Sdk, string BuildOperatingSystem, string BuildArchitecture, string InstalledRuntimes, string? SourceRevision,
    bool? SourceDirty, IReadOnlyDictionary<string, string> EffectiveProperties,
    IReadOnlyDictionary<string, string> Libraries, string PublishDirectory, int PublishExitCode,
    string PublishLog, string Archive, long TotalBytes, long CompressedBytes,
    IReadOnlyList<SizeFile> Files, SizeVerification Verification, IReadOnlyList<string> Notes);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SizeReport))]
internal partial class SizeJsonContext : JsonSerializerContext;

internal static class SizeApplication
{
    private static readonly string[] Properties = ["AssemblyName", "TargetFramework", "RuntimeIdentifier",
        "RunicHost", "PublishAot", "PublishTrimmed", "TrimMode", "OptimizationPreference", "SelfContained",
        "InvariantGlobalization", "StripSymbols", "DebugType", "DebugSymbols", "StackTraceSupport",
        "EventSourceSupport", "UseSystemResourceKeys", "HttpActivityPropagationSupport", "EnableUnsafeBinaryFormatterSerialization",
        "RunicDesktopMinimalHost", "ProjectAssetsFile", "RuntimeFrameworkVersion", "TargetLatestRuntimePatch"];

    internal static async Task<int> RunAsync(SizeOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Runtime))
            throw new DevUsageException("RTKSIZE1001", "Specify --runtime explicitly, for example linux-x64.");
        if (options.Profile is not ("default" or "minimal"))
            throw new DevUsageException("RTKSIZE1001", "Profile must be default or minimal.");
        if (options.VerifyArguments.Count != 0 && string.IsNullOrWhiteSpace(options.Verify))
            throw new DevUsageException("RTKSIZE1001", "--verify-argument requires --verify.");
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
        string workingDirectory = Path.GetDirectoryName(project)!;
        string reportPath = Path.GetFullPath(options.Report);
        if (File.Exists(reportPath))
            throw new DevUsageException("RTKSIZE1001", "The report already exists. Select a new --report path to preserve prior evidence.");
        using var requestedHost = new HostSelectionScope(options.Host);
        string dotnet = DevApplication.ResolveDotNetHost();
        var settings = new List<string> { $"-p:Configuration={options.Configuration}",
            $"-p:RuntimeIdentifier={options.Runtime}", $"-p:PublishAot={options.Aot.ToString().ToLowerInvariant()}",
            "-p:SelfContained=true", "-p:OptimizationPreference=Size",
            $"-p:RunicDesktopMinimalHost={(options.Profile == "minimal" ? "true" : "false")}" };
        async Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments) =>
            await CommandRunner.RunAsync(executable, workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        async Task<Dictionary<string, string>> Evaluate()
        {
            CommandResult result = await Run(dotnet, ["msbuild", project, "-nologo", .. settings,
                $"-getProperty:{string.Join(',', Properties)}"]);
            if (result.ExitCode != 0)
                throw new DevUsageException("RTKSIZE1002", "Could not evaluate publish settings: " + result.CombinedOutput);
            using var json = JsonDocument.Parse(result.StandardOutput);
            return json.RootElement.GetProperty("Properties").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        }
        Dictionary<string, string> effective = await Evaluate();
        string host = effective["RunicHost"];
        if (options.Profile == "minimal" && host == "cswebui")
            throw new DevUsageException("RTKSIZE1001", "The minimal profile applies to Desktop. Use --profile default with CS-WebUI.");
        // The evaluated selection also reaches contract generation and frontend tooling.
        using var selectedHost = new HostSelectionScope(host);
        string directory = Path.Combine(Path.GetDirectoryName(reportPath)!,
            $"{Path.GetFileNameWithoutExtension(reportPath)}-{Guid.NewGuid():N}");
        string publishDirectory = Path.Combine(directory, "publish");
        Directory.CreateDirectory(publishDirectory);
        string logPath = Path.Combine(directory, "publish.log");
        string archive = Path.Combine(directory, "distribution.zip");
        string main = effective["AssemblyName"] + (options.Runtime.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");
        string sdk = (await Run(dotnet, ["--version"])).StandardOutput.Trim();
        string runtimes = (await Run(dotnet, ["--list-runtimes"])).StandardOutput.Trim();
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
        Console.WriteLine($"Publishing {host} / {options.Profile} for {options.Runtime}...");
        CommandResult publish = await Run(dotnet, ["publish", project, "--nologo", .. settings, "--output", publishDirectory]);
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
        var report = new SizeReport("runic.size/1", DateTimeOffset.UtcNow, project, options.Runtime,
            host, options.Profile, sdk, RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(), runtimes, revision, dirty, effective, libraries,
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
        return publish.ExitCode == 0 && verification.Status != "failed" ? Program.Success : Program.DevelopmentFailure;
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
