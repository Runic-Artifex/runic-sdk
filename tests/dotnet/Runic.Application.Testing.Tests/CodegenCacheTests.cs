using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Exercises the generator cache through the built executable. This protects
/// the build integration rather than merely testing manifest implementation
/// details in-process.
/// </summary>
// One at a time: these cases share the generated-client process lock under obj/ (GeneratedHarnessProcess).
[NotInParallel("GeneratedHarnessProcess")]
internal sealed class CodegenCacheTests
{
    private const string ManifestFileName = ".runic-bridge-generation-cache.json";

    [Test]
    public async Task RepeatedGenerationReusesAndRepairsTheCacheAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenCacheTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        if (!File.Exists(generator))
            throw new InvalidOperationException($"The built bridge generator was not found at '{generator}'.");

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-cache-{Guid.NewGuid():N}");
        var csharp = Path.Combine(temporaryRoot, "obj", "generated");
        var typescript = Path.Combine(temporaryRoot, "frontend", "generated");
        try
        {
            await GenerateAsync(root, generator, csharp, typescript).ConfigureAwait(false);

            var manifest = Path.Combine(csharp, ManifestFileName);
            Require(File.Exists(manifest), "The first bridge generation did not write a cache manifest.");
            var csharpOutput = Output(csharp, "*.Bridge.g.cs");
            var typeScriptOutput = Output(typescript, "*.ts");
            var originalCSharp = File.ReadAllText(csharpOutput);
            var originalTypeScript = File.ReadAllText(typeScriptOutput);

            // Use an explicitly old timestamp instead of a sleep. This makes a
            // cache hit observable even though WriteIfChanged also preserves the
            // generated files on a cache miss.
            var sentinel = DateTime.UtcNow.AddMinutes(-2);
            File.SetLastWriteTimeUtc(manifest, sentinel);
            var recordedSentinel = File.GetLastWriteTimeUtc(manifest);
            var supportsExplicitMtime = Math.Abs((recordedSentinel - sentinel).TotalSeconds) < 2;
            await GenerateAsync(root, generator, csharp, typescript, "--reactiveui-flavor", "primitives")
                .ConfigureAwait(false);
            Require(File.ReadAllText(csharpOutput) == originalCSharp && File.ReadAllText(typeScriptOutput) == originalTypeScript,
                "A cache hit changed generated output.");
            if (supportsExplicitMtime)
                Require(File.GetLastWriteTimeUtc(manifest) == recordedSentinel,
                    "The explicit primitives flavor should be equivalent to the default and reuse the cache entry.");

            // NavOrphanViewModel has no View. A cache hit skips generation but
            // repeats its warning, so a no-op build still reports it.
            Require(File.ReadAllText(manifest).Contains("warning RUNICBRIDGE013:", StringComparison.Ordinal),
                "The cache manifest did not record the generation's warnings.");
            File.SetLastWriteTimeUtc(manifest, sentinel);
            recordedSentinel = File.GetLastWriteTimeUtc(manifest);
            var replayed = await GenerateOutputAsync(root, generator, csharp, typescript).ConfigureAwait(false);
            Require(replayed.Contains("Bridge generation is up to date.", StringComparison.Ordinal)
                    && replayed.Contains("warning RUNICBRIDGE013:", StringComparison.Ordinal)
                    && replayed.Contains(nameof(NavOrphanViewModel), StringComparison.Ordinal),
                $"A cache hit did not repeat the recorded warnings.\n{replayed}");
            if (supportsExplicitMtime)
                Require(File.GetLastWriteTimeUtc(manifest) == recordedSentinel, "The warning replay was not a cache hit.");

            File.Delete(csharpOutput);
            await GenerateAsync(root, generator, csharp, typescript).ConfigureAwait(false);
            Require(File.Exists(csharpOutput) && File.ReadAllText(csharpOutput) == originalCSharp,
                "A missing generated C# output did not invalidate and repair the cache entry.");

            File.AppendAllText(typeScriptOutput, "\n// cache-test mutation\n");
            await GenerateAsync(root, generator, csharp, typescript).ConfigureAwait(false);
            Require(File.ReadAllText(typeScriptOutput) == originalTypeScript,
                "An edited generated TypeScript output did not invalidate and repair the cache entry.");

            File.SetLastWriteTimeUtc(manifest, sentinel);
            recordedSentinel = File.GetLastWriteTimeUtc(manifest);
            await GenerateAsync(root, generator, csharp, typescript, "--aot").ConfigureAwait(false);
            if (supportsExplicitMtime)
                Require(File.GetLastWriteTimeUtc(manifest) > recordedSentinel,
                    "An output-affecting generator option did not invalidate the cache entry.");

            // The TypeScript output may be an application source directory.
            // Hand-written modules survive generation and do not affect the
            // cache; stale files carrying the generated header are removed.
            var handWritten = Path.Combine(typescript, "main.ts");
            var handWrittenCSharp = Path.Combine(csharp, "Custom.g.cs");
            var staleTypeScript = Path.Combine(typescript, "removedView.ts");
            var staleCSharp = Path.Combine(csharp, "RemovedBridge.g.cs");
            File.WriteAllText(handWritten, "export const handWritten = true;\n");
            File.WriteAllText(handWrittenCSharp, "// hand-written\n");
            File.SetLastWriteTimeUtc(manifest, sentinel);
            recordedSentinel = File.GetLastWriteTimeUtc(manifest);
            await GenerateAsync(root, generator, csharp, typescript, "--aot").ConfigureAwait(false);
            if (supportsExplicitMtime)
                Require(File.GetLastWriteTimeUtc(manifest) == recordedSentinel,
                    "A hand-written module beside generated output invalidated the cache entry.");
            File.WriteAllText(staleTypeScript, "// <auto-generated />\nexport {};\n");
            File.WriteAllText(staleCSharp, "// <auto-generated />\n");
            await GenerateAsync(root, generator, csharp, typescript, "--aot").ConfigureAwait(false);
            Require(File.Exists(handWritten) && File.Exists(handWrittenCSharp),
                "Bridge generation deleted a hand-written file from an output directory.");
            Require(!File.Exists(staleTypeScript) && !File.Exists(staleCSharp),
                "Bridge generation retained a stale generated file.");
            Require(Directory.EnumerateFiles(csharp, "*.View.g.cs").Any(path => path.EndsWith("Tests.CollisionWindow.View.g.cs", StringComparison.Ordinal))
                && Directory.EnumerateFiles(csharp, "*.Bridge.g.cs").Any(path => path.EndsWith("Tests.CollisionWindowViewModel.Bridge.g.cs", StringComparison.Ordinal))
                && Directory.EnumerateFiles(csharp, "*.ItemView.View.g.cs").Count() == 2,
                "A View partial and a ViewModel bridge, or same-name Views, did not get distinct generated files.");

            // Library mode (#1318) is a cache argument: turning it on or off writes or removes the barrels and the marker.
            var barrel = Path.Combine(typescript, "runic-clients.ts");
            var marker = Path.Combine(csharp, "RunicClientLibrary.g.cs");
            await GenerateAsync(root, generator, csharp, typescript, "--aot", "--client-library").ConfigureAwait(false);
            Require(File.Exists(barrel) && File.Exists(marker), "Turning library mode on was a cache hit.");
            File.Delete(marker);
            await GenerateAsync(root, generator, csharp, typescript, "--aot", "--client-library").ConfigureAwait(false);
            Require(File.Exists(marker), "A missing library-mode marker did not invalidate and repair the cache entry.");
            await GenerateAsync(root, generator, csharp, typescript, "--aot").ConfigureAwait(false);
            Require(!File.Exists(barrel) && !File.Exists(marker), "Turning library mode off was a cache hit.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static async Task GenerateAsync(string root, string generator, string csharp, string typescript,
        params string[] options)
    {
        await GeneratedHarnessProcess.RunAsync("dotnet",
            [generator, "--generate", typeof(CodegenCacheTests).Assembly.Location, csharp, .. options], root).ConfigureAwait(false);
        // The build runs the TypeScript emitter on the IR after the generator (R7 G6).
        var (exitCode, output) = await TypeScriptEmitterProcess.RunAsync(csharp, typescript).ConfigureAwait(false);
        Require(exitCode == 0, $"The TypeScript emitter failed.\n{output}");
    }

    private static async Task<string> GenerateOutputAsync(string root, string generator, string csharp, string typescript)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root,
                ArgumentList = { generator, "--generate", typeof(CodegenCacheTests).Assembly.Location, csharp },
            },
        };
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The bridge generator did not finish within 30 seconds.");
        }
        var (exitCode, text) = await TypeScriptEmitterProcess.AfterGeneratorAsync(
            (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false)), csharp, typescript).ConfigureAwait(false);
        Require(exitCode == 0, $"The bridge generator or the TypeScript emitter failed.\n{text}");
        return text;
    }

    private static string Output(string directory, string pattern) => Directory.EnumerateFiles(directory, pattern)
        .OrderBy(path => path, StringComparer.Ordinal)
        .FirstOrDefault() ?? throw new InvalidOperationException($"Generation did not produce {pattern} in '{directory}'.");

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
