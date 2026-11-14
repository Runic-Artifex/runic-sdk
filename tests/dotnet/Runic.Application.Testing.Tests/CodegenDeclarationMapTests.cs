using System.Text.Json;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// The generated clients' declaration maps (#952): each client's types are in <c>&lt;view&gt;.contract.d.ts</c>, whose
/// <c>.d.ts.map</c> leads every Bridge member to the C# location in the development source map (#951), so tsserver's
/// Go to Definition on <c>client.save()</c> opens the ViewModel's C#.
/// </summary>
internal sealed partial class CodegenDiagnosticsTests
{
    private const string DeclarationMapFixture = Preamble + """
        public sealed partial class LinkViewModel : FixtureModel
        {
            public int Count { get; private set; }
            private string title = "";
            public string Title
            {
                get => title;
                set => title = value;
            }
            [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private int _total;
            [RelayCommand]
            private void Save() { }
            [System.CodeDom.Compiler.GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
            public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
            public IRelayCommand<string> RenameCommand { get; } = new RelayCommand<string>(Rename);
            private static void Rename(string? name) { }
            public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(LoadAsync);
            private static System.Threading.Tasks.Task LoadAsync() => System.Threading.Tasks.Task.CompletedTask;
            public ViewRequest<string> Focus { get; } = new();
            public ViewQuestion<string, bool> Confirm { get; } = new((_, _) => System.Threading.Tasks.ValueTask.FromResult(false));
        }
        public sealed partial class LinkWindow : RunicWindow<LinkViewModel>;
        """;

    private const string DeclarationMapGenerated = """
        namespace Fixture;
        partial class LinkViewModel
        {
            public int Total { get => _total; set => _total = value; }
        }
        """;

    // Each name the contract declares for a Bridge member, and the source map entry it leads to ("" is the ViewModel). A
    // checked field's name in LinkCheckedFields leads to its setter.
    private static readonly (string Name, string Id)[] DeclarationMapNames =
    [
        ("LinkState", ""), ("LinkClient", ""), ("LinkPageReference", ""),
        ("count", "state/count"), ("title", "state/title"), ("total", "state/total"),
        ("canSave", "commandState/canSave"), ("canLoad", "commandState/canLoad"), ("isLoadExecuting", "commandState/isLoadExecuting"),
        ("setTitle", "setter/title"), ("setTotal", "setter/total"), ("writeTitle", "setter/title"), ("writeTotal", "setter/total"),
        ("LinkCheckedFields.title", "setter/title"), ("LinkCheckedFields.total", "setter/total"),
        ("save", "command/save"), ("rename", "command/rename"), ("canRename", "command/rename"), ("load", "command/load"),
        ("startLoad", "command/load"), ("recoverLoad", "command/load"),
        ("confirm", "interaction/confirm"), ("focus", "request/focus"),
    ];

    [Test]
    public async Task DeclarationMapLeadsEveryClientMemberToItsCSharpAsync()
    {
        var temporaryRoot = DeclarationMapRoot();
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "with-pdb", symbols: true,
                fixture: DeclarationMapFixture, generatedSource: DeclarationMapGenerated).ConfigureAwait(false);
            var typescript = Path.Combine(directory, "ts");
            var contractPath = Path.Combine(typescript, "link.contract.d.ts");
            var contract = File.ReadAllText(contractPath);
            Require(contract.EndsWith("\n//# sourceMappingURL=link.contract.d.ts.map\n", StringComparison.Ordinal),
                $"The contract does not name its declaration map:\n{contract}");
            using var map = JsonDocument.Parse(File.ReadAllText(contractPath + ".map"));
            var segments = DecodeDeclarationMap(map.RootElement, contract, typescript);
            var sources = map.RootElement.GetProperty("sources").EnumerateArray().Select(source => source.GetString()!).ToArray();
            Require(sources.SequenceEqual(["../Fixture.cs"]) && !map.RootElement.TryGetProperty("sourcesContent", out _)
                    && map.RootElement.GetProperty("file").GetString() == "link.contract.d.ts",
                $"The map's sources are not the fixture relative to the map, or it has sourcesContent: {map.RootElement}");

