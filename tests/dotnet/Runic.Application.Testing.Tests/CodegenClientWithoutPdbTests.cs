using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// #1298: the generator reads member locations and the comments of partial properties through each assembly's PDB, so a
/// build without one (Release with <c>DebugType</c> none) writes clients without their declaration maps and those comments.
/// Committed clients would change between configurations without a hint, so RUNICBRIDGE076 names the assembly.
/// </summary>
internal sealed partial class CodegenDiagnosticsTests
{
    // A ViewModel library that the application presents, with a [Reactive] partial property: its summary is only on the
    // declaring part, which the generator finds through the library's PDB.
    private const string PdbLibrarySource = """
        using System.ComponentModel;

        namespace FixtureLibrary;

        /// <summary>A note.</summary>
        public sealed partial class NotesViewModel : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

            /// <summary>The note's title.</summary>
            [ReactiveUI.SourceGenerators.Reactive]
            public partial string Title { get; set; }

            public int Count { get; private set; }
        }
        """;

    private const string PdbApplicationSource = Preamble + """
        /// <summary>The main window.</summary>
        public sealed partial class MainViewModel : FixtureModel
        {
            /// <summary>The name to save.</summary>
            [ReactiveUI.SourceGenerators.Reactive]
            public partial string Name { get; set; }

            public int Total { get; private set; }
        }
        public sealed partial class MainWindow : RunicWindow<MainViewModel>;
        public sealed partial class NotesWindow : RunicWindow<FixtureLibrary.NotesViewModel>;
        """;

    // What ReactiveUI's source generator adds to each assembly: its attribute and the implementation part, documented
    // as <inheritdoc/>, in a document that is not on disk.
    private static string ReactiveGenerated(string ns, string type, string property) => $$"""
        namespace ReactiveUI.SourceGenerators
        {
            [System.AttributeUsage(System.AttributeTargets.Property)]
            internal sealed class ReactiveAttribute : System.Attribute { }
        }
        namespace {{ns}}
        {
            partial class {{type}}
            {
                /// <inheritdoc/>
                public partial string {{property}} { get => field; set => field = value; }
            }
        }
        """;

