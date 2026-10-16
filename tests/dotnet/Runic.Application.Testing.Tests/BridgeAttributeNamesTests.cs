using System.Reflection;
using Microsoft.CodeAnalysis;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// How the Bridge reads attributes (BridgeAttributeNames): through reflection
/// in a running application, so Hot Reload sees the loaded module, and from
/// the module file's metadata only when an attribute type cannot be resolved,
/// as in the code generator without the Windows Desktop framework.
/// </summary>
internal sealed class BridgeAttributeNamesTests
{
    // A WPF stand-in that the fixtures reference but this process cannot load.
    private const string PresentationFramework = """
        namespace System.Windows;
        [AttributeUsage(AttributeTargets.All)]
        public sealed class LocalizabilityAttribute : Attribute { }
        public static class Holder
        {
            [AttributeUsage(AttributeTargets.All)]
            public sealed class MarkerAttribute : Attribute { }
        }
        """;

    private const string Dtos = """
        using System;
        using System.Text.Json.Serialization;
        using System.Windows;

        [assembly: Runic.Application.Views.RunicBridgeJsonIgnore]
        [assembly: Localizability]

        namespace Fixture;

        public static class Outer
        {
            [AttributeUsage(AttributeTargets.All)]
            public sealed class NestedAttribute : Attribute { }
        }

        public sealed class Dto
        {
            [JsonIgnore, Localizability] public string Implicit { get; set; } = "";
            [JsonIgnore(Condition = JsonIgnoreCondition.Always), Localizability] public string Always { get; set; } = "";
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), Localizability] public string? WhenNull { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), Localizability] public int WhenDefault { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.Never), Localizability] public string Never { get; set; } = "";
            [Outer.Nested, Holder.Marker] public string Nested { get; set; } = "";
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Reflected { get; set; }
            [JsonIgnore] public string ReflectedImplicit { get; set; } = "";
            [JsonExtensionData, Localizability] public System.Collections.Generic.Dictionary<string, object>? Extension { get; set; }
            [JsonExtensionData] public System.Collections.Generic.Dictionary<string, object>? ReflectedExtension { get; set; }
        }
        """;

    [Test]
    public void MetadataFallbackReadsJsonIgnoreConditionsAndNestedAttributeTypes()
    {
        var directory = Directory.CreateTempSubdirectory("runic-attribute-names-").FullName;
        try
        {
            var wpf = Image("PresentationFramework", PresentationFramework);
            var path = Path.Combine(directory, "FixtureAttributeNames.dll");
            Emit("FixtureAttributeNames", Dtos, path, MetadataReference.CreateFromImage(wpf));
            var assembly = Assembly.LoadFrom(path);
            var dto = assembly.GetType("Fixture.Dto", throwOnError: true)!;
            PropertyInfo Property(string name) => dto.GetProperty(name)!;
            // The WPF attributes make reflection fail, so these go through metadata.
            Require(Throws(() => assembly.CustomAttributes.ToList()) && Throws(() => Property("WhenNull").CustomAttributes.ToList()),
                "The stand-in WPF attributes resolved; the metadata fallback is not exercised.");

            Require(BridgeDtoContract.HonorsJsonIgnore(assembly), "The assembly opt-in was not found beside a WPF attribute.");
            foreach (var name in new[] { "Implicit", "Always", "ReflectedImplicit" })
                Require(BridgeDtoContract.IsIgnored(Property(name), honorJsonIgnore: true), $"{name}: an unconditional [JsonIgnore] was not honored.");
            foreach (var name in new[] { "Extension", "ReflectedExtension" })
            {
                Require(BridgeDtoContract.IsIgnored(Property(name), honorJsonIgnore: true), $"{name}: [JsonExtensionData] was not excluded under the opt-in.");
                Require(!BridgeDtoContract.IsIgnored(Property(name), honorJsonIgnore: false), $"{name}: [JsonExtensionData] was excluded without the opt-in.");
            }
            foreach (var name in new[] { "WhenNull", "WhenDefault", "Never", "Reflected", "Nested" })
                Require(!BridgeDtoContract.IsIgnored(Property(name), honorJsonIgnore: true), $"{name}: a conditional or absent [JsonIgnore] excluded the property.");
            Require(!BridgeDtoContract.IsIgnored(Property("Implicit"), honorJsonIgnore: false), "[JsonIgnore] excluded a property without the opt-in.");

            var nested = BridgeAttributeNames.Of(Property("Nested")).Select(entry => entry.Name).ToArray();
            Require(nested.Contains("Fixture.Outer+NestedAttribute") && nested.Contains("System.Windows.Holder+MarkerAttribute"),
                $"Nested attribute types were named {string.Join(", ", nested)}.");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Test]
    public void RunningApplicationsReadTheLoadedModuleNotTheFile()
    {
        // Hot Reload applies metadata updates to the loaded module; the file on
        // disk does not change. Simulate a file that differs from the loaded
        // module: replace it after loading, as a rebuild would.
        var directory = Directory.CreateTempSubdirectory("runic-attribute-runtime-").FullName;
        try
        {
            const string Loaded = """
                [assembly: Runic.Application.Views.RunicBridgeJsonIgnore]
                namespace Fixture;
                public sealed class ItemViewModel { [Runic.Application.Views.RunicIgnore] public string Title { get; set; } = ""; }
                """;
            const string OnDisk = """
                namespace Fixture;
                public sealed class ItemViewModel { public string Title { get; set; } = ""; }
                """;
            var path = Path.Combine(directory, "FixtureRuntimeAttributes.dll");
            Emit("FixtureRuntimeAttributes", Loaded, path);
            var assembly = Assembly.LoadFrom(path);
            var replacement = Path.Combine(directory, "replacement.dll");
            Emit("FixtureRuntimeAttributes", OnDisk, replacement);
            // A loaded image can be renamed, also on Windows, but not overwritten there.
            File.Move(path, Path.Combine(directory, "loaded.dll"));
            File.Move(replacement, path);

            var title = assembly.GetType("Fixture.ItemViewModel", throwOnError: true)!.GetProperty("Title")!;
            Require(BridgeDtoContract.IsRunicIgnored(title), "[RunicIgnore] of the loaded module was read from the file instead.");
            Require(BridgeDtoContract.HonorsJsonIgnore(assembly), "The loaded assembly's opt-in was read from the file instead.");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static byte[] Image(string name, string source)
    {
        using var image = new MemoryStream();
        var emitted = CodegenDiagnosticsTests.CompileFixture(name, source, []).Emit(image);
        Require(emitted.Success, $"{name} did not compile.\n{string.Join('\n', emitted.Diagnostics)}");
        return image.ToArray();
    }

    private static void Emit(string name, string source, string path, params MetadataReference[] references)
    {
        using var image = File.Create(path);
        var emitted = CodegenDiagnosticsTests.CompileFixture(name, source, references).Emit(image);
        Require(emitted.Success, $"{name} did not compile.\n{string.Join('\n', emitted.Diagnostics)}");
    }

    private static bool Throws(Func<object> read)
    {
        try { return read() is null; }
        catch (FileNotFoundException) { return true; }
    }

    // A loaded image cannot be deleted on Windows; the temporary directory is
    // then left for the system to clean.
    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