            // Every mapped name leads where the development source map locates its member, and only the expected names
            // are mapped.
            using var sourceMap = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cs", "sources.json")));
            var locations = sourceMap.RootElement.GetProperty("members").EnumerateArray()
                .Concat(sourceMap.RootElement.GetProperty("viewModels").EnumerateArray())
                .Where(entry => entry.TryGetProperty("location", out _))
                .ToDictionary(entry => entry.GetProperty("id").GetString()!, entry => entry.GetProperty("location").Clone());
            string Id(string member) => "Fixture.LinkViewModel" + (member.Length == 0 ? "" : "/" + member);
            Require(DeclarationMapNames.All(entry => locations.ContainsKey(Id(entry.Id))),
                $"The source map has no location for {string.Join(", ", DeclarationMapNames.Where(entry => !locations.ContainsKey(Id(entry.Id))).Select(entry => entry.Id))}.");
            // A name is "Interface.member" when another interface declares the same member.
            var lines = contract.Split('\n');
            string Interface(int line) => Enumerable.Range(0, line + 1).Select(index => Regex.Match(lines[line - index], @"^export interface (\w+)"))
                .First(match => match.Success).Groups[1].Value;
            var mapped = segments.Where(segment => segment.Source is not null)
                .Select(segment => (Name: Regex.Match(lines[segment.Line][segment.Column..], @"^[\w$]+").Value, Segment: segment))
                .Select(entry => (Name: Interface(entry.Segment.Line) == "LinkCheckedFields" && entry.Name != "LinkCheckedFields" ? "LinkCheckedFields." + entry.Name : entry.Name, entry.Segment))
                .ToDictionary(entry => entry.Name, entry => entry.Segment);
            Require(mapped.Keys.Order(StringComparer.Ordinal).SequenceEqual(DeclarationMapNames.Select(entry => entry.Name).Order(StringComparer.Ordinal)),
                $"The mapped names are {string.Join(", ", mapped.Keys.Order(StringComparer.Ordinal))}.\n{contract}");
            foreach (var (name, member) in DeclarationMapNames)
            {
                var segment = mapped[name];
                var location = locations[Id(member)];
                Require(Path.GetFullPath(Path.Combine(typescript, segment.Source!)) == location.GetProperty("file").GetString()
                        && segment.SourceLine + 1 == location.GetProperty("line").GetInt32()
                        && segment.SourceColumn + 1 == location.GetProperty("column").GetInt32(),
                    $"{name} maps to {segment.Source}:{segment.SourceLine + 1}:{segment.SourceColumn + 1}, not {Id(member)} at {location}.");
            }
            // The lines a reader expects: the handler of a command, the setter, and the getter of a state member.
            var fixtureLines = File.ReadAllLines(Path.Combine(directory, "Fixture.cs"));
            int Line(string text) => Array.FindIndex(fixtureLines, line => line.Contains(text, StringComparison.Ordinal));
            Require(mapped["save"].SourceLine == Line("private void Save()") && mapped["rename"].SourceLine == Line("private static void Rename(")
                    && mapped["setTitle"].SourceLine == Line("set => title = value;") && mapped["title"].SourceLine == Line("get => title;")
                    && mapped["count"].SourceLine == Line("public int Count") && mapped["LinkClient"].SourceLine == Line("public sealed partial class LinkViewModel"),
                "A command, setter, state member or the client does not map to its C# line.");

            // <view>.ts carries no mapping and keeps exporting the client's types.
            var client = File.ReadAllText(Path.Combine(typescript, "link.ts"));
            Require(!client.Contains("sourceMappingURL", StringComparison.Ordinal)
                    && Regex.IsMatch(client, @"^export type \{ LinkState, [^}]*\bLinkClient, LinkPageReference \} from ""\./link\.contract\.js"";$", RegexOptions.Multiline),
                $"link.ts does not re-export its contract:\n{client}");