    // The issue's reproduction: build Release, then Release with -p:DebugType=none (no PDB for the application or the
    // referenced library), then only the library without a PDB.
    [Test]
    public async Task ClientsWithoutAPdbWarnAndNameTheAssemblyAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-client-pdb-{Guid.NewGuid():N}");
        try
        {
            var directory = Path.Combine(temporaryRoot, "app");
            var typescript = Path.Combine(directory, "ts");
            var first = await GenerateWithPdbsAsync(directory, application: true, library: true).ConfigureAwait(false);
            Require(!first.Contains("warning", StringComparison.OrdinalIgnoreCase), $"A Release build with PDBs warned.\n{first}");
            var committed = Directory.GetFiles(typescript).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
            foreach (var (client, summary) in new[] { ("main", "The name to save."), ("notes", "The note's title.") })
            {
                var contract = committed[$"{client}.contract.d.ts"];
                Require(contract.EndsWith($"//# sourceMappingURL={client}.contract.d.ts.map\n", StringComparison.Ordinal)
                        && committed.ContainsKey($"{client}.contract.d.ts.map"),
                    $"The Release build's {client} client has no declaration map:\n{contract}");
                Require(contract.Contains(summary, StringComparison.Ordinal),
                    $"The Release build's {client} contract lacks the [Reactive] property's summary:\n{contract}");
            }

            // -p:DebugType=none: neither assembly has a PDB. Each contract loses its map and its [Reactive] summary, and the
            // warning names the assembly that declares the client's ViewModel and the fix.
            var none = await GenerateWithPdbsAsync(directory, application: false, library: false).ConfigureAwait(false);
            var warnings = ClientWithoutPdbWarnings(none);
            foreach (var (client, assembly, other, summary) in new[]
                     {
                         ("main", "FixtureSourceMap", "FixtureLibrary", "The name to save."),
                         ("notes", "FixtureLibrary", "FixtureSourceMap", "The note's title."),
                     })
            {
                var contractPath = Path.Combine(typescript, $"{client}.contract.d.ts");
                var contract = File.ReadAllText(contractPath);
                Require(!File.Exists(contractPath + ".map") && !contract.Contains("sourceMappingURL", StringComparison.Ordinal)
                        && !contract.Contains(summary, StringComparison.Ordinal),
                    $"Without PDBs the {client} contract kept its map or its [Reactive] summary:\n{contract}");
                var warning = warnings.SingleOrDefault(line => line.StartsWith(contractPath + ": warning RUNICBRIDGE076: ", StringComparison.Ordinal));
                Require(warning is not null
                        && warning.Contains("loses its declaration map and ", StringComparison.Ordinal)
                        && warning.Contains("documentation line", StringComparison.Ordinal)
                        && warning.Contains($"because {assembly} has no PDB", StringComparison.Ordinal)
                        && !warning.Contains(other, StringComparison.Ordinal)
                        && warning.Contains("<DebugType>portable</DebugType>", StringComparison.Ordinal)
                        && warning.Contains("runicbridge076", StringComparison.Ordinal),
                    $"Without PDBs the {client} contract has no RUNICBRIDGE076 that names {assembly} and the fix.\n{none}");
            }
            Require(warnings.All(line => line.StartsWith(typescript, StringComparison.Ordinal)),
                $"A RUNICBRIDGE076 does not name a generated client:\n{none}");

            // With the PDBs back, the clients are the committed ones again, without a warning.
            var again = await GenerateWithPdbsAsync(directory, application: true, library: true).ConfigureAwait(false);
            Require(!again.Contains("warning", StringComparison.OrdinalIgnoreCase), $"Restoring the PDBs warned.\n{again}");
            RequireCommitted(typescript, committed, client: null);

            // Only the referenced library without a PDB, which is how the issue met it: only its client changes and warns.
            var library = await GenerateWithPdbsAsync(directory, application: true, library: false).ConfigureAwait(false);
            warnings = ClientWithoutPdbWarnings(library);
            Require(warnings.Count > 0 && warnings.All(line => line.StartsWith(Path.Combine(typescript, "notes."), StringComparison.Ordinal)
                        && line.Contains("because FixtureLibrary has no PDB", StringComparison.Ordinal)),
                $"Without the library's PDB, RUNICBRIDGE076 does not name only the library's client and the library.\n{library}");
            Require(!File.Exists(Path.Combine(typescript, "notes.contract.d.ts.map")), "The library's client kept its declaration map.");
            RequireCommitted(typescript, committed, client: "main");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // Compiles the library and the application as an optimized build does, each with a portable PDB or none (as
    // DebugType=none), with their XML documentation files, and runs the generator over the application.
    private static async Task<string> GenerateWithPdbsAsync(string directory, bool application, bool library)
    {
        Directory.CreateDirectory(Path.Combine(directory, "library"));
        var librarySourcePath = Path.Combine(directory, "library", "Library.cs");
        File.WriteAllText(librarySourcePath, PdbLibrarySource);
        var libraryCompilation = Optimized(Compile("Library", PdbLibrarySource, librarySourcePath)
            .AddSyntaxTrees(Generated(ReactiveGenerated("FixtureLibrary", "NotesViewModel", "Title"), directory, "NotesViewModel.g.cs")));
        var libraryAssembly = Path.Combine(directory, "FixtureLibrary.dll");
        EmitWithDocumentation(libraryCompilation, libraryAssembly, library);

        var applicationSourcePath = Path.Combine(directory, "Fixture.cs");
        File.WriteAllText(applicationSourcePath, PdbApplicationSource);
        var applicationCompilation = Optimized(Compile("SourceMap", PdbApplicationSource, applicationSourcePath,
                [MetadataReference.CreateFromFile(libraryAssembly)])
            .AddSyntaxTrees(Generated(ReactiveGenerated("Fixture", "MainViewModel", "Name"), directory, "MainViewModel.g.cs")));
        EmitWithDocumentation(applicationCompilation, Path.Combine(directory, "FixtureSourceMap.dll"), application);
        foreach (var reference in applicationCompilation.GetUsedAssemblyReferences().OfType<PortableExecutableReference>())
            if (reference.FilePath is { } path && Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
                File.Copy(path, Path.Combine(directory, Path.GetFileName(path)), overwrite: true);

        var (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: false).ConfigureAwait(false);
        Require(exitCode == 0, $"The generator failed (application PDB {application}, library PDB {library}).\n{output}");
        return output;

        static CSharpCompilation Optimized(CSharpCompilation compilation) =>
            compilation.WithOptions(compilation.Options.WithOptimizationLevel(OptimizationLevel.Release));

        static SyntaxTree Generated(string source, string directory, string name) => CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Latest), Path.Combine(directory, "generated", name), System.Text.Encoding.UTF8);
    }

    private static void EmitWithDocumentation(CSharpCompilation compilation, string assembly, bool symbols)
    {
        var pdb = Path.ChangeExtension(assembly, ".pdb");
        // DebugType=none writes no PDB, and leaves none from an earlier build beside the assembly.
        File.Delete(pdb);
        Microsoft.CodeAnalysis.Emit.EmitResult emitted;
        using (var image = File.Create(assembly))
        using (var documentation = File.Create(Path.ChangeExtension(assembly, ".xml")))
        {
            if (symbols)
            {
                using var symbolStream = File.Create(pdb);
                emitted = compilation.Emit(image, symbolStream, documentation, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                    debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb, pdbFilePath: pdb));
            }
            else emitted = compilation.Emit(image, xmlDocumentationStream: documentation);
        }
        Require(emitted.Success, $"{Path.GetFileName(assembly)} did not compile.\n"
            + string.Join('\n', emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
    }

    private static List<string> ClientWithoutPdbWarnings(string output) => output.Split('\n')
        .Select(line => line.TrimEnd('\r')).Where(line => line.Contains(": warning RUNICBRIDGE076: ", StringComparison.Ordinal)).ToList();

    // The generated files are the committed ones: all of them, or those of one client.
    private static void RequireCommitted(string typescript, Dictionary<string, string> committed, string? client)
    {
        var current = Directory.GetFiles(typescript).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
        foreach (var (name, text) in committed.Where(entry => client is null || entry.Key.StartsWith(client + ".", StringComparison.Ordinal)))
            Require(current.TryGetValue(name, out var now) && now == text, $"{name} differs from the Release build's.");
        if (client is null)
            Require(current.Keys.Order(StringComparer.Ordinal).SequenceEqual(committed.Keys.Order(StringComparer.Ordinal)),
                $"The generated files are {string.Join(", ", current.Keys.Order(StringComparer.Ordinal))}.");
    }
}
