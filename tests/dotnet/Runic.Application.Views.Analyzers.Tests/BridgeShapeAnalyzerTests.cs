using TUnit.Core;
using static Runic.Application.Views.Analyzers.Tests.AnalyzerHarness;

namespace Runic.Application.Views.Analyzers.Tests;

// The RUNICBRIDGE rules that BridgeShapeAnalyzer reports while you type, each with the generator's ID and message for
// the same member (tools/Runic.Application.Views.Codegen), and the shapes the generator accepts, which it never reports.
public sealed class BridgeShapeAnalyzerTests
{
    [Test]
    [Arguments("public ICommand Save { get; } = null!;",
        "RUNICBRIDGE009", "MainViewModel.Save: Bridge commands must end with Command.")]
    [Arguments("public IRelayCommand NewCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel.NewCommand: the generated client member 'new' declares a construct signature in a TypeScript object type, so the generated client does not compile. Rename the command, for example to CreateCommand (client member 'create').")]
    [Arguments("[RunicCommandInput(typeof(string))] public ICommand ThenCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel.ThenCommand: the generated client member 'then' makes the connected client thenable, so 'await connect…()' calls it instead of resolving to the client. Rename the command, for example to ContinueCommand (client member 'continue').")]
    [Arguments("public IRelayCommand ToStringCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel.ToStringCommand: the generated client member 'toString' replaces the client's string conversion. Rename the command, for example to FormatCommand (client member 'format').")]
    [Arguments("public ICommand ResetCommand { get; } = null!;",
        "RUNICBRIDGE009", "MainViewModel.ResetCommand: unsupported command shape. Use a CommunityToolkit IRelayCommand or IAsyncRelayCommand, a ReactiveUI ReactiveCommand, or an ICommand with RunicCommandInput or RunicCommandKey.")]
    [Arguments("[RunicCommandResult(RunicCommandResultCardinality.Stream)] public IRelayCommand ResetCommand { get; } = null!;",
        "RUNICBRIDGE009", "MainViewModel.ResetCommand: RunicCommandResult selects a ReactiveUI command's result cardinality; a CommunityToolkit command has the single result of its Task<T> method, and a plain command has none.")]
    [Arguments("public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; set; } = null!;",
        "RUNICBRIDGE011", "MainViewModel.Confirm: interactions are application-owned and cannot have a public setter.")]
    [Arguments("public ViewRequest<string> Focus { get; set; } = null!;",
        "RUNICBRIDGE035", "MainViewModel.Focus: a view request is application-owned and cannot have a public setter. Expose it as a get-only property, such as { get; } = new(...).")]
    [Arguments("public ViewRequest<string> Focus { private get; set; } = null!;",
        "RUNICBRIDGE035", "MainViewModel.Focus: a view request needs a public getter. Expose it as a get-only property, such as { get; } = new(...).")]
    [Arguments("public ViewQuestion<string, bool> Ask { get; init; } = null!;",
        "RUNICBRIDGE035", "MainViewModel.Ask: a view question is application-owned and cannot have a public setter. Expose it as a get-only property, such as { get; } = new(...).")]
    [Arguments("public NavigationRegion<IPage> Content { get; set; } = null!;",
        "RUNICBRIDGE008", "MainViewModel.Content: a NavigationRegion slot must be get-only; its navigator, not the web view, changes the content.")]
    [Arguments("public NavigationRegion<object> Content { get; } = null!;",
        "RUNICBRIDGE008", "MainViewModel.Content: a NavigationRegion<object> slot has no known content. Use an interface or base class that the region's ViewModels share.")]
    [Arguments("public NavigationSelection Tabs { get; set; } = null!;",
        "RUNICBRIDGE008", "MainViewModel.Tabs: a NavigationSelection slot must be get-only; its SelectAsync, not the web view, changes the selection. Select through a command.")]
    [Arguments("public IPage? Page { get; set; }",
        "RUNICBRIDGE008", "MainViewModel.Page: ViewModel content must be set by .NET, not the web view.")]
    [Arguments("public IReadOnlyList<IPage> Pages { get; set; } = [];",
        "RUNICBRIDGE008", "MainViewModel.Pages: ViewModel collections must be set by .NET, not the web view.")]
    [Arguments("[RunicAppendOnlyText] public string Log { get; set; } = \"\";",
        "RUNICBRIDGE008", "MainViewModel.Log: RunicAppendOnlyText requires a read-only string state property.")]
    [Arguments("[RunicAppendOnlyText] public int Log { get; }",
        "RUNICBRIDGE008", "MainViewModel.Log: RunicAppendOnlyText requires a read-only string state property.")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; set; } = [];",
        "RUNICBRIDGE010", "MainViewModel.Rows: RunicCollection requires a nonnullable, read-only collection of nonnullable DTO rows.")]
    [Arguments("[RunicRegionCollection(typeof(IPage))] public NavigationSelection Documents { get; set; } = null!;",
        "RUNICBRIDGE070", "MainViewModel.Documents: a region collection must be get-only; its Add, SelectAsync and RemoveAsync, not the web view, change it.")]
    [Arguments("[RunicRegionCollection(typeof(IPage))] public NavigationRegion<IPage> Documents { get; } = null!;",
        "RUNICBRIDGE070", "MainViewModel.Documents: RunicRegionCollection applies to a NavigationSelection property, not to a NavigationRegion`1.")]
    [Arguments("[RunicRegionCollection(typeof(object))] public NavigationSelection Documents { get; } = null!;",
        "RUNICBRIDGE070", "MainViewModel.Documents: a region collection of object has no known content. Name an interface or base class that the regions' ViewModels share.")]
    [Arguments("public RunicQueryCollection<Row, string> Rows { get; set; } = null!;",
        "RUNICBRIDGE060", "MainViewModel.Rows: a RunicQueryCollection must be get-only; the page changes its query with setQuery.")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public RunicQueryCollection<Row, string> Rows { get; } = null!;",
        "RUNICBRIDGE060", "MainViewModel.Rows: a RunicQueryCollection has its own keys; remove [RunicCollection] or [RunicAppendOnlyText].")]
    [Arguments("public RunicQueryCollection<Row, long> Rows { get; } = null!;",
        "RUNICBRIDGE060", "MainViewModel.Rows: a RunicQueryCollection key is a string, Guid or Int32, not Int64.")]
    // object is no Bridge value (#962 part 4: the value graph).
    [Arguments("public object? Anything { get; set; }",
        "RUNICBRIDGE003", "MainViewModel.Anything: System.Object is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.")]
    [Arguments("[RunicFailure(typeof(string))] public int Total { get; }",
        "RUNICBRIDGE012", "MainViewModel.Total: RunicFailure applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method.")]
    [Arguments("[RunicCommandKey(nameof(Find))] public static Row? Find(string id) => null;",
        "RUNICBRIDGE027", "MainViewModel.Find: RunicCommandKey applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method.")]
    public async Task ReportsTheGeneratorsErrorAtTheMember(string member, string id, string message)
    {
        var diagnostics = await AnalyzeAsync(AppSource(member));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == id && Message(diagnostics[0]) == message
            && diagnostics[0].Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error,
            $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
        var line = diagnostics[0].Location.GetLineSpan();
        Require(line.Path == "App.cs" && line.StartLinePosition.Line == 15, $"{member}: reported at {line}");
        Require(diagnostics[0].Descriptor.HelpLinkUri.EndsWith("#" + id.ToLowerInvariant(), StringComparison.Ordinal),
            diagnostics[0].Descriptor.HelpLinkUri);
    }

    [Test]
    [Arguments("public IRelayCommand SaveCommand { get; } = null!;")]
    [Arguments("public IRelayCommand NewItemCommand { get; } = null!;")]
    [Arguments("[RunicCommandInput(typeof(Row))] public ICommand SaveCommand { get; } = null!;")]
    [Arguments("[RunicCommandKey(nameof(Find))] public ICommand OpenCommand { get; } = null!; public Row? Find(string id) => null;")]
    [Arguments("public ReactiveUI.ReactiveCommand<string, bool> SaveCommand { get; } = null!;")]
    [Arguments("[RunicIgnore] public ICommand Save { get; } = null!;")]
    [Arguments("public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; } = new();")]
    [Arguments("public ViewRequest<string> Focus { get; } = new();")]
    [Arguments("public ViewQuestion<string, bool> Ask { get; private set; } = null!;")]
    [Arguments("public NavigationRegion<IPage> Content { get; } = null!;")]
    [Arguments("public NavigationSelection Tabs { get; } = null!;")]
    [Arguments("public IPage? Page { get; private set; }")]
    [Arguments("public IReadOnlyList<IPage> Pages { get; private set; } = [];")]
    [Arguments("public IReadOnlyList<Row> Rows { get; set; } = [];")]
    [Arguments("[RunicAppendOnlyText] public string Log { get; private set; } = \"\";")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; } = [];")]
    [Arguments("[RunicRegionCollection(typeof(IPage))] public NavigationSelection Documents { get; } = null!;")]
    [Arguments("public RunicQueryCollection<Row, System.Guid> Rows { get; } = null!;")]
    [Arguments("public IRelayCommand SaveCommand { get; } = null!; [RunicFailure(typeof(string))] public IRelayCommand LoadCommand { get; } = null!;")]
    [Arguments("[RunicIgnore] public NavigationSelection Tabs { get; set; } = null!; private ICommand Hidden { get; } = null!;")]
    // #1180: an async command's IProgress<double> parameter is bound to the operation, not a Bridge value from the page.
    [Arguments("public IAsyncRelayCommand<System.IProgress<double>> ImportCommand { get; } = null!;")]
    public async Task AcceptsWhatTheGeneratorAccepts(string member)
    {
        var diagnostics = await AnalyzeAsync(AppSource(member));
        Require(diagnostics.IsEmpty, $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
    }

    // #1320: in explicit publication the analyzer checks only the members the generator publishes: [RunicInclude] ones and
    // those implementing a [RunicSharedClient] interface. The class's declaration wins over the assembly's.
    private const string UnpublishedMembers = """
            [RunicInclude] public string Title { get; set; } = "";
            public HashSet<string> Tags { get; } = [];
            public IRelayCommand NewCommand { get; } = null!;
        """;

    private static string Publication(string onClass, string onAssembly, string types = "", string interfaces = "") =>
        AppSource(UnpublishedMembers, types)
            .Replace("namespace App;", onAssembly + "\nnamespace App;", StringComparison.Ordinal)
            // The harness's PageViewModel publishes its Title in either mode.
            .Replace("public string Title { get; } = \"\";", "[RunicInclude] public string Title { get; } = \"\";", StringComparison.Ordinal)
            .Replace("public sealed partial class MainViewModel : INotifyPropertyChanged",
                onClass + " public sealed partial class MainViewModel : INotifyPropertyChanged" + interfaces, StringComparison.Ordinal);

    private const string ExplicitAssembly = "[assembly: RunicPublication(RunicPublicationMode.Explicit)]";

    [Test]
    [Arguments("[RunicPublication(RunicPublicationMode.Explicit)]", "")]
    [Arguments("", ExplicitAssembly)]
    public async Task ExplicitPublicationChecksOnlyIncludedMembers(string onClass, string onAssembly)
    {
        var diagnostics = await AnalyzeAsync(Publication(onClass, onAssembly));
        Require(diagnostics.IsEmpty, Describe(diagnostics));
    }

    [Test]
    public async Task ImplicitPublicationOnTheClassWinsOverTheAssembly()
    {
        var diagnostics = await AnalyzeAsync(Publication("[RunicPublication(RunicPublicationMode.Implicit)]", ExplicitAssembly));
        Require(diagnostics.Select(diagnostic => diagnostic.Id).Order(StringComparer.Ordinal).SequenceEqual(["RUNICBRIDGE003", "RUNICBRIDGE004"]),
            Describe(diagnostics));
    }

    [Test]
    public async Task ExplicitPublicationChecksSharedClientMembers()
    {
        var diagnostics = await AnalyzeAsync(Publication("", ExplicitAssembly,
            "[RunicSharedClient] public interface ITagged { HashSet<string> Tags { get; } }", ", ITagged"));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE003", Describe(diagnostics));
    }

    // CommunityToolkit generates SaveCommand from [RelayCommand] Save, SaveAsync or OnSave: the error is at the method.
    [Test]
    public async Task ReportsAToolkitCommandAtItsMethod()
    {
        var diagnostics = await AnalyzeAsync(AppSource("""
                [RelayCommand] private Task NewAsync() => Task.CompletedTask;
                [RunicFailure(typeof(string))] [RelayCommand] private void Save() { }
                [RunicFailure(typeof(string))] [RelayCommand] private void Load() { }
            """), generated: """
            partial class MainViewModel
            {
                public CommunityToolkit.Mvvm.Input.IRelayCommand NewCommand => null!;
                public CommunityToolkit.Mvvm.Input.IRelayCommand SaveCommand => null!;
            }
            """);
        // Load has no generated command yet, as while the IDE's generators run: it is not reported.
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE004"
            && Message(diagnostics[0]).StartsWith("MainViewModel.NewCommand: the generated client member 'new'", StringComparison.Ordinal)
            && diagnostics[0].Location.SourceTree!.FilePath == "App.cs"
            && diagnostics[0].Location.GetLineSpan().StartLinePosition.Line == 15,
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsViewsThatTheGeneratorRejects()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            internal sealed partial class HiddenView : RunicView<PageViewModel>;
            [RunicViewContract("side-panel")] public sealed partial class SideView : RunicView<PageViewModel>;
            public abstract class ShellView<T> : RunicView<T> where T : class;
            """));
        Require(diagnostics.Select(Message).Order(StringComparer.Ordinal).SequenceEqual([
                "App.HiddenView: a Runic Window or View must be a public, top-level concrete class.",
                "SideView: a View contract must contain only letters and digits and start with a letter.",
            ]) && diagnostics.All(diagnostic => diagnostic.Id == "RUNICBRIDGE006"),
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsViewModelsThatTheGeneratorRejects()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            public sealed class PlainViewModel { public int Value { get; } }
            public sealed partial class PlainView : RunicView<PlainViewModel>;
            public sealed class EmptyViewModel : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                public static int Shared { get; }
            }
            public sealed partial class EmptyView : RunicView<EmptyViewModel>;
            public static class Dialogs
            {
                public sealed class ConfirmViewModel : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    public string Text { get; } = "";
                }
            }
            public sealed partial class ConfirmView : RunicView<Dialogs.ConfirmViewModel>;
            // No View presents it, so the generator never inspects it.
            public sealed class UnusedViewModel { public ICommand Save { get; } = null!; }
            """));
        Require(diagnostics.Select(Message).Order(StringComparer.Ordinal).SequenceEqual([
                "App.Dialogs+ConfirmViewModel: a Bridge ViewModel must be a public, top-level class.",
                "App.PlainViewModel must implement INotifyPropertyChanged.",
                "EmptyViewModel: a ViewModel needs at least one state property, command, or interaction.",
            ]) && diagnostics.All(diagnostic => diagnostic.Id == "RUNICBRIDGE007"),
            Describe(diagnostics));
    }

    // Only where the generator runs: a project that generates its own Bridges from its own assembly.
    [Test]
    [Arguments("false", "")]
    [Arguments("", "")]
    [Arguments("true", "../Model/bin/Debug/net10.0/Model.dll")]
    public async Task ReportsNothingWhereTheGeneratorDoesNotRun(string generation, string modelAssembly)
    {
        var properties = new Dictionary<string, string>();
        if (generation.Length > 0) properties["RunicApplicationFrontendGenerationEnabled"] = generation;
        if (modelAssembly.Length > 0) properties["RunicApplicationFrontendModelAssembly"] = modelAssembly;
        var diagnostics = await AnalyzeAsync(AppSource("public ICommand New { get; set; } = null!;"), properties: properties);
        Require(diagnostics.IsEmpty, Describe(diagnostics));
    }
}