            // Without the PDB a new generation removes the map and its comment: tsserver reads <file>.map even without one.
            File.Delete(Path.Combine(directory, "FixtureSourceMap.pdb"));
            var (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: false).ConfigureAwait(false);
            Require(exitCode == 0 && !output.Contains("warning", StringComparison.OrdinalIgnoreCase), $"The generator failed without a PDB.\n{output}");
            Require(!File.Exists(contractPath + ".map") && File.ReadAllText(contractPath) == WithoutMapComment(contract),
                "Without a PDB the contract keeps a declaration map or differs by more than its comment.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // tsserver, as VS Code and Visual Studio run it, follows the map from a command, a setter and a state member to their
    // C# lines, and Go to Type Definition on the client to the ViewModel class.
    [Test]
    public async Task TsServerGoesFromTheClientToTheViewModelsCSharpAsync()
    {
        var temporaryRoot = DeclarationMapRoot();
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "tsserver", symbols: true,
                fixture: DeclarationMapFixture, generatedSource: DeclarationMapGenerated).ConfigureAwait(false);
            var app = Path.Combine(directory, "ts", "app.ts");
            string[] source =
            [
                "import { connectLink, type LinkClient, type LinkState } from \"./link.js\";",
                "export async function main(state: LinkState): Promise<number> {",
                "  const client: LinkClient = await connectLink();",
                "  await client.save();",
                "  await client.setTitle(\"x\");",
                "  await client.rename(\"y\");",
                "  return state.count + client.snapshot.total;",
                "}",
            ];
            File.WriteAllText(app, string.Join('\n', source) + "\n");
            string At(string text, string name, string command)
            {
                var line = Array.FindIndex(source, line => line.Contains(text, StringComparison.Ordinal));
                return $"{line + 1}:{source[line].IndexOf(name, StringComparison.Ordinal) + 1}:{command}";
            }
            var results = await TsServerAsync(FindWorkspaceRoot(), app,
            [
                At("client.save()", "save", "definition"),
                At("client.setTitle(", "setTitle", "definition"),
                At("client.rename(", "rename", "definition"),
                At("state.count", "count", "definition"),
                At("client.snapshot.total", "total", "definition"),
                At("const client", "client", "typeDefinition"),
                At("const client", "connectLink", "definition"),
            ]).ConfigureAwait(false);
            var fixture = File.ReadAllLines(Path.Combine(directory, "Fixture.cs"));
            var fixturePath = Path.GetFullPath(Path.Combine(directory, "Fixture.cs"));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool Lands(string[] definitions, string text) => definitions.Any(definition =>
            {
                var parts = definition.Split(':');
                var line = int.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture);
                return string.Equals(Path.GetFullPath(string.Join(':', parts[..^2])), fixturePath, comparison)
                    && fixture[line - 1].Contains(text, StringComparison.Ordinal);
            });
            Require(Lands(results[0], "private void Save()") && Lands(results[1], "set => title = value;") && Lands(results[2], "private static void Rename(")
                    && Lands(results[3], "public int Count") && Lands(results[4], "private int _total;"),
                $"Go to Definition did not reach the C# lines:\n{string.Join('\n', results.Select(result => string.Join(", ", result)))}");
            Require(Lands(results[5], "public sealed partial class LinkViewModel"),
                $"Go to Type Definition on the client did not reach the ViewModel class: {string.Join(", ", results[5])}");
            // Code that is not a Bridge member stays in the generated TypeScript. (connectLink returns LinkClient &
            // Disposable, and tsserver has no type definition for an intersection, so a variable is typed LinkClient here.)
            Require(results[6].Length > 0 && results[6].All(definition => definition.Contains("link.ts:", StringComparison.Ordinal)),
                $"connectLink left link.ts: {string.Join(", ", results[6])}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Test]
    public async Task DeclarationMapIsACachedOutputAsync()
    {
        var temporaryRoot = DeclarationMapRoot();
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "cached", symbols: true, cache: true,
                fixture: DeclarationMapFixture, generatedSource: DeclarationMapGenerated).ConfigureAwait(false);
            var typescript = Path.Combine(directory, "ts");
            var map = Path.Combine(typescript, "link.contract.d.ts.map");
            var written = File.ReadAllText(map);
            File.Delete(map);
            var (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: true).ConfigureAwait(false);
            Require(exitCode == 0 && !output.Contains("up to date", StringComparison.Ordinal) && File.ReadAllText(map) == written,
                $"A deleted declaration map did not regenerate.\n{output}");
            // The contract and map of a ViewModel that no longer exists are removed; a hand-written map stays.
            File.WriteAllText(Path.Combine(typescript, "gone.contract.d.ts.map"), written.Replace("\"link.contract.d.ts\"", "\"gone.contract.d.ts\"", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(typescript, "gone.contract.d.ts"), "// <auto-generated />\n");
            File.WriteAllText(Path.Combine(typescript, "notes.contract.d.ts.map"), "{\"version\":3,\"file\":\"notes.d.ts\"}");
            (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: false).ConfigureAwait(false);
            Require(exitCode == 0 && !File.Exists(Path.Combine(typescript, "gone.contract.d.ts.map"))
                    && !File.Exists(Path.Combine(typescript, "gone.contract.d.ts")) && File.Exists(Path.Combine(typescript, "notes.contract.d.ts.map")),
                $"A stale contract and map were kept, or a hand-written map was removed.\n{output}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // Inside the workspace, so the clients resolve @runic-artifex/views from its node_modules as an application's do.
    private static string DeclarationMapRoot() => Path.Combine(FindWorkspaceRoot(), "tests", "dotnet", "Runic.Application.Testing.Tests",
        "obj", $"declaration-map-{Guid.NewGuid():N}");

    private static string WithoutMapComment(string text) =>
        Regex.Replace(text, @"//# sourceMappingURL=[^\n]*\n$", "", RegexOptions.CultureInvariant);

    private sealed record MapSegment(int Line, int Column, string? Source, int SourceLine, int SourceColumn);

    // Decodes every segment and checks it as tsserver needs it, since one invalid segment disables the whole map: each
    // contract line has a segment list that starts at column 0; columns increase and stay inside the line; a mapped
    // segment names an existing source line and column and is followed by a generated-only one unless it ends the line.
    private static List<MapSegment> DecodeDeclarationMap(JsonElement map, string contract, string directory)
    {
        Require(map.GetProperty("version").GetInt32() == 3 && map.GetProperty("names").GetArrayLength() == 0, "The map is not a v3 map without names.");
        var sources = map.GetProperty("sources").EnumerateArray().Select(source => source.GetString()!).ToArray();
        var sourceLines = sources.Select(source =>
        {
            Require(!Path.IsPathRooted(source) && !source.Contains('\\', StringComparison.Ordinal), $"The source {source} is not a relative path with forward slashes.");
            return File.ReadAllLines(Path.Combine(directory, source));
        }).ToArray();
        var lines = contract.Split('\n');
        var mappingLines = map.GetProperty("mappings").GetString()!.Split(';');
        Require(mappingLines.Length == lines.Length - 1, $"The map has {mappingLines.Length} lines for {lines.Length - 1} contract lines.");
        var segments = new List<MapSegment>();
        int source = 0, sourceLine = 0, sourceColumn = 0;
        for (var line = 0; line < mappingLines.Length; line++)
        {
            Require(mappingLines[line].Length > 0, $"Line {line} has no segment.");
            var column = 0;
            var previous = -1;
            var openMapping = false;
            foreach (var text in mappingLines[line].Split(','))
            {
                var values = DecodeVlq(text);
                Require(values.Count is 1 or 4, $"Line {line}: segment '{text}' has {values.Count} fields.");
                column += values[0];
                Require(previous >= 0 || column == 0, $"Line {line} does not start with a segment at column 0.");
                Require(column > previous && column <= lines[line].Length, $"Line {line}: column {column} is out of order or outside the line.");
                Require(!openMapping || values.Count == 1, $"Line {line}: a mapped segment is followed by another mapped one.");
                previous = column;
                openMapping = values.Count == 4;
                if (values.Count == 1)
                {
                    segments.Add(new(line, column, null, 0, 0));
                    continue;
                }
                source += values[1];
                sourceLine += values[2];
                sourceColumn += values[3];
                Require(source >= 0 && source < sources.Length && sourceLine >= 0 && sourceLine < sourceLines[source].Length
                        && sourceColumn >= 0 && sourceColumn < sourceLines[source][sourceLine].Length,
                    $"Line {line}: the segment leads outside its source ({source}:{sourceLine}:{sourceColumn}).");
                segments.Add(new(line, column, sources[source], sourceLine, sourceColumn));
            }
        }
        return segments;
    }

    private static List<int> DecodeVlq(string text)
    {
        const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var values = new List<int>();
        int value = 0, shift = 0;
        foreach (var character in text)
        {
            var digit = Base64.IndexOf(character, StringComparison.Ordinal);
            Require(digit >= 0, $"'{text}' is not base64 VLQ.");
            value += (digit & 31) << shift;
            if ((digit & 32) != 0)
            {
                shift += 5;
                continue;
            }
            values.Add((value & 1) != 0 ? -(value >> 1) : value >> 1);
            value = 0;
            shift = 0;
        }
        Require(shift == 0 && values.Count > 0, $"'{text}' ends inside a value.");
        return values;
    }

    // Runs TsServerDefinitions.mjs: the definitions tsserver returns at each "line:offset:command".
    private static async Task<string[][]> TsServerAsync(string root, string file, IEnumerable<string> positions)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("bun")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = root,
            },
        };
        foreach (var argument in new[] { Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "TsServerDefinitions.mjs"),
                     Path.Combine(root, "node_modules", "typescript", "lib", "tsserver.js"), file }.Concat(positions))
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitScaledAsync(GeneratorTimeout).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false);
        Require(process.ExitCode == 0, $"tsserver failed ({process.ExitCode}):\n{text}\n{await error.ConfigureAwait(false)}");
        return JsonSerializer.Deserialize<string[][]>(text.Trim())!;
    }
}
