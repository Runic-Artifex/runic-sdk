namespace Runic.Application.Testing.Tests;

/// <summary>
/// Exercises the generator cache through the built executable. This protects
/// the build integration rather than merely testing manifest implementation
/// details in-process.
/// </summary>
internal static class CodegenCacheTests
{
    private const string ManifestFileName = ".runic-bridge-generation-cache.json";

    internal static async Task RunAsync()
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
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static Task GenerateAsync(string root, string generator, string csharp, string typescript,
        params string[] options) => GeneratedHarnessProcess.RunAsync("dotnet",
        [generator, "--generate", typeof(CodegenCacheTests).Assembly.Location, csharp, typescript, .. options], root);

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
