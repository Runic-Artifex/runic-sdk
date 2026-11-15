using Microsoft.CodeAnalysis;
using TUnit.Core;
using static Runic.Application.Views.Analyzers.Tests.AnalyzerHarness;

namespace Runic.Application.Views.Analyzers.Tests;

// The rules that compare a ViewModel or View with the rest of the application, the ReactiveUI adapter rule, and the
// warnings that the generator leaves to the analyzer (RUNICBRIDGE013, 028, 034, 039): each with the generator's ID,
// severity and message (tools/Runic.Application.Views.Codegen).
public sealed class BridgeApplicationAnalyzerTests
{
    [Test]
    [Arguments("[RunicOperationRetirement(RunicOperationRetirement.Cancel)] public IRelayCommand SaveCommand { get; } = null!;",
        "RUNICBRIDGE028", "MainViewModel.SaveCommand: RunicOperationRetirement applies only to a Bridge command that starts an operation (an asynchronous CommunityToolkit or ReactiveUI command); it has no effect here.")]
    [Arguments("[RunicOperationRetirement((RunicOperationRetirement)42)] public IAsyncRelayCommand SaveCommand { get; } = null!;",
        "RUNICBRIDGE028", "MainViewModel.SaveCommand: 42 is not a RunicOperationRetirement value; the declaration is ignored.")]
    [Arguments("[RunicOperationClose(RunicOperationClose.Durable)] public int Total { get; }",
        "RUNICBRIDGE039", "MainViewModel.Total: RunicOperationClose applies only to a Bridge command that starts an operation (an asynchronous CommunityToolkit or ReactiveUI command); it has no effect here.")]
    [Arguments("[RunicOperationClose((RunicOperationClose)9)] public ReactiveUI.ReactiveCommand<string, bool> SaveCommand { get; } = null!;",
        "RUNICBRIDGE039", "MainViewModel.SaveCommand: 9 is not a RunicOperationClose value; the declaration is ignored.")]
    [Arguments("[RunicPresentationScoped] public int Total { get; }",
        "RUNICBRIDGE034", "MainViewModel.Total: RunicPresentationScoped applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method; it has no effect here.")]
    [Arguments("[RunicPresentationScoped] private void Reset() { }",
        "RUNICBRIDGE034", "MainViewModel.Reset: RunicPresentationScoped applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method; it has no effect here.")]
    public async Task ReportsTheGeneratorsWarningAtTheMember(string member, string id, string message)
    {
        var diagnostics = await AnalyzeAsync(AppSource(member));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == id && Message(diagnostics[0]) == message
            && diagnostics[0].Severity == DiagnosticSeverity.Warning,
            $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
        var line = diagnostics[0].Location.GetLineSpan();
        Require(line.Path == "App.cs" && line.StartLinePosition.Line == 15, $"{member}: reported at {line}");
        Require(diagnostics[0].Descriptor.HelpLinkUri.EndsWith("#" + id.ToLowerInvariant(), StringComparison.Ordinal),
            diagnostics[0].Descriptor.HelpLinkUri);
    }

    [Test]
    [Arguments("[RunicOperationRetirement(RunicOperationRetirement.Cancel)] public IAsyncRelayCommand SaveCommand { get; } = null!;")]
    [Arguments("[RunicOperationClose(RunicOperationClose.Durable)] public ReactiveUI.ReactiveCommand<string, bool> SaveCommand { get; } = null!;")]
    [Arguments("[RunicPresentationScoped] public IRelayCommand SaveCommand { get; } = null!;")]
    [Arguments("[RunicPresentationScoped] [RelayCommand] private void Save() { }")]
    [Arguments("public NavigationRegion<IPage> Content { get; } = null!;")]
    public async Task AcceptsDeclarationsThatReachTheirMember(string member)
    {
        var diagnostics = await AnalyzeAsync(AppSource(member), generated: member.Contains("[RelayCommand]", StringComparison.Ordinal)
            ? "partial class MainViewModel { public CommunityToolkit.Mvvm.Input.IRelayCommand SaveCommand => null!; }" : null);
        Require(diagnostics.IsEmpty, $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
    }

    // A class declaration applies to the operations; a ViewModel without one is warned about at the class.
    [Test]
    public async Task ReportsAClassDeclarationWithoutOperations()
    {
        var source = AppSource("public IRelayCommand SaveCommand { get; } = null!;")
            .Replace("public sealed partial class MainViewModel", "[RunicOperationRetirement(RunicOperationRetirement.Drain)] public sealed partial class MainViewModel", StringComparison.Ordinal);
        var diagnostics = await AnalyzeAsync(source);
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE028"
            && Message(diagnostics[0]) == "MainViewModel: RunicOperationRetirement on the class applies to its operations (asynchronous Bridge commands), but it has none; it has no effect here.",
            Describe(diagnostics));
    }

