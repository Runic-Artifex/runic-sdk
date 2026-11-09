using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// [RunicSharedClient] (#839): the generated shared client, and RUNICBRIDGE036 when an implementing ViewModel no longer
// publishes the interface's members alike or the interface cannot have a shared client.
internal sealed partial class CodegenDiagnosticsTests
{
    private const string SharedPreamble = Preamble + """
        public sealed record ResultRow(string Id, string Label);

        """;

    [Test]
    public async Task SharedClientsAreGeneratedAndDriftIsReportedAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0", "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-shared-{Guid.NewGuid():N}");
        try
        {
            // The name comes from the interface without its leading I; a get-only interface member has no setter in the
            // shared client even where a ViewModel can set it, and a member only one ViewModel has stays its own.
            var valid = await GenerateValidAsync(generator, temporaryRoot, "SharedValid", SharedPreamble + """
                [RunicSharedClient]
                public interface IResultPage
                {
                    IReadOnlyList<ResultRow> Rows { get; }
                    string Title { get; }
                    IRelayCommand<string> RunActionCommand { get; }
                }
                public sealed class OrdersViewModel : FixtureModel, IResultPage
                {
                    public IReadOnlyList<ResultRow> Rows { get; } = [];
                    public string Title { get; set; } = "";
                    public IRelayCommand<string> RunActionCommand { get; } = new RelayCommand<string>(_ => { });
                    public int Open { get; } = 0;
                }
                public sealed class InvoicesViewModel : FixtureModel, IResultPage
                {
                    public IReadOnlyList<ResultRow> Rows { get; } = [];
                    public string Title => "";
                    public IRelayCommand<string> RunActionCommand { get; } = new RelayCommand<string>(_ => { });
                }
                public sealed class PlainViewModel : FixtureModel
                {
                    public string Title => "";
                }
                public sealed partial class OrdersView : RunicView<OrdersViewModel>;
                public sealed partial class InvoicesView : RunicView<InvoicesViewModel>;
                public sealed partial class PlainWindow : RunicWindow<PlainViewModel>;
                """).ConfigureAwait(false);
            var typescript = Path.Combine(Path.GetDirectoryName(valid)!, "ts");
            var shared = File.ReadAllText(Path.Combine(typescript, "resultPage.ts"));
            var orders = File.ReadAllText(Path.Combine(typescript, "orders.ts"));
            var plain = File.ReadAllText(Path.Combine(typescript, "plain.ts"));
            Require(shared.Contains("export interface ResultPageState {", StringComparison.Ordinal)
                    && shared.Contains("  readonly rows: readonly ResultRow[];", StringComparison.Ordinal)
                    && shared.Contains("  readonly title: string;", StringComparison.Ordinal)
                    && shared.Contains("export interface ResultPageClient extends ViewClient<ResultPageState> {", StringComparison.Ordinal)
                    && shared.Contains("  runAction(input: string): Promise<ResultPageState>;", StringComparison.Ordinal)
                    && shared.Contains("  canRunAction(input: string): Promise<boolean>;", StringComparison.Ordinal)
                    && shared.Contains("  readonly kind: \"invoices\" | \"orders\";", StringComparison.Ordinal)
                    && shared.Contains("  connect(): Promise<ResultPageClient>;", StringComparison.Ordinal)
                    && shared.Contains("export const resultPageKinds: readonly ResultPagePageReference[\"kind\"][] = [\"invoices\", \"orders\"];", StringComparison.Ordinal)
                    && !shared.Contains("setTitle", StringComparison.Ordinal) && !shared.Contains("readonly open:", StringComparison.Ordinal),
                $"The shared client is not the members the implementing ViewModels share.\n{shared}");
            Require(orders.Contains("export interface OrdersPageReference extends ResultPagePageReference {", StringComparison.Ordinal)
                    && orders.Contains("  readonly [sharedPageReferences]?: ResultPagePageReference;", StringComparison.Ordinal)
                    && orders.Contains("import type { ResultPagePageReference } from \"./resultPage.js\";", StringComparison.Ordinal)
                    && orders.Contains("setTitle(value: string): Promise<OrdersState>;", StringComparison.Ordinal),
                $"An implementing ViewModel's page reference does not extend the shared one.\n{orders}");
            Require(!plain.Contains("sharedPageReferences", StringComparison.Ordinal) && !plain.Contains("resultPage", StringComparison.Ordinal)
                    && plain.Contains("export interface PlainPageReference {", StringComparison.Ordinal),
                $"A ViewModel without a shared client changed.\n{plain}");

            // Three ViewModels implement IResultPage; the one that differs from the other two is reported.
            const string Interface = """
                [RunicSharedClient]
                public interface IResultPage
                {
                    string Title { get; }
                    IRelayCommand<string> RunActionCommand { get; }
                }

                """;
            string Implementer(string name, string title = "public string Title => \"\";",
                string command = "public IRelayCommand<string> RunActionCommand { get; } = new RelayCommand<string>(_ => { });") => $$"""
                public sealed class {{name}}ViewModel : FixtureModel, IResultPage
                {
                    {{title}}
                    {{command}}
                }
                public sealed partial class {{name}}View : RunicView<{{name}}ViewModel>;

                """;
            await RejectSharedAt(generator, temporaryRoot, "SharedWireName", Interface + Implementer("Orders") + Implementer("Invoices")
                + Implementer("Quotes", title: "[System.Text.Json.Serialization.JsonPropertyName(\"heading\")] public string Title => \"\";"),
                "[System.Text.Json.Serialization.JsonPropertyName(\"heading\")] public string Title",
                "QuotesViewModel.Title: every ViewModel that implements IResultPage.Title of the shared client ResultPage must publish it alike, but QuotesViewModel's client has `readonly heading: string;` where InvoicesViewModel, OrdersViewModel's has `readonly title: string;`").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedCommandFailure", SharedPreamble.Replace("public sealed record ResultRow", "public sealed record Refused(string Reason);\npublic sealed record ResultRow", StringComparison.Ordinal)
                    + Interface + Implementer("Orders") + Implementer("Invoices")
                    + Implementer("Quotes", command: "[RunicFailure(typeof(Refused))] public IRelayCommand<string> RunActionCommand { get; } = new RelayCommand<string>(_ => { });"),
                "[RunicFailure(typeof(Refused))] public IRelayCommand<string> RunActionCommand",
                "QuotesViewModel's client has `runAction(input: string): Promise<BridgeOutcome<ResultPageState, Refused>>;` where InvoicesViewModel, OrdersViewModel's has `runAction(input: string): Promise<ResultPageState>;`",
                prefix: false).ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedExplicit", Interface + Implementer("Orders") + """
                public sealed class InvoicesViewModel : FixtureModel, IResultPage
                {
                    string IResultPage.Title => "";
                    public string Heading => "";
                    public IRelayCommand<string> RunActionCommand { get; } = new RelayCommand<string>(_ => { });
                }
                public sealed partial class InvoicesView : RunicView<InvoicesViewModel>;
                """, "string IResultPage.Title",
                "InvoicesViewModel implements IResultPage.Title of the shared client ResultPage, but implements it explicitly, which the Bridge does not publish; implement it with a public property.").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedIgnored", Interface + Implementer("Orders")
                + Implementer("Invoices", title: "[RunicIgnore] public string Title => \"\";"), "[RunicIgnore] public string Title",
                "InvoicesViewModel implements IResultPage.Title of the shared client ResultPage, but marks Title [RunicIgnore].").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedInherited", Interface + Implementer("Orders") + """
                public abstract class PageBase : FixtureModel
                {
                    public string Title => "";
                }
                public sealed class InvoicesViewModel : PageBase, IResultPage
                {
                    public IRelayCommand<string> RunActionCommand { get; } = new RelayCommand<string>(_ => { });
                }
                public sealed partial class InvoicesView : RunicView<InvoicesViewModel>;
                """, "public sealed class InvoicesViewModel",
                "InvoicesViewModel implements IResultPage.Title of the shared client ResultPage, but inherits it from PageBase, and the Bridge publishes only properties the ViewModel class declares.").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedNullability", """
                [RunicSharedClient]
                public interface IResultPage
                {
                    string? Title { get; }
                }
                public sealed class OrdersViewModel : FixtureModel, IResultPage
                {
                    public string Title => "";
                }
                public sealed partial class OrdersView : RunicView<OrdersViewModel>;
                """, "public string Title",
                "OrdersViewModel.Title is String, but IResultPage.Title of the shared client ResultPage is String?. Declare the same nullability.").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedViewRequest", """
                [RunicSharedClient]
                public interface IResultPage
                {
                    ViewRequest<string> Focus { get; }
                }
                public sealed class OrdersViewModel : FixtureModel, IResultPage
                {
                    public ViewRequest<string> Focus { get; } = new();
                }
                public sealed partial class OrdersView : RunicView<OrdersViewModel>;
                """, "public ViewRequest<string> Focus",
                "OrdersViewModel.Focus: IResultPage.Focus of the shared client ResultPage is a view request, which a shared client does not include. Remove it from the interface.").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedGeneric", """
                [RunicSharedClient]
                public interface IResultPage<T>
                {
                    T Title { get; }
                }
                public sealed class OrdersViewModel : FixtureModel, IResultPage<string>
                {
                    public string Title => "";
                }
                public sealed partial class OrdersView : RunicView<OrdersViewModel>;
                """, "public interface IResultPage<T>",
                "IResultPage: a [RunicSharedClient] interface cannot be generic").ConfigureAwait(false);
            await RejectSharedAt(generator, temporaryRoot, "SharedName", """
                [RunicSharedClient(Name = "Result-Page")]
                public interface IResultPage
                {
                    string Title { get; }
                }
                public sealed class OrdersViewModel : FixtureModel, IResultPage
                {
                    public string Title => "";
                }
                public sealed partial class OrdersView : RunicView<OrdersViewModel>;
                """, "public interface IResultPage",
                "IResultPage: the shared client name 'Result-Page' must contain only letters and digits and start with a letter.").ConfigureAwait(false);
            // The shared module and a ViewModel's module cannot have the same file.
            var collision = await GenerateAsync(generator, temporaryRoot, "SharedFileCollision", Preamble + """
                [RunicSharedClient]
                public interface IOrders
                {
                    string Title { get; }
                }
                public sealed class OrdersViewModel : FixtureModel, IOrders
                {
                    public string Title => "";
                }
                public sealed partial class OrdersView : RunicView<OrdersViewModel>;
                """).ConfigureAwait(false);
            Require(collision.Contains("error RUNICBRIDGE004: generated file 'orders.ts' would be written by", StringComparison.Ordinal),
                $"A shared client named like a ViewModel was not RUNICBRIDGE004.\n{collision}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // RUNICBRIDGE036 at the declaration's line, ending with its help link, with the expected text.
    private static async Task RejectSharedAt(string generator, string temporaryRoot, string name, string members, string declaration,
        string expected, bool prefix = true)
    {
        // Without the prefix, members is the whole source.
        var source = (prefix ? SharedPreamble : "") + members;
        var output = await GenerateAsync(generator, temporaryRoot, name, source).ConfigureAwait(false);
        var line = source[..source.IndexOf(declaration, StringComparison.Ordinal)].Count(character => character == '\n') + 1;
        var location = $"{Path.Combine(temporaryRoot, name, "Fixture.cs")}({line},";
        var errors = output.Split('\n').Where(text => text.Contains(": error RUNICBRIDGE036: ", StringComparison.Ordinal)).ToArray();
        Require(errors.Any(text => text.StartsWith(location, StringComparison.Ordinal)),
            $"{name}: expected 'error RUNICBRIDGE036' at line {line} of the fixture.\n{output}");
        Require(errors.All(text => text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE036")}", StringComparison.Ordinal)),
            $"{name}: expected RUNICBRIDGE036 to end with its help link.\n{output}");
        Require(output.Contains(expected, StringComparison.Ordinal), $"{name}: the generator output did not contain '{expected}'.\n{output}");
    }
}
