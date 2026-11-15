using Microsoft.CodeAnalysis;
using TUnit.Core;
using static Runic.Application.Views.Analyzers.Tests.AnalyzerHarness;

namespace Runic.Application.Views.Analyzers.Tests;

// The value graph (RUNICBRIDGE003, 065), the rows and keys of keyed and query collections (010, 060), the row editors
// (050, 051, 052) and the warnings that follow from values (031, 032, 053), each with the generator's message
// (tools/Runic.Application.Views.Codegen BridgeTypeGraph and GenerateOne).
public sealed class BridgeValueAnalyzerTests
{
    private const string Types = """
        public sealed class Node { public Node? Next { get; set; } }
        public sealed class Empty { }
        [System.Flags] public enum Access { None = 0, Read = 1, Write = 2 }
        public sealed class NoConstructor { public NoConstructor(int seed) { } public string Name { get; } = ""; }
        public sealed class Holder { public System.Uri Link { get; } = null!; }
        public sealed class Twins { [RunicAlias("a")] public int A { get; } [RunicAlias("a")] public int B { get; } }
        [RunicUnion(typeof(Circle))] public class Shape { }
        [RunicUnionCase("circle")] public sealed class Circle : Shape { public double Radius { get; } }
        public sealed class PlainCodec { }
        [RunicCodec(typeof(PlainCodec))] public sealed class Coded { public int Value { get; } }
        public sealed record CodeRow(string? Code, string Name);
        public sealed record Keyed<T>(T Id, string Name);
        public class OpenRow { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
        public class Detail { public string Note { get; set; } = ""; }
        public sealed record DeepRow(string Id, Detail Detail);
        """;

