using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// The development source map (#951): the generator writes <c>sources.json</c> beside the generated C# with each
/// Bridge member's C# symbol and file:line:column, and without a PDB it writes the map without locations.
/// </summary>
internal sealed partial class CodegenDiagnosticsTests
{
    private const string SourceMapFixture = Preamble + """
        public sealed partial class MapViewModel : FixtureModel
        {
            public int Count { get; private set; }
            private string title = "";
            public string Title
            {
                get => title;
                set => title = value;
            }
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _total;
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] public partial string Name { get; set; }
            [RelayCommand]
            private void Save() { }
            [System.CodeDom.Compiler.GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
            public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
            public IRelayCommand ResetCommand { get; } = new RelayCommand(Reset);
            private static void Reset() { }
            public ReactiveUI.ReactiveCommand<string, string> EchoCommand { get; }
            public ReactiveUI.ReactiveCommand<string, string> ShoutCommand { get; }
            public MapViewModel()
            {
                EchoCommand = ReactiveUI.ReactiveCommand.Create<string, string>(Echo);
                ShoutCommand = ReactiveUI.ReactiveCommand.Create<string, string>(value => value.ToUpperInvariant());
            }
            private string Echo(string value) => value;
            public IRelayCommand ArchiveCommand { get; } = new RelayCommand(Archive);
            private static void Archive()
            {
                System.Console.WriteLine("archived");
            }
            private string notes = "";
            public string Notes
            {
                get
                {
                    return notes;
                }
                set
                {
                    notes = value;
                }
            }
        }
        public sealed partial class MapWindow : RunicWindow<MapViewModel>;
        """;

    // What CommunityToolkit's source generators would add, in a document that is not on disk, as generator output is.
    private const string SourceMapGenerated = """
        namespace Fixture;
        partial class MapViewModel
        {
            public int Total { get => _total; set => _total = value; }
            public partial string Name { get => field; set => field = value; }
        }
        """;

