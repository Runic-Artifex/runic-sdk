using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// #1320: explicit publication ([RunicPublication(RunicPublicationMode.Explicit)]) on an assembly or a ViewModel class, the
// class's declaration winning over the assembly's, [RunicSharedClient] members counting as included, RUNICBRIDGE077 and
// RUNICBRIDGE078, and the default contract unchanged. PublicationTests drives a generated explicit Bridge.
internal sealed partial class CodegenDiagnosticsTests
{
    // Members the generator rejects when it publishes them: an unsupported collection (RUNICBRIDGE003) and a command whose
    // client member is reserved (RUNICBRIDGE004). Unpublished, they are never inspected.
    private const string UnpublishableMembers = """
            public HashSet<string> Tags { get; } = [];
            public IRelayCommand NewCommand { get; } = new RelayCommand(() => { });
        """;

    [Test]
    public async Task ExplicitPublicationPublishesOnlyIncludedMembersAsync()
    {
        var generator = GeneratorPath();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-publication-{Guid.NewGuid():N}");
        try
        {
            // The assembly is explicit; Imports follows it, Legacy returns to the default, Results implements a shared
            // client, and Panel is explicit on its own base class.
            var (exitCode, output, directory) = await RunGeneratorAsync(generator, temporaryRoot, "ExplicitAssembly", Preamble.Replace(
                "namespace Fixture;", "[assembly: RunicPublication(RunicPublicationMode.Explicit)]\nnamespace Fixture;", StringComparison.Ordinal) + $$"""
                public sealed class ImportsViewModel : FixtureModel
                {
                    [RunicInclude] public string Title { get; set; } = "";
                    [RunicInclude] public IRelayCommand<string> RenameCommand { get; } = new RelayCommand<string>(_ => { });
                    public IReadOnlyList<string> Imported { get; } = [];
                    public bool ShowSkipped => false;
                    public IRelayCommand ShowHelpCommand { get; } = new RelayCommand(() => { });
                {{UnpublishableMembers}}
                }
                [RunicPublication(RunicPublicationMode.Implicit)]
                public sealed class LegacyViewModel : FixtureModel
                {
                    public int Count { get; set; }
                    public IReadOnlyList<string> Imported { get; } = [];
                }
                [RunicSharedClient]
                public interface IResultPage { string Heading { get; } IRelayCommand RefreshCommand { get; } }
                public sealed class ResultsViewModel : FixtureModel, IResultPage
                {
                    public string Heading => "";
                    public IRelayCommand RefreshCommand { get; } = new RelayCommand(() => { });
                    public string Footer => "";
                }
                [RunicPublication(RunicPublicationMode.Explicit)]
                public abstract class ExplicitModel : FixtureModel;
                public sealed class PanelViewModel : ExplicitModel
                {
                    [RunicInclude] public string Caption => "";
                    public string Hidden => "";
                }
                public sealed partial class ImportsWindow : RunicWindow<ImportsViewModel>;
                public sealed partial class LegacyView : RunicView<LegacyViewModel>;
                public sealed partial class ResultsView : RunicView<ResultsViewModel>;
                public sealed partial class PanelView : RunicView<PanelViewModel>;
                """).ConfigureAwait(false);
            Require(exitCode == 0 && !output.Contains("RUNICBRIDGE", StringComparison.Ordinal),
                $"The generator did not accept explicit publication without diagnostics.\n{output}");
            var typescript = Path.Combine(directory, "ts");
            var imports = GeneratedClientSource.Read(typescript, "imports");
            Require(imports.Contains("  readonly title: string;", StringComparison.Ordinal)
                    && imports.Contains("setTitle(value: string)", StringComparison.Ordinal)
                    && imports.Contains("rename(input: string)", StringComparison.Ordinal)
                    && imports.Contains("canRename(input: string)", StringComparison.Ordinal)
                    && Lacks(imports, "imported", "showSkipped", "showHelp", "ShowHelp", "tags", "new("),
                $"An explicit ViewModel's client is not its [RunicInclude] members.\n{imports}");
            var bridge = File.ReadAllText(Path.Combine(directory, "cs", "Fixture.ImportsViewModel.Bridge.g.cs"));
            Require(bridge.Contains("Title", StringComparison.Ordinal) && bridge.Contains("RenameCommand", StringComparison.Ordinal)
                    && Lacks(bridge, "Imported", "ShowSkipped", "ShowHelpCommand", "Tags", "NewCommand"),
                $"An explicit ViewModel's Bridge reads a member its contract omits.\n{bridge}");
            var legacy = GeneratedClientSource.Read(typescript, "legacy");
            Require(legacy.Contains("  readonly count: number;", StringComparison.Ordinal) && legacy.Contains("  readonly imported: readonly string[];", StringComparison.Ordinal),
                $"[RunicPublication(Implicit)] on the class did not win over the assembly's explicit mode.\n{legacy}");
            var results = GeneratedClientSource.Read(typescript, "results");
            var shared = GeneratedClientSource.Read(typescript, "resultPage");
            Require(results.Contains("  readonly heading: string;", StringComparison.Ordinal) && results.Contains("refresh()", StringComparison.Ordinal)
                    && !results.Contains("footer", StringComparison.Ordinal)
                    && shared.Contains("  readonly heading: string;", StringComparison.Ordinal) && shared.Contains("refresh()", StringComparison.Ordinal),
                $"A [RunicSharedClient] interface's members were not published in explicit mode.\n{results}\n{shared}");
            var panel = GeneratedClientSource.Read(typescript, "panel");
            Require(panel.Contains("  readonly caption: string;", StringComparison.Ordinal) && !panel.Contains("hidden", StringComparison.Ordinal),
                $"[RunicPublication] on a base class did not apply to the ViewModel deriving from it.\n{panel}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // The default mode generates what it generated before the attribute existed: the same ViewModel without a declaration,
    // with [RunicPublication(Implicit)], and in explicit mode with every member included, has one client and one Bridge.
    [Test]
    public async Task PublicationModesKeepTheDefaultContractAsync()
    {
        var generator = GeneratorPath();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-publication-default-{Guid.NewGuid():N}");
        try
        {
            async Task<(string Client, string Bridge)> Generate(string name, string attribute, string include, string? includeLines = null)
            {
                var cs = await GenerateValidAsync(generator, temporaryRoot, name, Preamble + $$"""
                    {{attribute}}
                    public sealed class EditorViewModel : FixtureModel
                    {
                        {{include}} public string Title { get; set; } = "";
                        {{includeLines ?? include}} public IReadOnlyList<string> Lines { get; } = [];
                        {{include}} public IRelayCommand<string> RenameCommand { get; } = new RelayCommand<string>(_ => { });
                        {{include}} public IAsyncRelayCommand SaveCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                        [RunicIgnore] public object? NativeOnly { get; set; }
                    }
                    public sealed partial class EditorWindow : RunicWindow<EditorViewModel>;
                    """).ConfigureAwait(false);
                return (GeneratedClientSource.Read(Path.Combine(Path.GetDirectoryName(cs)!, "ts"), "editor"),
                    File.ReadAllText(Path.Combine(cs, "Fixture.EditorViewModel.Bridge.g.cs")));
            }
            var unmarked = await Generate("PublicationUnmarked", "", "").ConfigureAwait(false);
            var implicitMode = await Generate("PublicationImplicit", "[RunicPublication(RunicPublicationMode.Implicit)]", "").ConfigureAwait(false);
            var explicitMode = await Generate("PublicationExplicit", "[RunicPublication(RunicPublicationMode.Explicit)]", "[RunicInclude]").ConfigureAwait(false);
            // Leaving one member out changes the contract fingerprint, which the client and the Bridge carry.
            var fewer = await Generate("PublicationExplicitFewer", "[RunicPublication(RunicPublicationMode.Explicit)]", "[RunicInclude]", "").ConfigureAwait(false);
            Require(unmarked.Client.Contains("  readonly title: string;", StringComparison.Ordinal) && !unmarked.Client.Contains("nativeOnly", StringComparison.Ordinal),
                $"The default ViewModel's client is not its public members.\n{unmarked.Client}");
            Require(unmarked == implicitMode, "[RunicPublication(Implicit)] changed the generated client or Bridge.");
            Require(unmarked == explicitMode, "Explicit publication with every member included differs from the default.");
            static string Fingerprint(string client) =>
                System.Text.RegularExpressions.Regex.Match(client, "const bridgeContract = \"Fixture.EditorViewModel:([0-9A-F]{64})\"").Groups[1].Value;
            Require(Fingerprint(explicitMode.Client).Length == 64 && Fingerprint(fewer.Client).Length == 64
                    && Fingerprint(fewer.Client) != Fingerprint(explicitMode.Client)
                    && fewer.Bridge.Contains(Fingerprint(fewer.Client), StringComparison.Ordinal) && !fewer.Client.Contains("readonly lines", StringComparison.Ordinal),
                $"Leaving a member out of explicit publication did not change the contract fingerprint.\n{fewer.Client}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // RUNICBRIDGE077: [RunicInclude] with [RunicIgnore], also through an overridden property, is a located error in either
    // mode. RUNICBRIDGE078: [RunicInclude] where the Bridge publishes nothing is a located warning that leaves the contract
    // as it is.
    [Test]
    public async Task PublicationDeclarationsAreReportedAsync()
    {
        var generator = GeneratorPath();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-publication-diagnostics-{Guid.NewGuid():N}");
        try
        {
            const string Contradiction = Preamble + """
                public abstract class IgnoringModel : FixtureModel
                {
                    [RunicIgnore] public virtual string Note { get; set; } = "";
                }
                public sealed class ContradictionViewModel : IgnoringModel
                {
                    [RunicInclude, RunicIgnore] public string Title { get; set; } = "";
                    [RunicInclude] public override string Note { get; set; } = "";
                    public int Count { get; set; }
                }
                public sealed partial class ContradictionWindow : RunicWindow<ContradictionViewModel>;
                [RunicPublication(RunicPublicationMode.Explicit)]
                public sealed class ExplicitContradictionViewModel : FixtureModel
                {
                    [RunicInclude] public int Count { get; set; }
                    [RunicIgnore, RunicInclude] public string Title { get; set; } = "";
                }
                public sealed partial class ExplicitContradictionView : RunicView<ExplicitContradictionViewModel>;
                """;
            var (exitCode, output, directory) = await RunGeneratorAsync(generator, temporaryRoot, "IncludeIgnored", Contradiction).ConfigureAwait(false);
            int Line(string source, string declaration) => source[..source.IndexOf(declaration, StringComparison.Ordinal)].Count(character => character == '\n') + 1;
            bool ErrorAt(string declaration, string message) => output.Split('\n').Any(text =>
                text.StartsWith($"{Path.Combine(directory, "Fixture.cs")}({Line(Contradiction, declaration)},", StringComparison.Ordinal)
                && text.Contains(": error RUNICBRIDGE077: " + message, StringComparison.Ordinal)
                && text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE077")}", StringComparison.Ordinal));
            // Each ViewModel reports its first problem.
            Require(exitCode != 0
                    && ErrorAt("[RunicInclude, RunicIgnore] public string Title", "ContradictionViewModel.Title: [RunicInclude] and [RunicIgnore] contradict each other")
                    && ErrorAt("[RunicIgnore, RunicInclude] public string Title", "ExplicitContradictionViewModel.Title: [RunicInclude] and [RunicIgnore] contradict each other"),
                $"[RunicInclude] with [RunicIgnore] was not reported as located RUNICBRIDGE077 errors.\n{output}");
            var (_, overridden, _) = await RunGeneratorAsync(generator, temporaryRoot, "IncludeIgnoredOverride", Contradiction
                .Replace("[RunicInclude, RunicIgnore] public string Title", "public string Title", StringComparison.Ordinal)
                .Replace("[RunicIgnore, RunicInclude] public string Title", "public string Title", StringComparison.Ordinal)).ConfigureAwait(false);
            Require(overridden.Contains("error RUNICBRIDGE077: ContradictionViewModel.Note: [RunicInclude] and [RunicIgnore]", StringComparison.Ordinal),
                $"[RunicInclude] on an override of a [RunicIgnore] property was not RUNICBRIDGE077.\n{overridden}");

            const string Unpublishable = Preamble + """
                public abstract class SharedModel : FixtureModel
                {
                    [RunicInclude] public string Inherited { get; set; } = "";
                    [RunicInclude] public virtual string Overridden { get; set; } = "";
                }
                [RunicPublication(RunicPublicationMode.Explicit)]
                public sealed class UnpublishableViewModel : SharedModel
                {
                    [RunicInclude] public string Title { get; set; } = "";
                    public override string Overridden { get; set; } = "";
                    [RunicInclude] public static string Shared { get; set; } = "";
                    [RunicInclude] private string Secret { get; set; } = "";
                    [RunicInclude] public string this[int index] => "";
                    [RunicInclude] public string Field = "";
                    public string Read() => Secret;
                }
                public sealed partial class UnpublishableWindow : RunicWindow<UnpublishableViewModel>;
                """;
            var (published, warningsOutput, warningsDirectory) = await RunGeneratorAsync(generator, temporaryRoot, "IncludeUnpublishable", Unpublishable).ConfigureAwait(false);
            var warnings = warningsOutput.Split('\n').Where(text => text.Contains("warning RUNICBRIDGE078:", StringComparison.Ordinal)).ToArray();
            bool WarningAt(string declaration, string message) => warnings.Any(text =>
                text.StartsWith($"{Path.Combine(warningsDirectory, "Fixture.cs")}({Line(Unpublishable, declaration)},", StringComparison.Ordinal)
                && text.Contains(message, StringComparison.Ordinal)
                && text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE078")}", StringComparison.Ordinal));
            Require(published == 0 && warnings.Length == 5
                    && WarningAt("[RunicInclude] public static string Shared", "UnpublishableViewModel.Shared: [RunicInclude] has no effect here: only a public instance property is published.")
                    && WarningAt("[RunicInclude] private string Secret", "UnpublishableViewModel.Secret: [RunicInclude] has no effect here: only a public instance property is published.")
                    && WarningAt("[RunicInclude] public string this[int index]", "UnpublishableViewModel.Item: [RunicInclude] has no effect here: an indexer is not published.")
                    // A PDB has no location for a field, so its warning points at the class.
                    && WarningAt("public sealed class UnpublishableViewModel", "UnpublishableViewModel.Field: [RunicInclude] has no effect here: a field is not published")
                    && WarningAt("[RunicInclude] public string Inherited", "UnpublishableViewModel.Inherited: [RunicInclude] has no effect here: SharedModel declares it"),
                $"[RunicInclude] where the Bridge publishes nothing was not reported as five located RUNICBRIDGE078 warnings.\n{warningsOutput}");
            // The override inherits the base property's [RunicInclude]; the warnings leave the contract as it is.
            var client = GeneratedClientSource.Read(Path.Combine(warningsDirectory, "ts"), "unpublishable");
            Require(client.Contains("  readonly title: string;", StringComparison.Ordinal) && client.Contains("  readonly overridden: string;", StringComparison.Ordinal)
                    && Lacks(client, "readonly inherited", "readonly shared", "readonly secret", "readonly field"),
                $"The explicit ViewModel's client is not its published members.\n{client}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static bool Lacks(string text, params string[] members) =>
        members.All(member => !text.Contains(member, StringComparison.Ordinal));

    private static string GeneratorPath()
    {
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        return Path.Combine(FindWorkspaceRoot(), "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0", "BridgeCodegen.dll");
    }
}
