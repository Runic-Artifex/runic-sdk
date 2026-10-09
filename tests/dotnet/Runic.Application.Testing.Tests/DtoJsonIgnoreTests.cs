using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static partial class CodegenDiagnosticsTests
{
    private const string JsonIgnoreOptIn = "[assembly: RunicBridgeJsonIgnore]\n";

    private static async Task RunDtoJsonIgnoreAsync(string generator, string temporaryRoot)
    {
        var preamble = Preamble.Replace("using System.Text;", "using System.Text;\nusing System.Text.Json.Serialization;", StringComparison.Ordinal);
        var optedInPreamble = preamble.Replace("namespace Fixture;", JsonIgnoreOptIn + "namespace Fixture;", StringComparison.Ordinal);
        const string Computed = """
            public sealed record HistoryQuery(string Message, string Author)
            {
                [JsonIgnore] public bool IsFiltered => Message.Length != 0 || Author.Length != 0;
            }
            public sealed class HistoryViewModel : FixtureModel { public HistoryQuery Query { get; } = new("", ""); }
            public sealed partial class HistoryWindow(HistoryViewModel model) : RunicWindow<HistoryViewModel>(model);
            """;

        var rejected = await GenerateAsync(generator, temporaryRoot, "JsonIgnoreComputedOff", preamble + Computed).ConfigureAwait(false);
        foreach (var text in new[] { "RUNICBRIDGE003", "HistoryViewModel.Query", "[RunicIgnore]", "[assembly: RunicBridgeJsonIgnore]", "Without that opt-in", "WhenWritingNull", "WhenWritingDefault", "Never" })
            Require(rejected.Contains(text, StringComparison.Ordinal), $"Computed DTO diagnostic omitted '{text}'.\n{rejected}");
        var computed = await GenerateValidAsync(generator, temporaryRoot, "JsonIgnoreComputedOn", optedInPreamble + Computed).ConfigureAwait(false);
        Require(!File.ReadAllText(Path.Combine(computed, "Fixture.HistoryViewModel.Bridge.g.cs")).Contains("value.IsFiltered", StringComparison.Ordinal),
            "An opted-in computed DTO property was emitted in its codec.");
        var runicSource = preamble + Computed.Replace("[JsonIgnore]", "[RunicIgnore]", StringComparison.Ordinal);
        var runic = await GenerateValidAsync(generator, temporaryRoot, "RunicIgnoreComputed", runicSource).ConfigureAwait(false);
        Require(File.ReadAllText(Path.Combine(runic, "Fixture.HistoryViewModel.Bridge.g.cs")) == File.ReadAllText(Path.Combine(computed, "Fixture.HistoryViewModel.Bridge.g.cs")),
            "RunicIgnore and opted-in unconditional JsonIgnore produced different DTO contracts.");

        // Off means exactly the previous generated wire shape, even when JSON
        // serialization would omit these properties. Root ViewModel JsonIgnore
        // and RunicIgnore also keep their existing meanings under the opt-in.
        var off = await GenerateValidAsync(generator, temporaryRoot, "JsonIgnoreFieldsOff", preamble + JsonIgnoreFields).ConfigureAwait(false);
        var on = await GenerateValidAsync(generator, temporaryRoot, "JsonIgnoreFieldsOn", optedInPreamble + JsonIgnoreFields).ConfigureAwait(false);
        var baselineSource = preamble + JsonIgnoreFields;
        foreach (var attribute in new[] { "[JsonIgnore]", "[JsonIgnore(Condition = JsonIgnoreCondition.Always)]", "[JsonIgnore(Condition = JsonIgnoreCondition.Never)]", "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]", "[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]" })
            baselineSource = baselineSource.Replace(attribute, "", StringComparison.Ordinal);
        var baseline = await GenerateValidAsync(generator, temporaryRoot, "JsonIgnoreFieldsBaseline", baselineSource).ConfigureAwait(false);
        foreach (var file in new[] { "Fixture.JsonIgnoreViewModel.Bridge.g.cs", "Fixture.JsonIgnoreWindow.View.g.cs" })
            Require(File.ReadAllText(Path.Combine(off, file)) == File.ReadAllText(Path.Combine(baseline, file)),
                $"JsonIgnore changed the default generated {file} contract.");
        var offTypes = File.ReadAllText(Path.Combine(Path.GetDirectoryName(off)!, "ts", "types.ts"));
        var onTypes = File.ReadAllText(Path.Combine(Path.GetDirectoryName(on)!, "ts", "types.ts"));
        Require(offTypes == File.ReadAllText(Path.Combine(Path.GetDirectoryName(baseline)!, "ts", "types.ts")),
            "JsonIgnore changed default generated TypeScript declarations.");
        foreach (var property in new[] { "implicit", "explicit" })
        {
            Require(offTypes.Contains($"readonly {property}: string;", StringComparison.Ordinal), $"Default DTO contract excluded {property}.");
            Require(!onTypes.Contains($"readonly {property}:", StringComparison.Ordinal), $"Opted-in DTO contract included {property}.");
        }
        foreach (var declaration in new[] { "readonly never: string;", "readonly whenNull: string | null;", "readonly whenDefault: number;" })
            Require(onTypes.Contains(declaration, StringComparison.Ordinal), $"Opted-in DTO contract excluded '{declaration}'.");
        Require(!offTypes.Contains("runicExcluded", StringComparison.Ordinal) && !onTypes.Contains("runicExcluded", StringComparison.Ordinal),
            "JsonIgnore Never overrode RunicIgnore.");

        var offAssembly = CompileGeneratedDtoFixture("JsonIgnoreFieldsOff", preamble + JsonIgnoreFields, off, JsonIgnoreProbe);
        var onAssembly = CompileGeneratedDtoFixture("JsonIgnoreFieldsOn", optedInPreamble + JsonIgnoreFields, on, JsonIgnoreProbe);
        var baselineAssembly = CompileGeneratedDtoFixture("JsonIgnoreFieldsBaseline", baselineSource, baseline, JsonIgnoreProbe);
        var offModel = offAssembly.GetType("Fixture.JsonIgnoreViewModel")!;
        var onModel = onAssembly.GetType("Fixture.JsonIgnoreViewModel")!;
        Require(BridgeContractShape.Parts(offModel).SequenceEqual(BridgeContractShape.Parts(baselineAssembly.GetType("Fixture.JsonIgnoreViewModel")!)),
            "JsonIgnore changed default canonical fingerprint parts.");
        Require(BridgeContractShape.Compute(offModel) != BridgeContractShape.Compute(onModel), "Opted-in DTO exclusion did not change its fingerprint.");
        foreach (var (assembly, directory, optedIn) in new[] { (offAssembly, off, false), (onAssembly, on, true) })
        {
            var fingerprint = BridgeContractShape.Compute(assembly.GetType("Fixture.JsonIgnoreViewModel")!);
            var client = File.ReadAllText(Path.Combine(Path.GetDirectoryName(directory)!, "ts", "jsonIgnore.ts"));
            Require(client.Contains($"Fixture.JsonIgnoreViewModel:{fingerprint}", StringComparison.Ordinal),
                "The generated contract fingerprint differs from runtime reflection.");
            using var result = JsonDocument.Parse((string)assembly.GetType("Fixture.JsonIgnoreProbe")!.GetMethod("Run")!.Invoke(null, null)!);
            foreach (var stateName in new[] { "initial", "updated" })
            {
                var state = result.RootElement.GetProperty(stateName).GetProperty("state");
                Require(state.GetProperty("rootOnly").GetString() == "root", "The DTO opt-in excluded a root ViewModel JsonIgnore property.");
                var current = state.GetProperty("current");
                foreach (var value in new[] { current.GetProperty("single"), current.GetProperty("items")[0], current.GetProperty("lookup").GetProperty("key") })
                    AssertJsonIgnoreFields(value, optedIn);
            }
            Require(result.RootElement.GetProperty("updated").GetProperty("state").GetProperty("current").GetProperty("single").GetProperty("plain").GetString() == "updated",
                "A DTO command input did not decode through the generated codec.");
        }

        await VerifyIndependentJsonIgnoreDtosAsync(generator, temporaryRoot, optedInPreamble).ConfigureAwait(false);
    }

    private static void AssertJsonIgnoreFields(JsonElement value, bool optedIn)
    {
        foreach (var property in new[] { "implicit", "explicit" })
            Require(value.TryGetProperty(property, out _) != optedIn, $"Generated DTO writer applied the wrong exclusion to {property}.");
        Require(value.GetProperty("never").GetString() == "included" && value.GetProperty("whenNull").ValueKind == JsonValueKind.Null
            && value.GetProperty("whenDefault").GetInt32() == 0 && !value.TryGetProperty("runicExcluded", out _),
            "Generated DTO writer treated conditional JSON ignores or Never as bridge exclusions.");
    }

    private static Assembly CompileGeneratedDtoFixture(string name, string source, string directory, string probe,
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var compilation = Compile(name, source, additionalReferences: additionalReferences).AddSyntaxTrees(
            Directory.GetFiles(directory, "*.g.cs").Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path),
                new CSharpParseOptions(LanguageVersion.Latest), path)).Append(CSharpSyntaxTree.ParseText(probe,
                new CSharpParseOptions(LanguageVersion.Latest))));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Require(emitted.Success, $"{name}: generated DTO codecs did not compile.\n{string.Join('\n', emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))}");
        return Assembly.Load(image.ToArray());
    }

    private static async Task VerifyIndependentJsonIgnoreDtosAsync(string generator, string temporaryRoot, string optedInPreamble)
    {
        var corePath = Path.Combine(temporaryRoot, "FixtureIndependentJsonIgnoreCore.dll");
        using (var image = File.Create(corePath))
        {
            var emitted = Compile("IndependentJsonIgnoreCore", IndependentJsonIgnoreCore).Emit(image);
            Require(emitted.Success, $"Independent DTO fixture did not compile: {string.Join('\n', emitted.Diagnostics)}");
        }
        var core = Assembly.LoadFrom(corePath);
        Require(core.GetReferencedAssemblies().All(reference => !reference.Name!.StartsWith("Runic.", StringComparison.Ordinal)),
            "The shared DTO fixture acquired a Runic dependency.");
        var references = new[] { MetadataReference.CreateFromFile(corePath) };
        const string Consumer = """
            public sealed class IndependentViewModel : FixtureModel
            {
                public SharedCore.HistoryQuery Query { get; set; } = new();
                public List<SharedCore.HistoryQuery> Queries { get; } = [new()];
                public Dictionary<string, SharedCore.HistoryQuery> Lookup { get; } = new() { ["key"] = new() };
                [RunicAlias("validationDto")] public SharedCore.ValidationDto Validation { get; } = new();
            }
            public sealed partial class IndependentWindow(IndependentViewModel model) : RunicWindow<IndependentViewModel>(model);
            """;
        var source = optedInPreamble + Consumer;
        var (exit, output, directory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "IndependentJsonIgnoreConsumer", source, references).ConfigureAwait(false);
        Require(exit == 0, $"Independent shared DTOs were not accepted.\n{output}");
        var cs = Path.Combine(directory, "cs");
        var generated = File.ReadAllText(Path.Combine(cs, "Fixture.IndependentViewModel.Bridge.g.cs"));
        foreach (var member in new[] { ".IsFiltered", ".HasErrors", ".Hidden" })
            Require(!generated.Contains(member, StringComparison.Ordinal), $"The shared DTO codec or validation metadata read ignored member {member}.");
        var assembly = CompileGeneratedDtoFixture("IndependentJsonIgnoreConsumer", source, cs, IndependentJsonIgnoreProbe, references);
        var fingerprint = BridgeContractShape.Compute(assembly.GetType("Fixture.IndependentViewModel")!);
        Require(File.ReadAllText(Path.Combine(directory, "ts", "independent.ts")).Contains(fingerprint, StringComparison.Ordinal),
            "Independent nested DTO fingerprints disagree between generator and runtime.");
        using var result = JsonDocument.Parse((string)assembly.GetType("Fixture.IndependentJsonIgnoreProbe")!.GetMethod("Run")!.Invoke(null, null)!);
        var state = result.RootElement.GetProperty("state");
        foreach (var query in new[] { state.GetProperty("query"), state.GetProperty("queries")[0], state.GetProperty("lookup").GetProperty("key") })
            Require(query.EnumerateObject().Select(property => property.Name).SequenceEqual(["message", "author"]),
                "An independent nested DTO included its computed JsonIgnore property.");
        var errors = state.GetProperty("validation").GetProperty("errors");
        Require(errors.GetArrayLength() == 1 && errors[0].GetProperty("message").GetString() == "visible error"
            && errors[0].GetProperty("path").EnumerateArray().Select(segment => segment.GetString()).SequenceEqual(["validationDto", "value"]),
            "Generated validation traversed a DTO property excluded by JsonIgnore.");
        await VerifyMixedJsonIgnorePoliciesAsync(generator, temporaryRoot, optedInPreamble, references).ConfigureAwait(false);
    }

    private static async Task VerifyMixedJsonIgnorePoliciesAsync(string generator, string temporaryRoot,
        string optedInPreamble, MetadataReference[] coreReferences)
    {
        const string DefaultModel = """
            using System.ComponentModel;
            namespace SharedModels;
            public sealed class MixedDefaultViewModel : INotifyPropertyChanged
            {
                public SharedCore.PolicyDto Value { get; } = new();
                public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
            }
            """;
        var modelPath = Path.Combine(temporaryRoot, "FixtureMixedJsonIgnoreModels.dll");
        using (var image = File.Create(modelPath))
        {
            var emitted = Compile("MixedJsonIgnoreModels", DefaultModel, additionalReferences: coreReferences).Emit(image);
            Require(emitted.Success, $"Mixed-policy model fixture did not compile: {string.Join('\n', emitted.Diagnostics)}");
        }
        _ = Assembly.LoadFrom(modelPath);
        MetadataReference[] references = [.. coreReferences, MetadataReference.CreateFromFile(modelPath)];
        var source = optedInPreamble + """
            public sealed class MixedJsonIgnoreViewModel : FixtureModel { public SharedCore.PolicyDto Value { get; } = new(); }
            public sealed partial class MixedJsonIgnoreWindow(MixedJsonIgnoreViewModel model) : RunicWindow<MixedJsonIgnoreViewModel>(model);
            public sealed partial class MixedDefaultWindow(SharedModels.MixedDefaultViewModel model) : RunicWindow<SharedModels.MixedDefaultViewModel>(model);
            """;
        var (exit, output, directory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "MixedJsonIgnoreConsumer", source, references).ConfigureAwait(false);
        Require(exit == 0, $"Mixed-policy ViewModels were rejected.\n{output}");
        var cs = Path.Combine(directory, "cs");
        var assembly = CompileGeneratedDtoFixture("MixedJsonIgnoreConsumer", source, cs, MixedJsonIgnoreProbe, references);
        var types = File.ReadAllText(Path.Combine(directory, "ts", "types.ts"));
        foreach (var (typeName, module, includeIgnored) in new[]
        {
            ("SharedModels.MixedDefaultViewModel", "mixedDefault", true),
            ("Fixture.MixedJsonIgnoreViewModel", "mixedJsonIgnore", false),
        })
        {
            var model = assembly.GetType(typeName) ?? Assembly.LoadFrom(modelPath).GetType(typeName)!;
            var client = File.ReadAllText(Path.Combine(directory, "ts", module + ".ts"));
            Require(client.Contains(BridgeContractShape.Compute(model), StringComparison.Ordinal), "A mixed-policy fingerprint disagrees with runtime reflection.");
            var dtoName = System.Text.RegularExpressions.Regex.Match(client, @"readonly value: ([A-Za-z0-9_]+);").Groups[1].Value;
            Require(dtoName.Length > 0, "The mixed-policy client omitted its named DTO.");
            var declaration = System.Text.RegularExpressions.Regex.Match(types, @"export interface " + dtoName + @" \{([^}]+)\}").Groups[1].Value;
            Require(declaration.Contains("readonly included: string;", StringComparison.Ordinal)
                && declaration.Contains("readonly ignored: string;", StringComparison.Ordinal) == includeIgnored,
                "Named TypeScript declarations shared DTO shapes across different ViewModel exclusion policies.");
        }
        using var result = JsonDocument.Parse((string)assembly.GetType("Fixture.MixedJsonIgnoreProbe")!.GetMethod("Run")!.Invoke(null, null)!);
        Require(result.RootElement.GetProperty("defaultState").GetProperty("state").GetProperty("value").TryGetProperty("ignored", out _)
            && !result.RootElement.GetProperty("optedInState").GetProperty("state").GetProperty("value").TryGetProperty("ignored", out _),
            "A Window's assembly policy overrode its external ViewModel's policy.");
    }

    private const string JsonIgnoreFields = """
        public sealed class JsonIgnoreFieldDto
        {
            public string Plain { get; set; } = "plain";
            [JsonIgnore] public string Implicit { get; set; } = "hidden";
            [JsonIgnore(Condition = JsonIgnoreCondition.Always)] public string Explicit { get; set; } = "hidden";
            [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string Never { get; set; } = "included";
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? WhenNull { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int WhenDefault { get; set; }
            [RunicIgnore] [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public object RunicExcluded { get; } = new();
        }
        public sealed class JsonIgnoreContainerDto
        {
            public JsonIgnoreFieldDto Single { get; set; } = new();
            public JsonIgnoreFieldDto[] Items { get; set; } = [new()];
            public Dictionary<string, JsonIgnoreFieldDto> Lookup { get; set; } = new() { ["key"] = new() };
        }
        public sealed class JsonIgnoreViewModel : FixtureModel
        {
            public JsonIgnoreContainerDto Current { get; } = new();
            [JsonIgnore] public string RootOnly => "root";
            [RunicIgnore] public object RootExcluded { get; } = new();
            [RunicFailure(typeof(JsonIgnoreFieldDto))]
            public IRelayCommand<JsonIgnoreFieldDto> ApplyCommand { get; }
            public ReactiveUI.ReactiveCommand<JsonIgnoreFieldDto, JsonIgnoreFieldDto> EchoCommand { get; } = ReactiveUI.ReactiveCommand.Create<JsonIgnoreFieldDto, JsonIgnoreFieldDto>(input => input);
            public ReactiveUI.Binding.Interaction<JsonIgnoreFieldDto, JsonIgnoreFieldDto> Confirm { get; } = new();
            public JsonIgnoreViewModel() => ApplyCommand = new RelayCommand<JsonIgnoreFieldDto>(input => Current.Single = input!);
        }
        public sealed partial class JsonIgnoreWindow(JsonIgnoreViewModel model) : RunicWindow<JsonIgnoreViewModel>(model);
        """;

    private const string JsonIgnoreProbe = """"
        namespace Fixture;
        public static class JsonIgnoreProbe
        {
            public static string Run()
            {
                using var model = new DisposableModel();
                using var host = new Runic.Application.Testing.RunicWindowTestHost<JsonIgnoreViewModel>(model.Value, "jsonIgnore",
                    (transport, content, vm) => new JsonIgnoreBridge(transport, vm, content: content));
                using var initial = host.Snapshot();
                _ = host.Transport.Call("jsonIgnoreApply", new(StringValue: """{"plain":"updated","implicit":"hidden","explicit":"hidden","never":"included","whenNull":null,"whenDefault":0}"""));
                using var updated = host.Snapshot();
                return System.Text.Json.JsonSerializer.Serialize(new { initial = initial.RootElement, updated = updated.RootElement });
            }
            private sealed class DisposableModel : System.IDisposable
            {
                internal JsonIgnoreViewModel Value { get; } = new();
                public void Dispose() => Value.EchoCommand.Dispose();
            }
        }
        """";

    private const string IndependentJsonIgnoreCore = """
        using System;
        using System.Collections;
        using System.ComponentModel;
        using System.Text.Json.Serialization;
        namespace SharedCore;
        public sealed record HistoryQuery(
            [property: JsonPropertyName("message")] string Message = "",
            [property: JsonPropertyName("author")] string Author = "")
        {
            [JsonIgnore] public bool IsFiltered => Message.Length != 0 || Author.Length != 0;
        }
        public sealed class ValidationDto : INotifyDataErrorInfo
        {
            public string Value { get; set; } = "";
            [JsonIgnore] public bool HasErrors => true;
            [JsonIgnore] public ValidationDto Hidden => throw new InvalidOperationException("Ignored getters must never run.");
            public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
            public IEnumerable GetErrors(string? propertyName) => propertyName switch
            {
                nameof(Value) => new[] { "visible error" },
                nameof(Hidden) or nameof(HasErrors) => new[] { "hidden error" },
                _ => Array.Empty<string>(),
            };
        }
        public sealed class PolicyDto
        {
            public string Included { get; set; } = "included";
            [JsonIgnore] public string Ignored { get; set; } = "ignored";
        }
        """;

    private const string IndependentJsonIgnoreProbe = """
        namespace Fixture;
        public static class IndependentJsonIgnoreProbe
        {
            public static string Run()
            {
                var model = new IndependentViewModel();
                using var host = new Runic.Application.Testing.RunicWindowTestHost<IndependentViewModel>(model, "independent",
                    (transport, content, vm) => new IndependentBridge(transport, vm, content: content));
                using var snapshot = host.Snapshot();
                return snapshot.RootElement.GetRawText();
            }
        }
        """;

    private const string MixedJsonIgnoreProbe = """
        namespace Fixture;
        public static class MixedJsonIgnoreProbe
        {
            public static string Run()
            {
                using var defaultHost = new Runic.Application.Testing.RunicWindowTestHost<SharedModels.MixedDefaultViewModel>(new(), "mixedDefault",
                    (transport, content, vm) => new SharedModels.MixedDefaultBridge(transport, vm, content: content));
                using var optedInHost = new Runic.Application.Testing.RunicWindowTestHost<MixedJsonIgnoreViewModel>(new(), "mixedJsonIgnore",
                    (transport, content, vm) => new MixedJsonIgnoreBridge(transport, vm, content: content));
                using var defaultState = defaultHost.Snapshot();
                using var optedInState = optedInHost.Snapshot();
                return System.Text.Json.JsonSerializer.Serialize(new { defaultState = defaultState.RootElement, optedInState = optedInState.RootElement });
            }
        }
        """;
}
