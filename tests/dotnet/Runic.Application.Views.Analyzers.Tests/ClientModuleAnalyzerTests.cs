using Microsoft.CodeAnalysis;
using TUnit.Core;
using static Runic.Application.Views.Analyzers.Tests.AnalyzerHarness;

namespace Runic.Application.Views.Analyzers.Tests;

// #1318: an application that maps a client library (RunicApplicationFrontendClientModule) declares no Views of the
// library's ViewModels; the generator adds the library's Views to the application's (ClientModules.Discover). The
// analyzer does the same, so a slot that presents a library ViewModel is content, not a DTO, and reports nothing at
// the library's types, which the library's own build analyzes.
public sealed class ClientModuleAnalyzerTests
{
    private const string Marker = """[assembly: System.Reflection.AssemblyMetadata("Runic.Application.Views.ClientLibrary", "1")]""";

    // A library ViewModel with a command, which a DTO cannot have (RUNICBRIDGE003), and a View with a second contract.
    private const string SharedUi = """
        namespace SharedUi
        {
            public sealed class AboutViewModel : System.ComponentModel.INotifyPropertyChanged
            {
                public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
                public string Version { get; } = "";
                public System.Windows.Input.ICommand CheckCommand { get; } = null!;
            }
            public sealed partial class AboutView : Runic.Application.Views.RunicView<AboutViewModel>;
            [Runic.Application.Views.RunicViewContract("compact")]
            public sealed partial class CompactAboutView : Runic.Application.Views.RunicView<AboutViewModel>;
        }
        """;

    private static Dictionary<string, string> Mapping(string modules) => new(Generating) { ["_RunicApplicationFrontendClientModules"] = modules };

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> AnalyzeAsync(string member, string library,
        Dictionary<string, string> properties) =>
        AnalyzerHarness.AnalyzeAsync(Document(AppSource(member), libraries: [Library("SharedUi", library)]), properties);

    [Test]
    [Arguments("public SharedUi.AboutViewModel About { get; } = null!;")]
    [Arguments("[RunicViewContract(\"compact\")] public SharedUi.AboutViewModel About { get; } = null!;")]
    [Arguments("public IReadOnlyList<SharedUi.AboutViewModel> Abouts { get; } = null!;")]
    [Arguments("public NavigationRegion<SharedUi.AboutViewModel> About { get; } = null!;")]
    public async Task PresentsAMappedLibrarysViewModelAsContent(string member)
    {
        var diagnostics = await AnalyzeAsync(member, Marker + SharedUi, Mapping("SharedUi"));
        Require(diagnostics.IsEmpty, $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
        // The item's names are trimmed and compared ignoring case, as the generator compares assembly names.
        diagnostics = await AnalyzeAsync(member, Marker + SharedUi, Mapping(" Other ; sharedui ;"));
        Require(diagnostics.IsEmpty, $"{member}:{Environment.NewLine}{Describe(diagnostics)}");
    }

    // Without the item, or for an assembly that was not built in library mode (the generator's RUNICBRIDGE079), the
    // library's Views are not the application's, so the slot is checked as a value, as before.
    [Test]
    [Arguments(true, "")]
    [Arguments(true, "Other")]
    [Arguments(false, "SharedUi")]
    public async Task ChecksTheViewModelAsAValueWithoutAMarkedMappedLibrary(bool marked, string modules)
    {
        var diagnostics = await AnalyzeAsync("public SharedUi.AboutViewModel About { get; } = null!;",
            (marked ? Marker : "") + SharedUi, modules.Length == 0 ? new(Generating) : Mapping(modules));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE003"
            && Message(diagnostics[0]).StartsWith("MainViewModel.About.checkCommand: ", StringComparison.Ordinal)
            && diagnostics[0].Location.GetLineSpan().Path == "App.cs",
            Describe(diagnostics));
    }

    // The library's View contracts count, as in the generator's content switch (RUNICBRIDGE006 at the slot).
    [Test]
    public async Task ChecksASlotsContractAgainstTheLibrarysViews()
    {
        var diagnostics = await AnalyzeAsync("[RunicViewContract(\"wide\")] public SharedUi.AboutViewModel About { get; } = null!;",
            Marker + SharedUi, Mapping("SharedUi"));
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICBRIDGE006"
            && Message(diagnostics[0]) == "MainViewModel.About: no AboutViewModel View has contract 'wide'."
            && diagnostics[0].Location.GetLineSpan().Path == "App.cs",
            Describe(diagnostics));
    }

    // A name that collides with a library ViewModel's is the generator's RUNICBRIDGE004, which it reports at the later,
    // the library's, ViewModel: the analyzer reports nothing at a library type.
    [Test]
    public async Task ReportsNothingAtTheLibrarysTypes()
    {
        var diagnostics = await AnalyzeAsync("public SharedUi.AboutViewModel About { get; } = null!;", Marker + SharedUi, Mapping("SharedUi"));
        Require(diagnostics.IsEmpty, Describe(diagnostics));
        var source = AppSource("public AboutViewModel About { get; } = null!;", """
            public sealed class AboutViewModel : INotifyPropertyChanged
            {
                public event PropertyChangedEventHandler? PropertyChanged;
                public string Title { get; } = "";
            }
            public sealed partial class AboutView : RunicView<AboutViewModel>;
            """);
        diagnostics = await AnalyzerHarness.AnalyzeAsync(Document(source, libraries: [Library("SharedUi", Marker + SharedUi)]), Mapping("SharedUi"));
        Require(diagnostics.All(diagnostic => diagnostic.Location.IsInSource), Describe(diagnostics));
    }
}
