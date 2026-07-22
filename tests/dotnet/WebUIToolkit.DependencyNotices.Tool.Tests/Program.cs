using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Evidence;
using WebUIToolkit.DependencyNotices.Tool;

namespace WebUIToolkit.DependencyNotices.Tool.Tests;

internal static class Program
{
    private static readonly (string Name, Func<Task> Run)[] Tests =
    [
        ("exit codes are stable and bounded", StableExitCodes),
        ("parser recognizes manual scan aliases", ParserRecognizesManualScan),
        ("parser rejects unknown and duplicate options", ParserRejectsUnknownOptions),
        ("parser failures honor JSON diagnostics", ParserFailureUsesJson),
        ("network flag is acquire only", NetworkFlagIsAcquireOnly),
        ("commands require explicit inputs", CommandsRequireExplicitInputs),
        ("NuGet and npm scanners dispatch offline", EcosystemScannersDispatch),
        ("policy command evaluates explicit input", PolicyCommandEvaluates),
        ("generate and verify compose renderer", GenerateAndVerify),
        ("generate refuses input collisions", GenerateRefusesInputCollision),
        ("SBOM command reconciles identity", SbomCommandReconciles),
        ("acquire uses explicit policy and cached bytes", AcquireUsesExplicitPolicy),
        ("purl contract emits canonical human value", PackageUrlContract),
        ("spdx contract emits canonical JSON", SpdxContract),
        ("diagnostic catalog emits versioned JSON", DiagnosticCatalog),
        ("manual scan succeeds without mutation", ManualScanSucceedsWithoutMutation),
        ("manual scan emits machine-readable diagnostics", ManualScanEmitsJsonDiagnostics),
        ("cancelled command has stable exit", CancellationIsStable),
        ("output sanitization redacts credentials and home", OutputIsSanitized),
        ("published executable entry point runs", ExecutableRuns),
    ];

