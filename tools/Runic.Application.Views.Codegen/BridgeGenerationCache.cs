using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using Runic.Application.Views.Codegen.ReactiveUI;
using Runic.Application.Views.Codegen.Toolkit;

/// <summary>
/// Content-addressed cache for the multi-view bridge generator.
///
/// The manifest intentionally lives beside generated C# in <c>obj</c>, never
/// in the frontend tree. A hit is accepted only when both inputs and every
/// output still have the recorded content hash, so an edited or removed
/// generated file cannot be silently retained.
/// </summary>
internal static class BridgeGenerationCache
{
    private const string ManifestFileName = ".runic-bridge-generation-cache.json";
    private const int SchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Returns true only when generation can be safely skipped.</summary>
    internal static bool TryHit(string modelAssemblyPath, string csharpOutputDirectory,
        string typeScriptOutputDirectory, IEnumerable<string> outputAffectingArguments)
    {
        if (IsBypassed()) return false;

        try
        {
            var roots = Roots.Create(csharpOutputDirectory, typeScriptOutputDirectory);
            var manifestPath = Path.Combine(roots.CSharp, ManifestFileName);
            if (!File.Exists(manifestPath)) return false;

            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null || manifest.SchemaVersion != SchemaVersion
                || !string.Equals(manifest.Fingerprint,
                    ComputeFingerprint(modelAssemblyPath, roots, outputAffectingArguments),
                    StringComparison.Ordinal)) return false;

            return VerifyOutputs(roots, manifest);
        }
        // A cache is an optimization. Corrupt manifests, temporary IO trouble,
        // and a concurrent cleaner must cause regeneration instead of failure.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or JsonException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Persists a cache entry only after all generator writes succeed.</summary>
    internal static void Save(string modelAssemblyPath, string csharpOutputDirectory,
        string typeScriptOutputDirectory, IEnumerable<string> outputAffectingArguments)
    {
        if (IsBypassed()) return;

        var roots = Roots.Create(csharpOutputDirectory, typeScriptOutputDirectory);
        var manifest = new Manifest
        {
            SchemaVersion = SchemaVersion,
            Fingerprint = ComputeFingerprint(modelAssemblyPath, roots, outputAffectingArguments),
            CSharpOutputs = CaptureOutputs(roots.CSharp, IsCSharpGeneratedFile),
            TypeScriptOutputs = CaptureOutputs(roots.TypeScript, IsTypeScriptGeneratedFile),
        };
        WriteAtomically(Path.Combine(roots.CSharp, ManifestFileName),
            JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static bool IsBypassed() => IsDisabled(Environment.GetEnvironmentVariable("RUNIC_BRIDGE_CODEGEN_CACHE"))
        || IsTruthy(Environment.GetEnvironmentVariable("RUNIC_BRIDGE_CODEGEN_FORCE"));

    private static bool IsTruthy(string? value) => value is not null
        && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static bool IsDisabled(string? value) => value is not null
        && (value.Equals("0", StringComparison.Ordinal) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase));

    private static string ComputeFingerprint(string modelAssemblyPath, Roots roots,
        IEnumerable<string> outputAffectingArguments)
    {
        var fullModel = Path.GetFullPath(modelAssemblyPath);
        var modelDirectory = Path.GetDirectoryName(fullModel)
            ?? throw new ArgumentException("The model assembly must have a parent directory.", nameof(modelAssemblyPath));
        var inputs = new List<string>
        {
            $"schema:{SchemaVersion}",
            $"model:{fullModel}",
            $"csharp-output:{roots.CSharp}",
            $"typescript-output:{roots.TypeScript}",
        };
        inputs.AddRange(outputAffectingArguments.Select(argument => "arg:" + argument));

        // A compiled model commonly brings its project and package dependencies
        // into the same output directory. Hashing every DLL there catches those
        // changes without recursively hashing the application or its frontend.
        foreach (var dependency in Directory.EnumerateFiles(modelDirectory, "*.dll")
                     .Select(Path.GetFullPath).OrderBy(path => path, StringComparer.Ordinal))
            inputs.Add("model-directory:" + dependency + ":" + HashFile(dependency));

        foreach (var runtime in GeneratorAndRuntimeAssemblies()
                     .OrderBy(path => path, StringComparer.Ordinal))
            inputs.Add("generator-runtime:" + runtime + ":" + HashFile(runtime));

        return HashText(string.Join('\n', inputs));
    }

    private static IEnumerable<string> GeneratorAndRuntimeAssemblies()
    {
        var assemblies = new[]
        {
            Assembly.GetExecutingAssembly(),
            typeof(IBridgeTransport).Assembly,
            typeof(ToolkitCommandInspector).Assembly,
            typeof(ReactiveCommandInspector).Assembly,
        };
        return assemblies.Select(assembly => assembly.Location)
            .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal);
    }

    private static bool VerifyOutputs(Roots roots, Manifest manifest) =>
        VerifyOutputGroup(roots.CSharp, manifest.CSharpOutputs, IsCSharpGeneratedFile)
        && VerifyOutputGroup(roots.TypeScript, manifest.TypeScriptOutputs, IsTypeScriptGeneratedFile);

    private static bool VerifyOutputGroup(string root, IReadOnlyList<Output>? expected,
        Func<string, bool> include)
    {
        if (expected is null) return false;
        var actual = CaptureOutputs(root, include);
        if (actual.Count != expected.Count) return false;
        for (var index = 0; index < actual.Count; index++)
            if (expected[index] is null || !string.Equals(actual[index].Path, expected[index].Path, StringComparison.Ordinal)
                || !string.Equals(actual[index].Hash, expected[index].Hash, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static List<Output> CaptureOutputs(string root, Func<string, bool> include)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(include)
            .Select(path => new Output
            {
                Path = Path.GetFileName(path),
                Hash = HashFile(path),
            })
            .OrderBy(output => output.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsCSharpGeneratedFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".Bridge.g.cs", StringComparison.Ordinal)
            || fileName.EndsWith(".View.g.cs", StringComparison.Ordinal)
            || string.Equals(fileName, "RunicBridgeComposition.g.cs", StringComparison.Ordinal);
    }

    // The TypeScript directory can be application source. Hand-written modules
    // beside generated ones must neither invalidate nor be recorded by the cache.
    private static bool IsTypeScriptGeneratedFile(string path) =>
        path.EndsWith(".ts", StringComparison.Ordinal) && GeneratedOutput.IsGenerated(path);

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string Fingerprint { get; set; } = "";
        public List<Output>? CSharpOutputs { get; set; }
        public List<Output>? TypeScriptOutputs { get; set; }
    }

    private sealed class Output
    {
        public string Path { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private sealed record Roots(string CSharp, string TypeScript)
    {
        internal static Roots Create(string csharp, string typeScript) =>
            new(Path.GetFullPath(csharp), Path.GetFullPath(typeScript));
    }
}
