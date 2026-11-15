using Microsoft.CodeAnalysis;
using TUnit.Core;
using static Runic.Application.Views.Analyzers.Tests.AnalyzerHarness;

namespace Runic.Application.Views.Analyzers.Tests;

// The generated client's names (RUNICBRIDGE004, 008, 071) and the RunicFailure and RunicCommandKey declarations of a
// command (RUNICBRIDGE012, 027), each with the generator's ID and message (tools/Runic.Application.Views.Codegen).
// MainViewModel has Count { get; set; } (state 'count', client members setCount and writeCount).
public sealed class BridgeSurfaceAnalyzerTests
{
    [Test]
    [Arguments("[RunicAlias(\"count\")] public int Total { get; }",
        "RUNICBRIDGE004", "MainViewModel: state properties Count and Total share the generated wire name 'count'.", 15)]
    [Arguments("[System.Text.Json.Serialization.JsonPropertyName(\"count\")] public int Total { get; }",
        "RUNICBRIDGE004", "MainViewModel: state properties Count and Total share the generated wire name 'count'.", 15)]
    [Arguments("[System.Text.Json.Serialization.JsonPropertyName(\" \")] public int Total { get; }",
        "RUNICBRIDGE008", "MainViewModel.Total: a bridge wire name is required.", 15)]
    [Arguments("public IRelayCommand SetCountCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel: generated view name 'setCount' conflicts between Count setter and SetCountCommand. Rename one member or add an explicit bridge alias.", 11)]
    [Arguments("public IRelayCommand WriteCountCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel: generated view name 'writeCount' conflicts between Count checked writer and WriteCountCommand. Rename one member or add an explicit bridge alias.", 11)]
    [Arguments("public bool CanReset { get; } public IRelayCommand ResetCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel: generated state name 'canReset' conflicts between CanReset and ResetCommand availability. Rename one member or add an explicit bridge alias.", 11)]
    [Arguments("public bool IsSaveExecuting { get; } public IAsyncRelayCommand SaveCommand { get; } = null!;",
        "RUNICBRIDGE004", "MainViewModel: generated state name 'isSaveExecuting' conflicts between IsSaveExecuting and SaveCommand execution state. Rename one member or add an explicit bridge alias.", 11)]
    [Arguments("[RunicRegionCollection(typeof(IPage))] public NavigationSelection Documents { get; } = null!; public IRelayCommand SelectDocumentsCommand { get; } = null!;",
        "RUNICBRIDGE071", "MainViewModel.Documents: the region collection's client method selectDocuments collides with another member of the generated client. Rename the property or the other member.", 15)]
    [Arguments("[RunicRegionCollection(typeof(IPage))] public NavigationSelection Documents { get; } = null!; public IRelayCommand CloseDocumentsCommand { get; } = null!;",
        "RUNICBRIDGE071", "MainViewModel.Documents: the region collection's client method closeDocuments collides with another member of the generated client. Rename the property or the other member.", 15)]
    [Arguments("[RunicFailure(typeof(System.InvalidOperationException))] public IRelayCommand SaveCommand { get; } = null!;",
        "RUNICBRIDGE012", "MainViewModel.SaveCommand: the failure type System.InvalidOperationException must be a Bridge value type other than object, an exception or Nullable<T>.", 15)]
    [Arguments("[RunicFailure(typeof(int?))] public IRelayCommand SaveCommand { get; } = null!;",
        "RUNICBRIDGE012", "MainViewModel.SaveCommand: the failure type System.Nullable<System.Int32> must be a Bridge value type other than object, an exception or Nullable<T>.", 15)]
    [Arguments("[RunicCommandKey(nameof(Find))] [RunicCommandInput(typeof(string))] public ICommand OpenCommand { get; } = null!; public Row? Find(string id) => null;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: RunicCommandKey replaces RunicCommandInput, whose key is the resolver's parameter. Declare one of them.", 15)]
    [Arguments("[RunicCommandKey(nameof(Find))] public IRelayCommand OpenCommand { get; } = null!; public Row? Find(string id) => null;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: RunicCommandKey resolves a command's parameter, but this command takes none.", 15)]
    [Arguments("[RunicCommandKey(\"Lookup\")] public ICommand OpenCommand { get; } = null!;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: the RunicCommandKey resolver 'Lookup' is not a method of MainViewModel.", 15)]
    [Arguments("[RunicCommandKey(nameof(Find))] public ICommand OpenCommand { get; } = null!; public Row? Find(string id) => null;\n    public Row? Find(int id) => null;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: the RunicCommandKey resolver MainViewModel.Find is overloaded. Name a method with one signature.", 16)]
    [Arguments("[RunicCommandKey(nameof(Find))] public ICommand OpenCommand { get; } = null!; private Row? Find(string id) => null;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: the RunicCommandKey resolver MainViewModel.Find must be public or internal, so the generated Bridge can call it.", 15)]
    [Arguments("[RunicCommandKey(nameof(Find))] public ICommand OpenCommand { get; } = null!; public Row? Find(string id, int page) => null;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: the RunicCommandKey resolver MainViewModel.Find must take the key as its only parameter, and an asynchronous resolver may add a CancellationToken.", 15)]
    [Arguments("[RunicCommandKey(nameof(Find))] public ICommand OpenCommand { get; } = null!; public void Find(string id) { }",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: the RunicCommandKey resolver MainViewModel.Find returns System.Void; it must return the command's parameter, or null when the key names no object, directly or as Task<T> or ValueTask<T>.", 15)]
    [Arguments("[RunicCommandKey(nameof(Find))] public IRelayCommand<Row> OpenCommand { get; } = null!; public Task<string?> Find(string id) => null!;",
        "RUNICBRIDGE027", "MainViewModel.OpenCommand: the RunicCommandKey resolver MainViewModel.Find returns System.Threading.Tasks.Task<System.String>; it must return the command's parameter App.Row, or null when the key names no object, directly or as Task<T> or ValueTask<T>.", 15)]
    public async Task ReportsTheGeneratorsErrorWhereItDoes(string members, string id, string message, int line)
    {
        var diagnostics = await AnalyzeAsync(AppSource(members));
        var found = diagnostics.Where(diagnostic => diagnostic.Id == id && Message(diagnostic) == message).ToArray();
        Require(found.Length == 1 && found[0].Severity == DiagnosticSeverity.Error && diagnostics.Length == 1,
            $"{members}:{Environment.NewLine}{Describe(diagnostics)}");
        var at = found[0].Location.GetLineSpan();
        Require(at.Path == "App.cs" && at.StartLinePosition.Line == line, $"{members}: reported at {at}");
    }

    [Test]
    [Arguments("[RunicCommandKey(nameof(Find))] public ICommand OpenCommand { get; } = null!; public Row? Find(string id) => null;")]
    [Arguments("[RunicCommandKey(nameof(Find))] public IRelayCommand<Row> OpenCommand { get; } = null!; public async Task<Row?> Find(string id, System.Threading.CancellationToken token) { await Task.Yield(); return null; }")]
    [Arguments("[RunicCommandKey(nameof(Find))] public IRelayCommand<object> OpenCommand { get; } = null!; internal int Find(string id) => 0;")]
    [Arguments("[RunicFailure(typeof(Row))] public IRelayCommand SaveCommand { get; } = null!;")]
    [Arguments("[RunicRegionCollection(typeof(IPage), PageCanClose = false)] public NavigationSelection Documents { get; } = null!; public IRelayCommand CloseDocumentsCommand { get; } = null!;")]
    [Arguments("[RunicAlias(\"total\")] public int Count2 { get; } public IAsyncRelayCommand<string> SaveCommand { get; } = null!; public bool CanSave { get; }")]
    public async Task AcceptsWhatTheGeneratorAccepts(string members)
    {
        var diagnostics = await AnalyzeAsync(AppSource(members));
        Require(diagnostics.IsEmpty, $"{members}:{Environment.NewLine}{Describe(diagnostics)}");
    }

    // [RelayCommand(CanExecute = nameof(CanSave))] Save makes CanSave the command's availability, not a state property.
    [Test]
    public async Task ACommandsAvailabilityIsNoStateProperty()
    {
        var diagnostics = await AnalyzeAsync(AppSource("""
                public bool CanSave { get; }
                [RelayCommand(CanExecute = nameof(CanSave))] private void Save() { }
            """), generated: "partial class MainViewModel { public CommunityToolkit.Mvvm.Input.IRelayCommand SaveCommand => null!; }");
        Require(diagnostics.IsEmpty, Describe(diagnostics));
    }

    [Test]
    public async Task ReportsAFailureDeclaredOnThePropertyAndTheMethod()
    {
        var diagnostics = await AnalyzeAsync(AppSource("[RunicFailure(typeof(Row))] [RelayCommand] private void Save() { }"), generated: """
            partial class MainViewModel
            {
                [global::Runic.Application.Views.RunicFailure(typeof(Row))] public CommunityToolkit.Mvvm.Input.IRelayCommand SaveCommand => null!;
            }
            """);
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE012"
            && Message(diagnostics[0]) == "MainViewModel.SaveCommand: declare RunicFailure once, on the command property or on its [RelayCommand] method, not on both."
            && diagnostics[0].Location.GetLineSpan().StartLinePosition.Line == 15 && diagnostics[0].Location.SourceTree!.FilePath == "App.cs",
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsTheReservedValidationMember()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            public sealed class FormViewModel : INotifyPropertyChanged, INotifyDataErrorInfo
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                public event System.EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
                public bool HasErrors => false;
                public System.Collections.IEnumerable GetErrors(string? propertyName) => System.Array.Empty<object>();
                public string Validation { get; } = "";
            }
            public sealed partial class FormView : RunicView<FormViewModel>;
            """));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE004"
            && Message(diagnostics[0]) == "FormViewModel.Validation: validation is reserved for generated validation state. Alias the model property.",
            Describe(diagnostics));
    }

    // A value whose DTO reports errors gives the client its validation state too.
    [Test]
    public async Task ReportsTheValidationMemberOfAValidatedValue()
    {
        var diagnostics = await AnalyzeAsync(AppSource("public Form Draft { get; } = new(); public string Validation { get; } = \"\";", """
            public sealed class Form : INotifyDataErrorInfo
            {
                public event System.EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
                public bool HasErrors => false;
                public System.Collections.IEnumerable GetErrors(string? propertyName) => System.Array.Empty<object>();
                public string Name { get; } = "";
            }
            """));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE004"
            && Message(diagnostics[0]) == "MainViewModel.Validation: validation is reserved for generated validation state. Alias the model property.",
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsViewRequestsWithTheSameName()
    {
        var diagnostics = await AnalyzeAsync(AppSource("""
                public ViewRequest<string> Focus { get; } = null!;
                public ViewRequest<string> focus { get; } = null!;
            """));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE004"
            && Message(diagnostics[0]) == "MainViewModel.focus: duplicate generated view request name 'focus'.", Describe(diagnostics));
    }

    [Test]
    public async Task ReportsInteractionsWithTheSameName()
    {
        var diagnostics = await AnalyzeAsync(AppSource("""
                public ViewQuestion<string, bool> Confirm { get; } = null!;
                public ReactiveUI.Binding.Interaction<string, bool> confirm { get; } = new();
            """));
        Require(diagnostics.Any(diagnostic => diagnostic.Id == "RUNICBRIDGE004"
            && Message(diagnostic) == "MainViewModel.confirm: duplicate generated interaction name 'confirm'."), Describe(diagnostics));
    }
}