    // A CommunityToolkit command that only generated code declares, with an attribute forwarded with [property: ...],
    // is reported at its [RelayCommand] method: the generator leaves the warning to the analyzer.
    [Test]
    public async Task ReportsAGeneratedCommandsWarningAtItsMethod()
    {
        var diagnostics = await AnalyzeAsync(AppSource("[RelayCommand] private void Save() { }"), generated: """
            partial class MainViewModel
            {
                [global::Runic.Application.Views.RunicOperationClose(global::Runic.Application.Views.RunicOperationClose.Cancel)]
                public CommunityToolkit.Mvvm.Input.IRelayCommand SaveCommand => null!;
            }
            """);
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE039"
            && Message(diagnostics[0]).StartsWith("MainViewModel.SaveCommand: RunicOperationClose applies only", StringComparison.Ordinal)
            && diagnostics[0].Location.SourceTree!.FilePath == "App.cs" && diagnostics[0].Location.GetLineSpan().StartLinePosition.Line == 15,
            Describe(diagnostics));
    }

    // A base class's declaration is reported for each ViewModel that inherits it, naming the declaring class.
    [Test]
    public async Task ReportsABaseClassDeclarationForTheViewModel()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            public abstract class ScreenBase : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                [RunicPresentationScoped] public virtual IRelayCommand RefreshCommand => null!;
                [RunicPresentationScoped] protected void Reset() { }
            }
            public sealed class ScreenViewModel : ScreenBase
            {
                public override IRelayCommand RefreshCommand => null!;
            }
            public sealed partial class ScreenView : RunicView<ScreenViewModel>;
            """));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE034"
            && Message(diagnostics[0]) == "ScreenViewModel.Reset: RunicPresentationScoped applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method; ScreenBase declares it on a member the Bridge does not expose; it has no effect here.",
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsAViewModelThatASlotAdmitsWithoutAView()
    {
        var diagnostics = await AnalyzeAsync(AppSource("""
                public NavigationRegion<IPage> Content { get; } = null!;
                [RunicViewContract("Side")] public IPage? Side { get; private set; }
            """, """
            public sealed class OrphanViewModel : IPage
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                public string Title { get; } = "";
            }
            // Derives from a ViewModel with a View, so that View presents it.
            public class BasePageViewModel : IPage { public event PropertyChangedEventHandler? PropertyChanged; public int Value { get; } }
            public sealed partial class BasePageView : RunicView<BasePageViewModel>;
            public sealed class DerivedPageViewModel : BasePageViewModel;
            // The Side slot's contract: each ViewModel it admits has a View of it (RUNICBRIDGE006 otherwise).
            [RunicViewContract("Side")] public sealed partial class SidePageView : RunicView<PageViewModel>;
            [RunicViewContract("Side")] public sealed partial class SideBasePageView : RunicView<BasePageViewModel>;
            """));
        Require(diagnostics.Select(Message).Order(StringComparer.Ordinal).SequenceEqual([
                "MainViewModel.Content: App.OrphanViewModel is assignable to IPage but has no Runic View, so pushing it to this navigation region throws NotSupportedException at runtime. Add 'public sealed partial class OrphanView : RunicView<OrphanViewModel>;', or suppress RUNICBRIDGE013 with NoWarn if it is never presented here.",
                "MainViewModel.Side: App.OrphanViewModel is assignable to IPage but has no Runic View with contract 'Side', so presenting it in this slot throws NotSupportedException at runtime. Add 'public sealed partial class OrphanView : RunicView<OrphanViewModel>;' with [RunicViewContract(\"Side\")], or suppress RUNICBRIDGE013 with NoWarn if it is never presented here.",
            ]) && diagnostics.All(diagnostic => diagnostic.Id == "RUNICBRIDGE013" && diagnostic.Severity == DiagnosticSeverity.Warning
                && diagnostic.Location.SourceTree!.GetText().ToString().Substring(diagnostic.Location.SourceSpan.Start).StartsWith("OrphanViewModel", StringComparison.Ordinal)),
            Describe(diagnostics));
    }

    // A slot type that the Window's ViewModel also implements, or that admits more than eight ViewModels without a View,
    // names no ViewModel meant for it.
    [Test]
    [Arguments(2, "public IPage? Page { get; private set; }", true)]
    [Arguments(9, "public IPage? Page { get; private set; }", false)]
    public async Task ReportsOnlySlotsMeantForTheViewModel(int orphans, string member, bool reported)
    {
        var types = string.Concat(Enumerable.Range(0, orphans).Select(index =>
            $"public sealed class Orphan{index}ViewModel : IPage {{ public event PropertyChangedEventHandler? PropertyChanged; public string Title {{ get; }} = \"\"; }}\n"));
        var diagnostics = await AnalyzeAsync(AppSource(member, types));
        Require(diagnostics.Length == (reported ? orphans : 0) && diagnostics.All(diagnostic => diagnostic.Id == "RUNICBRIDGE013"), Describe(diagnostics));
        var window = await AnalyzeAsync(AppSource(member, types)
            .Replace("public sealed partial class MainViewModel : INotifyPropertyChanged", "public sealed partial class MainViewModel : IPage", StringComparison.Ordinal)
            .Replace("public int Count { get; set; }", "public int Count { get; set; } public string Title { get; } = \"\";", StringComparison.Ordinal));
        Require(window.IsEmpty, Describe(window));
    }

    [Test]
    public async Task ASuppressedWarningIsNotReported()
    {
        var diagnostics = await AnalyzeAsync(AppSource("""
            #pragma warning disable RUNICBRIDGE034
                [RunicPresentationScoped] public int Total { get; }
            #pragma warning restore RUNICBRIDGE034
            """));
        Require(diagnostics.IsEmpty, Describe(diagnostics));
    }

    [Test]
    public async Task ReportsAReactiveUiMemberWithoutItsAdapter()
    {
        const string members = """
                public ReactiveUI.ReactiveCommand<string, bool> SaveCommand { get; } = null!;
                public ReactiveUI.Binding.Reactive.Interaction<string, bool> Confirm { get; } = new();
            """;
        var none = await AnalyzeAsync(AppSource(members), adapter: null);
        Require(none.Select(Message).Order(StringComparer.Ordinal).SequenceEqual([
                "MainViewModel.Confirm is a ReactiveUI interaction, but the project references no Runic ReactiveUI adapter, whose helpers the generated Bridge calls. Reference Runic.Application.Views.ReactiveUI.Reactive.",
                "MainViewModel.SaveCommand is a ReactiveUI command, but the project references no Runic ReactiveUI adapter, whose helpers the generated Bridge calls. Reference Runic.Application.Views.ReactiveUI, or Runic.Application.Views.ReactiveUI.Reactive for ReactiveUI's System.Reactive flavor.",
            ]) && none.All(diagnostic => diagnostic.Id == "RUNICBRIDGE025" && diagnostic.Severity == DiagnosticSeverity.Error),
            Describe(none));
        var other = await AnalyzeAsync(AppSource(members));
        Require(other.Length == 1 && Message(other[0]) == "MainViewModel.Confirm is a System.Reactive ReactiveUI interaction, but the project references Runic.Application.Views.ReactiveUI, which uses ReactiveUI.Primitives. Use one ReactiveUI flavor: reference Runic.Application.Views.ReactiveUI.Reactive instead, or declare the interaction with the ReactiveUI.Primitives ReactiveUI.Binding interface.",
            Describe(other));
        var same = await AnalyzeAsync(AppSource(members), adapter: ReactiveAdapter);
        Require(same.IsEmpty, Describe(same));
    }

    [Test]
    public async Task ReportsASecondViewWithTheSameContract()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            [RunicViewContract("Compact")] public sealed partial class CompactPageView : RunicView<PageViewModel>;
            [RunicViewContract("compact")] public sealed partial class SmallPageView : RunicView<PageViewModel>;
            public sealed partial class OtherPageView : RunicView<PageViewModel>;
            """));
        Require(diagnostics.Select(Message).Order(StringComparer.Ordinal).SequenceEqual([
                "App.OtherPageView: App.PageViewModel already has a Runic View with contract 'default'.",
                "App.SmallPageView: App.PageViewModel already has a Runic View with contract 'compact'.",
            ]) && diagnostics.All(diagnostic => diagnostic.Id == "RUNICBRIDGE006"),
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsViewModelNamesThatCollide()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            namespace Other
            {
                public sealed class PageViewModel : INotifyPropertyChanged
                {
                    public event PropertyChangedEventHandler? PropertyChanged;
                    public int Value { get; }
                }
                public sealed partial class OtherPageView : RunicView<PageViewModel>;
            }
            """).Replace("namespace App;", "namespace App {", StringComparison.Ordinal)
            .Replace("public sealed record Row(string Id, string Name);", "public sealed record Row(string Id, string Name); }", StringComparison.Ordinal));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE004"
            && Message(diagnostics[0]) == "Bridge ViewModel names must be unique within an application: App.PageViewModel and Other.PageViewModel both use 'Page'.",
            Describe(diagnostics));
    }

    [Test]
    public async Task ReportsPresentationKindsThatCollide()
    {
        var diagnostics = await AnalyzeAsync(AppSource("", """
            [RunicViewContract("Side")] public sealed partial class SidePageView : RunicView<PageViewModel>;
            public sealed class PageSideViewModel : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                public int Value { get; }
            }
            public sealed partial class PageSideView : RunicView<PageSideViewModel>;
            """));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE004"
            && Message(diagnostics[0]) == "Bridge presentation kinds must be unique across ViewModels and View contracts: 'pageSide' is used by SidePageView and PageSideView.",
            Describe(diagnostics));
    }

    [Test]
    [Arguments("public NavigationRegion<IOther> Content { get; } = null!;",
        "RUNICBRIDGE008", "MainViewModel.Content: no ViewModel with a registered View is a IOther, so the NavigationRegion slot can present nothing.")]
    [Arguments("[RunicRegionCollection(typeof(IOther))] public NavigationSelection Documents { get; } = null!;",
        "RUNICBRIDGE070", "MainViewModel.Documents: no ViewModel with a registered View is a IOther, so the region collection can present nothing.")]
    [Arguments("public IReadOnlyList<IOther> Others { get; } = [];",
        "RUNICBRIDGE008", "MainViewModel.Others: a ViewModel collection needs a specific item interface or base class with a registered View.")]
    [Arguments("public IReadOnlyList<object> Items { get; } = [];",
        "RUNICBRIDGE008", "MainViewModel.Items: a ViewModel collection needs a specific item interface or base class with a registered View.")]
    [Arguments("[RunicViewContract(\"Wide\")] public IPage? Page { get; private set; }",
        "RUNICBRIDGE006", "MainViewModel.Page: no PageViewModel View has contract 'Wide'.")]
    [Arguments("[RunicViewContract(\"wide-page\")] public IPage? Page { get; private set; }",
        "RUNICBRIDGE006", "MainViewModel.Page: a View contract must contain only letters and digits and start with a letter.")]
    public async Task ReportsContentThatNoViewPresents(string member, string id, string message)
    {
        var diagnostics = await AnalyzeAsync(AppSource(member, "public interface IOther : INotifyPropertyChanged;"));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == id && Message(diagnostics[0]) == message
            && diagnostics[0].Severity == DiagnosticSeverity.Error, $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
    }
}

// The generator leaves BridgeDiagnosticCodes.AnalyzerWarnings to the analyzer (--analyzer-warnings), so the analyzer
// reports exactly those warnings: one more there would be reported by no one, one more here twice.
public sealed class AnalyzerWarningOwnershipTests
{
    [Test]
    public async Task TheAnalyzerReportsTheWarningsTheGeneratorLeavesToIt()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) directory = directory.Parent;
        Require(directory is not null, "The repository root was not found.");
        var program = await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "tools", "Runic.Application.Views.Codegen", "Program.cs"));
        var constants = System.Text.RegularExpressions.Regex.Matches(program, @"internal const string (\w+) = ""(RUNICBRIDGE\d+)"";")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        var owned = System.Text.RegularExpressions.Regex.Match(program, @"AnalyzerWarnings = \[([^\]]*)\]").Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(name => constants[name]).Order().ToArray();
        var warnings = new BridgeShapeAnalyzer().SupportedDiagnostics.Where(rule => rule.DefaultSeverity == DiagnosticSeverity.Warning)
            .Select(rule => rule.Id).Order().ToArray();
        Require(owned.Length > 0 && owned.SequenceEqual(warnings), $"Generator: {string.Join(", ", owned)}; analyzer: {string.Join(", ", warnings)}");
    }
}
