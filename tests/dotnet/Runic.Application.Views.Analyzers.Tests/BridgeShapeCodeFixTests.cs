using TUnit.Core;
using static Runic.Application.Views.Analyzers.Tests.AnalyzerHarness;

namespace Runic.Application.Views.Analyzers.Tests;

// The mechanical fixes: a private setter, a renamed command, a public Window, View or ViewModel. Each fixed source
// is analyzed again and no longer reported.
public sealed class BridgeShapeCodeFixTests
{
    [Test]
    [Arguments("public ViewRequest<string> Focus { get; set; } = null!;", "public ViewRequest<string> Focus { get; private set; } = null!;")]
    [Arguments("public ViewQuestion<string, bool> Ask { get; init; } = null!;", "public ViewQuestion<string, bool> Ask { get; private init; } = null!;")]
    [Arguments("public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; set; } = null!;",
        "public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; private set; } = null!;")]
    [Arguments("public NavigationSelection Tabs\n    {\n        get;\n        set;\n    } = null!;",
        "public NavigationSelection Tabs\n    {\n        get;\n        private set;\n    } = null!;")]
    [Arguments("public IPage? Page { get; set; }", "public IPage? Page { get; private set; }")]
    [Arguments("[RunicAppendOnlyText] public string Log { get; set; } = \"\";", "[RunicAppendOnlyText] public string Log { get; private set; } = \"\";")]
    [Arguments("[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; set; } = [];",
        "[RunicCollection(nameof(Row.Id))] public ObservableCollection<Row> Rows { get; private set; } = [];")]
    [Arguments("public RunicQueryCollection<Row, string> Rows { get; set; } = null!;", "public RunicQueryCollection<Row, string> Rows { get; private set; } = null!;")]
    public async Task MakesTheSetterPrivate(string member, string expected)
    {
        var (text, fixedDocument, title) = await FixAsync(Document(AppSource(member)));
        Require(title == "Make the setter private" && text.Contains(expected, StringComparison.Ordinal), $"{title}:{Environment.NewLine}{text}");
        var remaining = await AnalyzeAsync(fixedDocument);
        Require(remaining.IsEmpty, Describe(remaining));
    }

    [Test]
    [Arguments("public IRelayCommand Save { get; } = null!; private void Run() => Save.Execute(null);",
        "public IRelayCommand SaveCommand { get; } = null!; private void Run() => SaveCommand.Execute(null);", "Rename to SaveCommand")]
    [Arguments("public IRelayCommand NewCommand { get; } = null!;", "public IRelayCommand CreateCommand { get; } = null!;", "Rename to CreateCommand")]
    public async Task RenamesTheCommand(string member, string expected, string expectedTitle)
    {
        var (text, fixedDocument, title) = await FixAsync(Document(AppSource(member)));
        Require(title == expectedTitle && text.Contains(expected, StringComparison.Ordinal), $"{title}:{Environment.NewLine}{text}");
        var remaining = await AnalyzeAsync(fixedDocument);
        Require(remaining.IsEmpty, Describe(remaining));
    }

    // CommunityToolkit generates the command from the method, so the method is renamed, keeping On and Async.
    [Test]
    [Arguments("[RelayCommand] private void New() { }", "[RelayCommand] private void Create() { }", "Rename to Create")]
    [Arguments("[RelayCommand] private Task OnThenAsync() => Task.CompletedTask;", "[RelayCommand] private Task OnContinueAsync() => Task.CompletedTask;", "Rename to OnContinueAsync")]
    public async Task RenamesTheToolkitMethod(string member, string expected, string expectedTitle)
    {
        var command = member.Contains("Then", StringComparison.Ordinal) ? "ThenCommand" : "NewCommand";
        var document = Document(AppSource(member),
            $"partial class MainViewModel {{ public CommunityToolkit.Mvvm.Input.IRelayCommand {command} => null!; }}");
        var (text, _, title) = await FixAsync(document);
        Require(title == expectedTitle && text.Contains(expected, StringComparison.Ordinal), $"{title}:{Environment.NewLine}{text}");
    }

    [Test]
    [Arguments("internal sealed partial class SideView : RunicView<PageViewModel>;", "public sealed partial class SideView : RunicView<PageViewModel>;")]
    [Arguments("[RunicViewContract(\"Side\")]\nsealed partial class SideView : RunicView<PageViewModel>;",
        "[RunicViewContract(\"Side\")]\npublic sealed partial class SideView : RunicView<PageViewModel>;")]
    public async Task MakesTheViewPublic(string view, string expected)
    {
        var (text, fixedDocument, title) = await FixAsync(Document(AppSource("", view)));
        Require(title == "Make SideView public" && text.Contains(expected, StringComparison.Ordinal), $"{title}:{Environment.NewLine}{text}");
        var remaining = await AnalyzeAsync(fixedDocument);
        Require(remaining.IsEmpty, Describe(remaining));
    }
}
