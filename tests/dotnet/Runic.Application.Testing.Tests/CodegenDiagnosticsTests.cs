using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Compiles small ViewModel assemblies that the generator must reject and
/// checks its diagnostics. Valid shapes live in this project instead, so the
/// project build itself proves that their generated C# compiles.
/// </summary>
internal static class CodegenDiagnosticsTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Text;
        using CommunityToolkit.Mvvm.Input;
        using Runic.Application.Views;

        namespace Fixture;

        public abstract class FixtureModel : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        """;

    internal static async Task RunAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-diagnostics-{Guid.NewGuid():N}");
        try
        {
            async Task Reject(string name, string members, params string[] expected)
            {
                var output = await GenerateAsync(generator, temporaryRoot, name, Preamble + members).ConfigureAwait(false);
                foreach (var text in expected)
                    Require(output.Contains(text, StringComparison.Ordinal),
                        $"{name}: the generator output did not contain '{text}'.\n{output}");
            }

            await Reject("HashSetValue", """
                public sealed class SetViewModel : FixtureModel { public HashSet<string> Tags { get; } = []; }
                public sealed partial class SetWindow(SetViewModel model) : RunicWindow<SetViewModel>(model);
                """, "System.Collections.Generic.HashSet`1", "is not a supported bridge collection").ConfigureAwait(false);
            await Reject("ObjectValue", """
                public sealed class ObjectViewModel : FixtureModel { public object Value { get; } = new(); }
                public sealed partial class ObjectWindow(ObjectViewModel model) : RunicWindow<ObjectViewModel>(model);
                """, "System.Object is not an explicitly supported bridge value").ConfigureAwait(false);
            await Reject("FrameworkValue", """
                public sealed class BuilderViewModel : FixtureModel { public StringBuilder Text { get; } = new(); }
                public sealed partial class BuilderWindow(BuilderViewModel model) : RunicWindow<BuilderViewModel>(model);
                """, "System.Text.StringBuilder is not an explicitly supported bridge value").ConfigureAwait(false);
            await Reject("IntegerDictionary", """
                public sealed class LookupViewModel : FixtureModel { public Dictionary<int, string> Lookup { get; } = []; }
                public sealed partial class LookupWindow(LookupViewModel model) : RunicWindow<LookupViewModel>(model);
                """, "is not a supported bridge dictionary").ConfigureAwait(false);
            await Reject("EmptyDto", """
                public sealed class Marker { }
                public sealed class MarkerViewModel : FixtureModel { public Marker Value { get; } = new(); }
                public sealed partial class MarkerWindow(MarkerViewModel model) : RunicWindow<MarkerViewModel>(model);
                """, "Fixture.Marker has no public readable properties").ConfigureAwait(false);
            await Reject("DerivedCollection", """
                public sealed class Names : List<string> { }
                public sealed class NamesViewModel : FixtureModel { public Names Values { get; } = []; }
                public sealed partial class NamesWindow(NamesViewModel model) : RunicWindow<NamesViewModel>(model);
                """, "Fixture.Names is not a supported bridge collection").ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>Compiles one fixture assembly and returns the failed generator's output.</summary>
    private static async Task<string> GenerateAsync(string generator, string temporaryRoot, string name, string source)
    {
        var directory = Path.Combine(temporaryRoot, name);
        Directory.CreateDirectory(directory);
        var assembly = Path.Combine(directory, $"Fixture{name}.dll");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create($"Fixture{name}",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var emitted = compilation.Emit(assembly);
        if (!emitted.Success)
            throw new InvalidOperationException($"{name}: the fixture did not compile.\n"
                + string.Join('\n', emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                ArgumentList = { generator, "--generate", assembly, Path.Combine(directory, "cs"), Path.Combine(directory, "ts"), "--no-registry" },
            },
        };
        process.StartInfo.Environment["RUNIC_BRIDGE_CODEGEN_CACHE"] = "0";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{name}: the generator did not exit within 30 seconds.");
        }
        var text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        Require(process.ExitCode != 0, $"{name}: the generator accepted an invalid ViewModel.\n{text}");
        Require(!text.Contains("Unhandled exception", StringComparison.Ordinal),
            $"{name}: the generator crashed instead of reporting a diagnostic.\n{text}");
        return text;
    }

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
