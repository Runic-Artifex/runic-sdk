using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The contract IR (specs/application/contract-ir.md) of this assembly's fixtures, which its build writes to
// obj/<configuration>/net10.0/runic-bridge/contract.ir.json. Each golden set pins the Views of the set, the named types
// and shared clients they reach and their source files, as golden/<set>/contract.ir.json.golden: the sets that pin the
// generated C# and TypeScript of the same Views, and contract-ir for the rest of the IR's surface. Fingerprints cover
// every View of the assembly, and source lines move with unrelated edits, so the goldens hold neither. Set
// RUNIC_UPDATE_GOLDEN=1 to rewrite them after an intended change.
internal sealed class ContractIrGoldenTests
{
    private static readonly Regex Fingerprint = new("[0-9A-F]{64}", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Test]
    [Arguments("development-only", new[] { "developmentTools", "leftOutTools", "developmentPanel" })]
    [Arguments("domain-failures", new[] { "failureToolkit", "failureReactive" })]
    [Arguments("navigation-region", new[] { "navShell" })]
    [Arguments("explicit-publication", new[] { "importResult" })]
    [Arguments("field-paths", new[] { "pathGallery", "pathInvoice", "pathRows" })]
    [Arguments("mvvm-conventions", new[] { "documented", "generatedInteraction", "generatedReactive", "reactivePassword", "toolkitCancel",
        "toolkitTyped", "typedReactive" })]
    [Arguments("view-requests", new[] { "reactiveViewRequest", "requestOnly", "toolkitViewRequest" })]
    [Arguments("wire-keys", new[] { "keyedPlain", "keyedReactive", "keyedToolkit" })]
    // Shared clients, query and region collections, selections, blobs and uploads, custom codecs, JSON and maps, text
    // references, constraints, append-only text, scoped commands and interactions.
    [Arguments("contract-ir", new[] { "sharedInvoices", "sharedOrders", "catalog", "navDocuments", "navTabs", "blob", "dataShape",
        "codegenShape", "translatedText", "forms", "textDelta", "scopedWizard", "dtoInteraction" })]
    [DisplayName("ContractIrMatchesTheGoldenFile(set: $set)")]
    public void ContractIrMatchesTheGoldenFile(string set, string[] routes)
    {
        var root = DomainFailureCodegenTests.FindWorkspaceRoot();
        var project = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests");
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(ContractIrGoldenTests).Assembly)?.Configuration ?? "Debug";
        var path = Path.Combine(project, "obj", configuration, "net10.0", "runic-bridge", "contract.ir.json");
        var ir = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Require(ir["format"]!.GetValue<string>() == "runic.views.contract-ir/1", $"{path} has another format.");
        var actual = Select(ir, routes, root).ToJsonString(JsonOptions) + "\n";
        var golden = Path.Combine(project, "golden", set, "contract.ir.json.golden");
        if (Environment.GetEnvironmentVariable("RUNIC_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, actual);
            return;
        }
        Require(File.Exists(golden) && File.ReadAllText(golden).ReplaceLineEndings("\n") == actual,
            $"golden/{set}/contract.ir.json.golden differs from the IR in {path}. Review the change and run with RUNIC_UPDATE_GOLDEN=1.");
    }

    // The Views of the routes in their order, with the types and shared clients they reach and their source files.
    private static JsonObject Select(JsonObject ir, string[] routes, string root)
    {
        var all = ir["views"]!.AsArray().Select(view => view!.AsObject()).ToArray();
        var views = routes.Select(route => all.SingleOrDefault(view => view["route"]!.GetValue<string>() == route)
            ?? throw new InvalidOperationException($"The IR has no View with route {route}.")).ToArray();
        var ids = views.Select(view => view["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var shared = (ir["sharedClients"]?.AsArray() ?? []).Select(client => client!.AsObject())
            .Where(client => client["views"]!.AsArray().Any(view => ids.Contains(view!.GetValue<string>()))).ToArray();

        // The named types the Views reach, through their members, and the objects of union cases.
        var types = ir["types"]!.AsObject();
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject item:
                    if (item["kind"]?.GetValueKind() == JsonValueKind.String && item["kind"]!.GetValue<string>() == "named"
                        && item["ref"]!.GetValue<string>() is var reference && reached.Add(reference))
                        Visit(types[reference]);
                    if (item["object"] is JsonValue unionObject && reached.Add(unionObject.GetValue<string>()))
                        Visit(types[unionObject.GetValue<string>()]);
                    foreach (var (_, child) in item) Visit(child);
                    break;
                case JsonArray list:
                    foreach (var child in list) Visit(child);
                    break;
            }
        }
        foreach (var view in views) Visit(view);

        // Source files by id, relative to the workspace root.
        var sources = new JsonObject();
        foreach (var (id, location) in ir["sources"]!.AsObject())
            if (ids.Contains(id) || ids.Any(view => id.StartsWith(view + "/", StringComparison.Ordinal)))
                sources[id] = Path.GetRelativePath(root, location!["file"]!.GetValue<string>()).Replace('\\', '/');

        var result = new JsonObject
        {
            ["format"] = ir["format"]!.DeepClone(),
            ["producer"] = ir["producer"]!.DeepClone(),
            ["views"] = new JsonArray([.. views.Select(view => (JsonNode)WithoutFingerprints(view))]),
        };
        if (shared.Length > 0) result["sharedClients"] = new JsonArray([.. shared.Select(client => client.DeepClone())]);
        result["types"] = new JsonObject(reached.Select(id => KeyValuePair.Create(id, types[id]?.DeepClone())));
        result["sourceFiles"] = sources;
        return result;
    }

    private static JsonNode WithoutFingerprints(JsonObject view) =>
        JsonNode.Parse(Fingerprint.Replace(view.ToJsonString(), "<fingerprint>"))!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