    [Test]
    public async Task SourceMapLocatesEveryBridgeMemberAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-source-map-{Guid.NewGuid():N}");
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "with-pdb", symbols: true).ConfigureAwait(false);
            var source = Path.GetFullPath(Path.Combine(directory, "Fixture.cs"));
            using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cs", "sources.json")));
            var root = map.RootElement;
            Require(root.GetProperty("format").GetString() == "runic.views.bridge-sources/1", "The map has no runic.views.bridge-sources/1 format.");
            var lines = SourceMapFixture.Split('\n');
            // A text ending in a line break is a whole line.
            int Line(string text) => Array.FindIndex(lines, line => text.EndsWith('\n')
                ? line.TrimEnd('\r') == text[..^1] : line.Contains(text, StringComparison.Ordinal)) + 1;
            int Column(string text, string name) => lines[Line(text) - 1].IndexOf(name, StringComparison.Ordinal) + 1;

            var viewModel = root.GetProperty("viewModels").EnumerateArray().Single();
            Require(viewModel.GetProperty("id").GetString() == "Fixture.MapViewModel" && viewModel.GetProperty("name").GetString() == "Map"
                    && viewModel.GetProperty("route").GetString() == "map"
                    && viewModel.GetProperty("contract").GetString()!.StartsWith("Fixture.MapViewModel:", StringComparison.Ordinal)
                    && viewModel.GetProperty("pageKinds").EnumerateArray().Select(kind => kind.GetString()).SequenceEqual(["map"])
                    && At(viewModel, source, Line("public sealed partial class MapViewModel"), Column("public sealed partial class MapViewModel", "MapViewModel")),
                $"The ViewModel entry is wrong: {viewModel}");

            var members = root.GetProperty("members").EnumerateArray().ToDictionary(entry => entry.GetProperty("id").GetString()!);
            JsonElement Member(string id) => members.TryGetValue("Fixture.MapViewModel/" + id, out var entry) ? entry
                : throw new InvalidOperationException($"The map has no {id}: {string.Join(", ", members.Keys)}");
            void Expect(string id, string symbol, string lineText, string? property = null, string? columnName = null)
            {
                var entry = Member(id);
                Require(entry.GetProperty("symbol").GetString() == symbol, $"{id}: symbol {entry.GetProperty("symbol")} is not {symbol}.");
                Require((entry.TryGetProperty("property", out var value) ? value.GetString() : null) == property, $"{id}: property is not {property}.");
                Require(At(entry, source, Line(lineText), columnName is null ? null : Column(lineText, columnName)),
                    $"{id}: {entry} is not at line {Line(lineText)} of {source}.");
            }
            // Properties: the getter, and the setter for a member the page sets; a generated property's declaration.
            Expect("state/count", "Fixture.MapViewModel.Count", "public int Count { get; private set; }");
            Require(!members.ContainsKey("Fixture.MapViewModel/setter/count"), "A private setter is no page setter.");
            Expect("state/title", "Fixture.MapViewModel.Title", "get => title;");
            Expect("setter/title", "Fixture.MapViewModel.Title.set", "set => title = value;");
            Expect("state/total", "Fixture.MapViewModel.Total", "private int _total;", columnName: "_total");
            Expect("setter/total", "Fixture.MapViewModel.Total.set", "private int _total;", columnName: "_total");
            Expect("state/name", "Fixture.MapViewModel.Name", "public partial string Name", columnName: "Name {");
            // Commands: the handler method, from [RelayCommand], a method group, or the command's assignment.
            Expect("command/save", "Fixture.MapViewModel.Save()", "private void Save()", "Fixture.MapViewModel.SaveCommand");
            Expect("command/reset", "Fixture.MapViewModel.Reset()", "private static void Reset()", "Fixture.MapViewModel.ResetCommand");
            Expect("command/echo", "Fixture.MapViewModel.Echo(String)", "private string Echo(string value)", "Fixture.MapViewModel.EchoCommand");
            Expect("command/shout", "Fixture.MapViewModel.ShoutCommand", "ShoutCommand = ReactiveUI.ReactiveCommand", columnName: "ShoutCommand");
            Expect("commandState/canSave", "Fixture.MapViewModel.Save()", "private void Save()", "Fixture.MapViewModel.SaveCommand");
            // A block body: the declaration, not the body's first sequence point, which a Debug and a Release build put on
            // different lines.
            Expect("command/archive", "Fixture.MapViewModel.Archive()", "private static void Archive()", "Fixture.MapViewModel.ArchiveCommand", columnName: "Archive()");
            Expect("state/notes", "Fixture.MapViewModel.Notes", "        get\n", columnName: "get");
            Expect("setter/notes", "Fixture.MapViewModel.Notes.set", "        set\n", columnName: "set");
            Require(members.Values.All(entry => entry.GetProperty("id").GetString()
                    == $"{entry.GetProperty("viewModel").GetString()}/{entry.GetProperty("kind").GetString()}/{entry.GetProperty("member").GetString()}"),
                "A member id is not {viewModel}/{kind}/{member}.");

            // The generated TypeScript carries no source path, and the cache records the map: a deleted map regenerates.
            Require(Directory.GetFiles(Path.Combine(directory, "ts")).All(path => !File.ReadAllText(path).Contains(directory, StringComparison.Ordinal)),
                "The generated TypeScript names a source path.");

            // Without a PDB the generator still succeeds and writes the map, with no locations; the client is the same, and
            // its contract only lacks the declaration map's comment (#952).
            var withoutSymbols = await GenerateSourceMapFixtureAsync(temporaryRoot, "without-pdb", symbols: false).ConfigureAwait(false);
            using var bare = JsonDocument.Parse(File.ReadAllText(Path.Combine(withoutSymbols, "cs", "sources.json")));
            var bareMembers = bare.RootElement.GetProperty("members").EnumerateArray().ToArray();
            Require(bareMembers.Length == members.Count && bareMembers.All(entry => !entry.TryGetProperty("location", out _))
                    && bare.RootElement.GetProperty("viewModels").EnumerateArray().All(entry => !entry.TryGetProperty("location", out _)),
                $"Without a PDB the map should list every member without a location:\n{bare.RootElement}");
            foreach (var path in Directory.GetFiles(Path.Combine(directory, "ts")).Where(path => !path.EndsWith(".map", StringComparison.Ordinal)))
                Require(WithoutMapComment(File.ReadAllText(path)) == File.ReadAllText(Path.Combine(withoutSymbols, "ts", Path.GetFileName(path))),
                    $"{Path.GetFileName(path)} differs with and without the PDB.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // CI builds Release with deterministic paths, a developer usually Debug with local ones; a map committed with
    // generated output (#952) must be the same for both. A method's first sequence point differs between them (the
    // body's "{" in Debug, its first statement in Release), so locations are declarations.
    [Test]
    public async Task SourceMapIsTheSameForEveryConfigurationAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-source-map-configurations-{Guid.NewGuid():N}");
        try
        {
            string Map(string directory) => File.ReadAllText(Path.Combine(directory, "cs", "sources.json"))
                .Replace(JsonSerializer.Serialize(directory)[1..^1], "<fixture>", StringComparison.Ordinal);
            var debug = Map(await GenerateSourceMapFixtureAsync(temporaryRoot, "debug", symbols: true).ConfigureAwait(false));
            var release = Map(await GenerateSourceMapFixtureAsync(temporaryRoot, "release", symbols: true,
                optimization: OptimizationLevel.Release).ConfigureAwait(false));
            var deterministic = Map(await GenerateSourceMapFixtureAsync(temporaryRoot, "deterministic", symbols: true,
                optimization: OptimizationLevel.Release, mapPaths: true).ConfigureAwait(false));
            Require(debug.Contains("\"line\"", StringComparison.Ordinal), $"The Debug map has no locations:\n{debug}");
            Require(release == debug, $"A Release build's map differs from a Debug build's:\n{debug}\n---\n{release}");
            Require(deterministic == debug, $"A deterministic Release build's map differs from a Debug build's:\n{debug}\n---\n{deterministic}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Test]
    public async Task SourceMapIsACachedOutputAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-source-map-cache-{Guid.NewGuid():N}");
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "cached", symbols: true, cache: true).ConfigureAwait(false);
            var map = Path.Combine(directory, "cs", "sources.json");
            var written = File.ReadAllText(map);
            var (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: true).ConfigureAwait(false);
            Require(exitCode == 0 && output.Contains("Bridge generation is up to date.", StringComparison.Ordinal),
                $"An unchanged generation was not a cache hit.\n{output}");
            File.Delete(map);
            (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: true).ConfigureAwait(false);
            Require(exitCode == 0 && !output.Contains("up to date", StringComparison.Ordinal) && File.ReadAllText(map) == written,
                $"A deleted source map did not regenerate.\n{output}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // Compiles the fixture to <root>/<name>/FixtureSourceMap.dll (with a PDB over Fixture.cs on disk, or none) and
    // runs the generator into cs/ and ts/ there.
    // With mapPaths, the PDB names the documents under /_/ as a deterministic CI build does (PathMap), and the generator
    // gets the build's --path-map.
    private static async Task<string> GenerateSourceMapFixtureAsync(string temporaryRoot, string name, bool symbols, bool cache = false,
        string fixture = SourceMapFixture, string generatedSource = SourceMapGenerated,
        OptimizationLevel optimization = OptimizationLevel.Debug, bool mapPaths = false)
    {
        var directory = Path.Combine(temporaryRoot, name);
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "Fixture.cs");
        File.WriteAllText(sourcePath, fixture);
        var documents = mapPaths ? "/_/" : directory + Path.DirectorySeparatorChar;
        var compilation = Compile("SourceMap", fixture, documents + "Fixture.cs").AddSyntaxTrees(CSharpSyntaxTree.ParseText(generatedSource,
            new CSharpParseOptions(LanguageVersion.Latest), documents + "generated/MapViewModel.g.cs", System.Text.Encoding.UTF8));
        compilation = compilation.WithOptions(compilation.Options.WithOptimizationLevel(optimization));
        var assembly = Path.Combine(directory, "FixtureSourceMap.dll");
        Microsoft.CodeAnalysis.Emit.EmitResult emitted;
        using (var image = File.Create(assembly))
        {
            if (symbols)
            {
                using var pdb = File.Create(Path.ChangeExtension(assembly, ".pdb"));
                emitted = compilation.Emit(image, pdb, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                    debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb,
                    pdbFilePath: Path.ChangeExtension(assembly, ".pdb")));
            }
            else emitted = compilation.Emit(image);
        }
        Require(emitted.Success, "The source map fixture did not compile.\n"
            + string.Join('\n', emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        foreach (var reference in compilation.GetUsedAssemblyReferences().OfType<PortableExecutableReference>())
            if (reference.FilePath is { } path && Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
                File.Copy(path, Path.Combine(directory, Path.GetFileName(path)), overwrite: true);
        var (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache,
            mapPaths ? ["--path-map", $"{directory}{Path.DirectorySeparatorChar}=/_/"] : []).ConfigureAwait(false);
        Require(exitCode == 0 && !output.Contains("warning", StringComparison.OrdinalIgnoreCase),
            $"{name}: the generator did not accept the source map fixture cleanly.\n{output}");
        return directory;
    }

    private static async Task<(int ExitCode, string Output)> RunSourceMapGeneratorAsync(string directory, bool cache,
        IReadOnlyList<string>? options = null)
    {
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(FindWorkspaceRoot(), "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                ArgumentList = { generator, "--generate", Path.Combine(directory, "FixtureSourceMap.dll"), Path.Combine(directory, "cs"),
                    Path.Combine(directory, "ts") },
            },
        };
        foreach (var option in options ?? []) process.StartInfo.ArgumentList.Add(option);
        process.StartInfo.Environment["RUNIC_BRIDGE_CODEGEN_CACHE"] = cache ? "1" : "0";
        process.StartInfo.Environment.Remove("RUNIC_BRIDGE_CODEGEN_FORCE");
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitScaledAsync(GeneratorTimeout).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }

    private static bool At(JsonElement entry, string file, int line, int? column) =>
        entry.TryGetProperty("location", out var location) && location.GetProperty("file").GetString() == file
        && location.GetProperty("line").GetInt32() == line && (column is null || location.GetProperty("column").GetInt32() == column)
        && location.GetProperty("column").GetInt32() > 0;
}