    [Test]
    [Arguments("public object? Anything { get; set; }", "RUNICBRIDGE003",
        "MainViewModel.Anything: System.Object is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.")]
    [Arguments("public HashSet<string> Tags { get; } = [];", "RUNICBRIDGE003",
        "MainViewModel.Tags: System.Collections.Generic.HashSet`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] is not a supported bridge collection. Use T[], List<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, IList<T>, ICollection<T>, IEnumerable<T>, ImmutableArray<T>, ImmutableList<T>, ObservableCollection<T>, ReadOnlyObservableCollection<T>, or ReadOnlyCollection<T>.")]
    [Arguments("public Dictionary<int, string> Map { get; } = new();", "RUNICBRIDGE003",
        "MainViewModel.Map: System.Collections.Generic.Dictionary`2[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e],[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]] is not a supported bridge dictionary. Use Dictionary<string, T>, IDictionary<string, T>, or IReadOnlyDictionary<string, T>.")]
    [Arguments("public int[,] Grid { get; } = new int[1, 1];", "RUNICBRIDGE003",
        "MainViewModel.Grid: Multi-dimensional arrays are not a bridge contract; use a jagged array or a list.")]
    [Arguments("public Empty Nothing { get; } = new();", "RUNICBRIDGE003",
        "MainViewModel.Nothing: App.Empty has no public readable properties, so it would always serialize as {}. Add properties or a RunicCodec.")]
    [Arguments("public Access Rights { get; }", "RUNICBRIDGE003", "MainViewModel.Rights: Flags enums need an explicit combination codec.")]
    [Arguments("public NoConstructor Value { get; set; } = null!;", "RUNICBRIDGE003",
        "MainViewModel.Value: App.NoConstructor is read from the page, as a command input, an interaction result or a settable property, so it needs a public constructor whose parameters map to every required property, or a public parameterless constructor with public setters. Exclude computed or .NET-only DTO properties with [RunicIgnore] or an unconditional [JsonIgnore]. [assembly: RunicIncludeJsonIgnored] in the presentation/ViewModel assembly keeps [JsonIgnore] properties in the contract. JsonIgnore conditions Never, WhenWritingNull and WhenWritingDefault remain included in the Bridge contract.")]
    [Arguments("public IRelayCommand<RunicBlob> UploadCommand { get; } = null!;", "RUNICBRIDGE065",
        "MainViewModel.UploadCommand.input: A RunicBlob only goes to the page: it cannot be a command input or an interaction result. Take a RunicUpload, which the page creates with client.upload(file).")]
    [Arguments("public IRelayCommand<object> RunCommand { get; } = null!;", "RUNICBRIDGE003",
        "MainViewModel.RunCommand.input: System.Object is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.")]
    [Arguments("public ViewQuestion<object, bool> Ask { get; } = null!;", "RUNICBRIDGE003",
        "MainViewModel.Ask.input: System.Object is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.")]
    [Arguments("public ViewRequest<Empty> Focus { get; } = null!;", "RUNICBRIDGE003",
        "MainViewModel.Focus.input: App.Empty has no public readable properties, so it would always serialize as {}. Add properties or a RunicCodec.")]
    [Arguments("public Shape Figure { get; } = null!;", "RUNICBRIDGE003", "MainViewModel.Figure: A bridge union root must be an abstract class or interface.")]
    [Arguments("public Coded Value { get; } = new();", "RUNICBRIDGE003", "MainViewModel.Value: global::App.PlainCodec must implement IRunicCodec<global::App.Coded>.")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row?> Rows { get; } = [];", "RUNICBRIDGE010",
        "MainViewModel.Rows: RunicCollection requires a nonnullable, read-only collection of nonnullable DTO rows.")]
    [Arguments("[RunicCollection(\"Code\")] public ObservableCollection<Row> Rows { get; } = [];", "RUNICBRIDGE010",
        "MainViewModel.Rows: the collection key 'Code' must name a nonnullable string, Guid, Int32 or Int64 row property.")]
    [Arguments("[RunicCollection(nameof(CodeRow.Code))] public ObservableCollection<CodeRow> Rows { get; } = [];", "RUNICBRIDGE010",
        "MainViewModel.Rows: the collection key 'Code' must name a nonnullable string, Guid, Int32 or Int64 row property.")]
    // The key of a generic row takes its nullability from the use site, as the generator reads it.
    [Arguments("[RunicCollection(\"Id\")] public ObservableCollection<Keyed<string?>> Rows { get; } = [];", "RUNICBRIDGE010",
        "MainViewModel.Rows: the collection key 'Id' must name a nonnullable string, Guid, Int32 or Int64 row property.")]
    [Arguments("public RunicQueryCollection<int, string> Numbers { get; } = null!;", "RUNICBRIDGE060",
        "MainViewModel.Numbers: the rows of a RunicQueryCollection are DTOs, not Int32.")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; } = []; [RunicRowEditor] public void EditRow(string key, CodeRow row) { }",
        "RUNICBRIDGE050", "MainViewModel.EditRow: [RunicRowEditor] matches no [RunicCollection] property by its row type.")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; } = []; [RunicRowEditor] public int EditRow(string key, Row row) => 0;",
        "RUNICBRIDGE050", "MainViewModel.EditRow: a [RunicRowEditor] for Rows must be a public or internal instance method void EditRow(global::System.String key, global::App.Row row), optionally with a third parameter RunicRowEdit edit.")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; } = []; [RunicRowEditor] public void EditA(string key, Row row) { } [RunicRowEditor] public void EditB(string key, Row row) { }",
        "RUNICBRIDGE051", "MainViewModel: Rows has two [RunicRowEditor] methods, EditA and EditB.")]
    [Arguments("[RunicCollection(nameof(OpenRow.Id))] public ObservableCollection<OpenRow> Rows { get; } = []; [RunicRowEditor] public void EditRow(string key, OpenRow row) { }",
        "RUNICBRIDGE052", "MainViewModel.EditRow: the rows of Rows cannot be copied with one cell changed: global::App.OpenRow is a class that is not sealed, so a derived row would lose its own members. Make global::App.OpenRow a record, or seal it and give it a copy constructor or a Clone() method.")]
    [Arguments("public RunicQueryCollection<Row, string> Rows { get; } = null!; [RunicRowEditor(nameof(Rows))] public void EditRow(string key, Row row) { }",
        "RUNICBRIDGE050", "MainViewModel.EditRow: [RunicRowEditor] names Rows, a query collection, whose rows cannot be edited from the page yet.")]
    public async Task ReportsTheGeneratorsErrorAtTheMember(string members, string id, string message)
    {
        var diagnostics = await AnalyzeAsync(AppSource(members, Types));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == id && Message(diagnostics[0]) == message
            && diagnostics[0].Severity == DiagnosticSeverity.Error, $"{members}:{Environment.NewLine}{Describe(diagnostics)}");
        var at = diagnostics[0].Location.GetLineSpan();
        Require(at.Path == "App.cs" && at.StartLinePosition.Line == 15, $"{members}: reported at {at}");
        Require(diagnostics[0].Descriptor.HelpLinkUri.EndsWith("#" + id.ToLowerInvariant(), StringComparison.Ordinal), diagnostics[0].Descriptor.HelpLinkUri);
    }

    // A failure inside a DTO is reported at the DTO's member, with the path from the ViewModel.
    [Test]
    [Arguments("public Holder Box { get; } = new();", "MainViewModel.Box.link: System.Uri is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.", "public System.Uri Link")]
    [Arguments("public Twins Pair { get; } = new();", "MainViewModel.Pair.a: Wire name collides between Twins.A and Twins.B; use RunicAlias or RunicIgnore.", "public Twins Pair")]
    [Arguments("public Node Head { get; } = null!;", "MainViewModel.Head.next: Recursive DTO graphs are not a bridge contract.", "public Node? Next")]
    public async Task ReportsAFailureInsideADtoAtItsMember(string members, string message, string declaration)
    {
        var diagnostics = await AnalyzeAsync(AppSource(members, Types));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE003" && Message(diagnostics[0]) == message, Describe(diagnostics));
        var line = diagnostics[0].Location.SourceTree!.GetText().Lines[diagnostics[0].Location.GetLineSpan().StartLinePosition.Line].ToString();
        Require(line.Contains(declaration, StringComparison.Ordinal), $"reported at: {line}");
    }

    [Test]
    [Arguments("public Dictionary<string, Row> Map { get; } = new();")]
    [Arguments("public System.Collections.Immutable.ImmutableArray<int> Values { get; }")]
    [Arguments("public System.Text.Json.JsonElement Payload { get; }")]
    [Arguments("public int? Maybe { get; set; }")]
    [Arguments("public Keyed<string> Item { get; set; } = null!;")]
    [Arguments("public IReadOnlyList<Row> Rows { get; set; } = [];")]
    [Arguments("public IAsyncRelayCommand<Row> SaveCommand { get; } = null!;")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; } = []; [RunicRowEditor] public void EditRow(string key, Row row) { }")]
    [Arguments("[RunicCollection(\"Id\")] public ObservableCollection<Keyed<string>> Rows { get; } = [];")]
    [Arguments("public RunicQueryCollection<Row, string> Rows { get; } = null!;")]
    [Arguments("#nullable disable\n    [RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; } = [];\n#nullable enable")]
    public async Task AcceptsWhatTheGeneratorAccepts(string members)
    {
        var diagnostics = await AnalyzeAsync(AppSource(members, Types));
        Require(diagnostics.IsEmpty, $"{members}:{Environment.NewLine}{Describe(diagnostics)}");
    }

    [Test]
    public async Task WarnsAboutAnObservableCollectionWithoutAKeyAndDeclaresIt()
    {
        var document = Document(AppSource("public ObservableCollection<Row> Items { get; } = [];", Types));
        var diagnostics = await AnalyzeAsync(document);
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE032" && diagnostics[0].Severity == DiagnosticSeverity.Warning
            && Message(diagnostics[0]) == "MainViewModel.Items: an observable collection of Row rows that is not a [RunicCollection] publishes every change by encoding and sending the whole list. Declare [RunicCollection(nameof(Row.Id))] to publish row changes, or suppress RUNICBRIDGE032 with NoWarn for a list that stays small.",
            Describe(diagnostics));
        var (text, fixedDocument, title) = await FixAsync(document);
        Require(title == "Declare [RunicCollection(nameof(Row.Id))]"
            && System.Text.RegularExpressions.Regex.IsMatch(text, @"\[RunicCollection\(nameof\(Row\.Id\)\)\]\s+public ObservableCollection<Row> Items \{ get; \} = \[\];"), text);
        var remaining = await AnalyzeAsync(fixedDocument);
        Require(remaining.IsEmpty, Describe(remaining));
    }

    [Test]
    public async Task WarnsAboutAnObjectInAnEditableRowThatCannotBeCopied()
    {
        var diagnostics = await AnalyzeAsync(AppSource(
            "[RunicCollection(nameof(DeepRow.Id))] public ObservableCollection<DeepRow> Rows { get; } = []; [RunicRowEditor] public void EditRow(string key, DeepRow row) { }", Types));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE053" && diagnostics[0].Severity == DiagnosticSeverity.Warning
            && Message(diagnostics[0]) == "MainViewModel.Rows: DeepRow.Detail has no fields below it: global::App.Detail is a class that is not sealed, so a derived row would lose its own members. Mark it [RunicNoField], make its type a record, or seal it and give it a copy constructor or a Clone() method.",
            Describe(diagnostics));
    }

    // ReactiveUI.Validation's ValidationContext is no state: the validation projection represents it.
    [Test]
    public async Task LeavesOutTheValidationContext()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            namespace ReactiveUI.Validation.Contexts { public interface IValidationContext; }
            namespace ReactiveUI.Validation.Abstractions { public interface IValidatableViewModel { ReactiveUI.Validation.Contexts.IValidationContext ValidationContext { get; } } }
            namespace App
            {
                public sealed class FormViewModel : ReactiveUI.Validation.Abstractions.IValidatableViewModel, INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    public ReactiveUI.Validation.Contexts.IValidationContext ValidationContext { get; } = null!;
                    public int Value { get; }
                }
                public sealed partial class FormView : RunicView<FormViewModel>;
            }
            """).Replace("namespace App;", "namespace App {", StringComparison.Ordinal)
            .Replace("public sealed record Row(string Id, string Name);", "public sealed record Row(string Id, string Name); }", StringComparison.Ordinal));
        Require(diagnostics.IsEmpty, Describe(diagnostics));
    }

    [Test]
    public async Task WarnsAboutAnUnrecognizedValidatableViewModel()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            public interface IValidatableViewModel;
            public sealed class FormViewModel : IValidatableViewModel, INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                public int Value { get; }
            }
            public sealed partial class FormView : RunicView<FormViewModel>;
            """));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE031" && diagnostics[0].Severity == DiagnosticSeverity.Warning
            && Message(diagnostics[0]) == "FormViewModel: App.IValidatableViewModel is not the IValidatableViewModel of RunicArtifex.ReactiveUI.Validation or RunicArtifex.ReactiveUI.Validation.Reactive, so the Bridge does not read or observe its ValidationContext. Implement INotifyDataErrorInfo to report its errors.",
            Describe(diagnostics));
    }
}