    public static async Task<int> Main()
    {
        int failures = 0;
        foreach ((string name, Func<Task> run) in Tests)
        {
            try
            {
                await run().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"TOTAL {Tests.Length} PASSED {Tests.Length - failures} FAILED {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static Task StableExitCodes()
    {
        Equal(0, ToolExitCodes.Success);
        Equal(1, ToolExitCodes.UnexpectedFailure);
        Equal(2, ToolExitCodes.InvalidCommandOrConfiguration);
        Equal(3, ToolExitCodes.InventoryOrEvidenceIncomplete);
        Equal(4, ToolExitCodes.PolicyRejected);
        Equal(5, ToolExitCodes.OutputDrift);
        Equal(6, ToolExitCodes.SbomMismatch);
        Equal(7, ToolExitCodes.AcquisitionOrNetworkFailure);
        return Task.CompletedTask;
    }

    private static Task ParserRecognizesManualScan()
    {
        Equal(ToolCommand.ManualScan, CommandLineParser.Parse(["scan", "manual"]).Invocation!.Command);
        Equal(ToolCommand.ManualScan, CommandLineParser.Parse(["manual", "scan"]).Invocation!.Command);
        return Task.CompletedTask;
    }

    private static Task ParserRejectsUnknownOptions()
    {
        False(CommandLineParser.Parse(["scan", "manual", "--wat"]).Succeeded);
        False(CommandLineParser.Parse(["scan", "manual", "--root", ".", "--root", "."]).Succeeded);
        False(CommandLineParser.Parse(["unknown"]).Succeeded);
        return Task.CompletedTask;
    }

    private static async Task NetworkFlagIsAcquireOnly()
    {
        ToolParseResult denied = CommandLineParser.Parse(["generate", "--allow-network"]);
        False(denied.Succeeded);
        Contains("WUTNOTICE7001", denied.Error!);
        False(CommandLineParser.Parse(["acquire"]).Succeeded);
        True(CommandLineParser.Parse([
            "acquire", "--allow-network", "--origin", "https://evidence.example/license", "--sha256", new string('a', 64),
            "--cache", "cache", "--allow-host", "evidence.example"]).Succeeded);
        (int deniedCode, _, _) = await RunAsync(["generate", "--allow-network"]).ConfigureAwait(false);
        Equal(ToolExitCodes.AcquisitionOrNetworkFailure, deniedCode);
        (int missingOptInCode, _, _) = await RunAsync(["acquire"]).ConfigureAwait(false);
        Equal(ToolExitCodes.AcquisitionOrNetworkFailure, missingOptInCode);
    }

    private static async Task CommandsRequireExplicitInputs()
    {
        foreach (string command in new[] { "scan", "policy", "generate", "verify", "sbom" })
        {
            (int code, _, _) = await RunAsync([command]).ConfigureAwait(false);
            Equal(ToolExitCodes.InvalidCommandOrConfiguration, code);
        }
        (int acquireCode, _, _) = await RunAsync(["acquire", "--allow-network"]).ConfigureAwait(false);
        Equal(ToolExitCodes.InvalidCommandOrConfiguration, acquireCode);
    }

    private static async Task EcosystemScannersDispatch()
    {
        string repository = FindRepositoryRoot();
        string nuget = Path.Combine(repository, "spec", "dependency-notices", "fixtures", "nuget", "valid");
        (int nugetCode, string nugetOutput, _) = await RunAsync([
            "scan", "nuget",
            "--lock", Path.Combine(nuget, "packages.lock.json"),
            "--assets", Path.Combine(nuget, "project.assets.json"),
            "--framework", "net10.0",
            "--packages-root", Path.Combine(nuget, "packages")]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, nugetCode);
        Contains("component pkg:nuget/", nugetOutput);

        string npmTemplate = Path.Combine(repository, "spec", "dependency-notices", "fixtures", "npm", "basic");
        using TemporaryDirectory npm = MaterializeNpmFixture(npmTemplate);
        (int npmCode, string npmOutput, _) = await RunAsync([
            "scan", "npm", "--root", npm.Path, "--lock", "package-lock.json"]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, npmCode);
        Contains("component pkg:npm/", npmOutput);
    }

    private static TemporaryDirectory MaterializeNpmFixture(string source)
    {
        TemporaryDirectory fixture = new();
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments[0] == "installed")
                {
                    segments[0] = "node_modules";
                }

                for (int index = 1; index < segments.Length; index++)
                {
                    if (segments[index] == "_modules_") segments[index] = "node_modules";
                }

                string target = Path.Combine(fixture.Path, Path.Combine(segments));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static async Task PolicyCommandEvaluates()
    {
        using TemporaryDirectory fixture = new();
        string policy = """
            {
              "schemaVersion": 1,
              "defaultDecision": "review",
              "licenses": { "allow": ["MIT"], "deny": [], "review": [], "obligations": {} },
              "missingEvidence": "error",
              "orExpressions": "require-explicit-selection",
              "overrides": []
            }
            """;
        string policyPath = Path.Combine(fixture.Path, "policy.json");
        File.WriteAllText(policyPath, policy, new UTF8Encoding(false));
        (int code, string output, _) = await RunAsync([
            "policy", "--policy", policyPath, "--purl", "pkg:generic/example@1.0.0", "--license", "MIT",
            "--evaluation-date", "2026-07-22", "--evidence-digest", new string('a', 64)]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, code);
        Contains("decision=allow", output);
    }

    private static async Task GenerateAndVerify()
    {
        using TemporaryDirectory fixture = CreateManualFixture(validDigest: true);
        string outputRoot = Path.Combine(fixture.Path, "out");
        string[] common = ["--root", fixture.Path, "--output", outputRoot, "--artifact-name", "fixture-app"];
        (int generated, _, _) = await RunAsync(["generate", .. common]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, generated);
        True(File.Exists(Path.Combine(outputRoot, "dependency-notices.json")));
        True(File.Exists(Path.Combine(outputRoot, "dependency-notices.html")));

        Dictionary<string, string> beforeVerify = Snapshot(fixture.Path);
        (int verified, string output, _) = await RunAsync(["verify", .. common]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, verified);
        Contains("verified", output);
        Equal(beforeVerify, Snapshot(fixture.Path));

        File.AppendAllText(Path.Combine(outputRoot, "dependency-notices.html"), "drift", Encoding.UTF8);
        (int drift, _, _) = await RunAsync(["verify", .. common]).ConfigureAwait(false);
        Equal(ToolExitCodes.OutputDrift, drift);
    }

    private static async Task SbomCommandReconciles()
    {
        using TemporaryDirectory fixture = new();
        string sbom = """
            {"bomFormat":"CycloneDX","specVersion":"1.6","serialNumber":"urn:uuid:00000000-0000-0000-0000-000000000001","components":[{"bom-ref":"example","type":"library","name":"example","version":"1.0.0","purl":"pkg:generic/example@1.0.0"}]}
            """;
        string path = Path.Combine(fixture.Path, "sbom.json");
        File.WriteAllText(path, sbom, new UTF8Encoding(false));
        (int code, string output, _) = await RunAsync([
            "sbom", "--sbom", path, "--component", "pkg:generic/example@1.0.0|example|1.0.0"]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, code);
        Contains("ref=example", output);
    }

    private static async Task GenerateRefusesInputCollision()
    {
        using TemporaryDirectory fixture = CreateManualFixture(validDigest: true);
        Dictionary<string, string> before = Snapshot(fixture.Path);
        (int code, _, _) = await RunAsync([
            "generate", "--root", fixture.Path, "--output", fixture.Path, "--artifact-name", "fixture-app"]).ConfigureAwait(false);
        Equal(ToolExitCodes.InvalidCommandOrConfiguration, code);
        Equal(before, Snapshot(fixture.Path));
    }

    private static async Task AcquireUsesExplicitPolicy()
    {
        using TemporaryDirectory fixture = new();
        byte[] evidence = Encoding.UTF8.GetBytes("cached evidence");
        string digest = Convert.ToHexString(SHA256.HashData(evidence)).ToLowerInvariant();
        string shaRoot = Path.Combine(fixture.Path, "sha256");
        Directory.CreateDirectory(shaRoot);
        File.WriteAllBytes(Path.Combine(shaRoot, digest), evidence);
        (int code, string output, string error) = await RunAsync([
            "acquire", "--allow-network", "--origin", "https://evidence.example/license", "--sha256", digest,
            "--cache", fixture.Path, "--allow-host", "evidence.example"]).ConfigureAwait(false);
        Equal(ToolExitCodes.Success, code);
        Contains("cached=true", output);
        Equal(string.Empty, error);
    }

    private static async Task PackageUrlContract()
    {
        (int code, string output, _) = await RunAsync(["contract", "purl", "--value", "pkg:NPM/%40scope/Name@1.0.0"]).ConfigureAwait(false);
        Equal(0, code);
        Equal("pkg:npm/%40scope/Name@1.0.0", output.Trim());
    }

    private static async Task SpdxContract()
    {
        (int code, string output, _) = await RunAsync(["contract", "spdx", "--value", "MIT OR Apache-2.0", "--diagnostics-format", "json"]).ConfigureAwait(false);
        Equal(0, code);
        using JsonDocument json = JsonDocument.Parse(output);
        Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Equal("MIT OR Apache-2.0", json.RootElement.GetProperty("canonical").GetString());
    }

    private static async Task ParserFailureUsesJson()
    {
        (int code, _, string error) = await RunAsync(["scan", "manual", "--unknown", "--format", "json"]).ConfigureAwait(false);
        Equal(ToolExitCodes.InvalidCommandOrConfiguration, code);
        using JsonDocument json = JsonDocument.Parse(error);
        Equal("WUTNOTICE1002", json.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    private static async Task DiagnosticCatalog()
    {
        (int code, string output, _) = await RunAsync(["contract", "diagnostics", "--format", "json"]).ConfigureAwait(false);
        Equal(0, code);
        using JsonDocument json = JsonDocument.Parse(output);
        True(json.RootElement.GetProperty("diagnosticCodes").GetArrayLength() >= 20);
    }

    private static async Task ManualScanSucceedsWithoutMutation()
    {
        using TemporaryDirectory fixture = CreateManualFixture(validDigest: true);
        Dictionary<string, string> before = Snapshot(fixture.Path);
        (int code, string output, string error) = await RunAsync(["scan", "manual", "--root", fixture.Path]).ConfigureAwait(false);
        Equal(0, code);
        Contains("pkg:generic/example@1.0.0", output);
        Equal(string.Empty, error);
        Equal(before, Snapshot(fixture.Path));
    }

    private static async Task ManualScanEmitsJsonDiagnostics()
    {
        using TemporaryDirectory fixture = CreateManualFixture(validDigest: false);
        (int code, string output, _) = await RunAsync(["scan", "manual", "--root", fixture.Path, "--format", "json"]).ConfigureAwait(false);
        Equal(ToolExitCodes.InventoryOrEvidenceIncomplete, code);
        using JsonDocument json = JsonDocument.Parse(output);
        Equal("WUTNOTICE2002", json.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    private static async Task CancellationIsStable()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        StringWriter output = new();
        StringWriter error = new();
        int code = await ToolApplication.RunAsync(["contract", "diagnostics"], output, error, source.Token).ConfigureAwait(false);
        Equal(ToolExitCodes.UnexpectedFailure, code);
    }

    private static Task OutputIsSanitized()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string sanitized = OutputSanitizer.Sanitize($"https://person:secret@example.test/license?token=abc#part {home}");
        False(sanitized.Contains("secret", StringComparison.Ordinal));
        False(sanitized.Contains("token", StringComparison.Ordinal));
        if (home.Length > 0) False(sanitized.Contains(home, StringComparison.OrdinalIgnoreCase));
        Contains("example.test/license", sanitized);
        return Task.CompletedTask;
    }

    private static async Task ExecutableRuns()
    {
        string assembly = typeof(ToolApplication).Assembly.Location;
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("contract");
        start.ArgumentList.Add("purl");
        start.ArgumentList.Add("--value");
        start.ArgumentList.Add("pkg:generic/example@1");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start tool process.");
        string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        Equal(0, process.ExitCode);
        Equal("pkg:generic/example@1", output.Trim());
        Equal(string.Empty, error);
    }

    private static TemporaryDirectory CreateManualFixture(bool validDigest)
    {
        TemporaryDirectory fixture = new();
        byte[] evidence = Encoding.UTF8.GetBytes("MIT License\n");
        File.WriteAllBytes(Path.Combine(fixture.Path, "LICENSE"), evidence);
        string digest = validDigest ? EvidenceDigest.ComputeSha256(evidence) : new string('0', 64);
        string json = $$"""
            {
              "schemaVersion": 1,
              "manualComponents": [
                {
                  "purl": "pkg:generic/example@1.0.0",
                  "displayName": "Example",
                  "revision": "1",
                  "licenseExpression": "MIT",
                  "evidence": [
                    { "kind": "License", "path": "LICENSE", "origin": "fixture", "sha256": "{{digest}}" }
                  ]
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(fixture.Path, "dependency-notices.json"), json, new UTF8Encoding(false));
        return fixture;
    }

    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();
        int code = await ToolApplication.RunAsync(args, output, error).ConfigureAwait(false);
        return (code, output.ToString(), error.ToString());
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "dependency-notices.html"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) { if (value) throw new InvalidOperationException("Expected false."); }
    private static void Contains(string expected, string actual) { if (!actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"Expected '{expected}'."); }
    private static void Equal<T>(T expected, T actual)
    {
        if (expected is Array expectedArray && actual is Array actualArray)
        {
            if (!expectedArray.Cast<object>().SequenceEqual(actualArray.Cast<object>())) throw new InvalidOperationException("Sequences differ.");
            return;
        }
        if (expected is Dictionary<string, string> expectedMap && actual is Dictionary<string, string> actualMap)
        {
            if (expectedMap.Count != actualMap.Count || expectedMap.Any(pair => !actualMap.TryGetValue(pair.Key, out string? value) || value != pair.Value)) throw new InvalidOperationException("Snapshots differ.");
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wut-notice-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
