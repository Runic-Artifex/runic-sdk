using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// The View fingerprint (runic.views.fingerprint/1, specs/application/contract-ir.md, "Fingerprint"): a hash of each
// View's contract IR, which the generated Bridge, its client and the contracts of its interactions and view requests
// carry. eng/views-contract-ir.test.mjs recomputes it from the corpus without .NET.
internal sealed partial class CodegenDiagnosticsTests
{
    [Test]
    public async Task EachViewHasTheFingerprintOfItsOwnContractAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0", "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-fingerprint-{Guid.NewGuid():N}");
        try
        {
            static string Source(string counterMember) => Preamble + $$"""
                public sealed class CounterViewModel : FixtureModel
                {
                    public int {{counterMember}} => 0;
                    public ViewQuestion<string, bool> Confirm { get; } = new();
                    public ViewRequest<string> Focus { get; } = new();
                }
                /// <summary>The documentation is not part of the contract.</summary>
                public sealed class NotesViewModel : FixtureModel
                {
                    public string Title => "";
                }
                public sealed partial class CounterWindow : RunicWindow<CounterViewModel>;
                public sealed partial class NotesView : RunicView<NotesViewModel>;
                """;
            var first = await GenerateValidAsync(generator, temporaryRoot, "FingerprintFirst", Source("Count")).ConfigureAwait(false);
            var second = await GenerateValidAsync(generator, temporaryRoot, "FingerprintSecond", Source("Total")).ConfigureAwait(false);
            static Dictionary<string, JsonObject> Views(string directory) =>
                JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "contract.ir.json")))!["views"]!.AsArray()
                    .Select(view => view!.AsObject()).ToDictionary(view => view["id"]!.GetValue<string>());
            var before = Views(first);
            var after = Views(second);
            string Fingerprint(Dictionary<string, JsonObject> views, string id) => views[id]["fingerprint"]!.GetValue<string>();

            foreach (var (directory, views) in new[] { (first, before), (second, after) })
                foreach (var (id, view) in views)
                {
                    var fingerprint = Fingerprint(views, id);
                    Require(Regex.IsMatch(fingerprint, "^[0-9A-F]{64}$"), $"{id}: {fingerprint} is not an uppercase SHA-256.");
                    var bridge = File.ReadAllText(Path.Combine(directory, $"{id}.Bridge.g.cs"));
                    Require(bridge.Contains($"\"{fingerprint}\", content,", StringComparison.Ordinal),
                        $"{id}: the Bridge does not carry the IR's fingerprint {fingerprint}.\n{bridge}");
                    var client = File.ReadAllText(Path.Combine(Path.GetDirectoryName(directory)!, "ts", view["route"]!.GetValue<string>() + ".ts"));
                    Require(client.Contains($"\"{id}:{fingerprint}\"", StringComparison.Ordinal),
                        $"{id}: the client does not carry the contract {id}:{fingerprint}.");
                }
            // A change to one View leaves every other View's fingerprint, and the operations started under it, alone.
            Require(Fingerprint(before, "Fixture.CounterViewModel") != Fingerprint(after, "Fixture.CounterViewModel"),
                "A renamed state member left the View's fingerprint as it was.");
            Require(Fingerprint(before, "Fixture.NotesViewModel") == Fingerprint(after, "Fixture.NotesViewModel"),
                "A change to another View changed this View's fingerprint.");
            Require(Fingerprint(before, "Fixture.CounterViewModel") != Fingerprint(before, "Fixture.NotesViewModel"),
                "Two Views of different contracts share a fingerprint.");

            // The interaction's and view request's contracts derive from the fingerprint and the IR name.
            var counter = before["Fixture.CounterViewModel"];
            var counterFingerprint = Fingerprint(before, "Fixture.CounterViewModel");
            Require(counter["interactions"]![0]!["contract"]!.GetValue<string>() == $"{counterFingerprint}:interaction:Confirm"
                    && counter["requests"]![0]!["contract"]!.GetValue<string>() == $"{counterFingerprint}:request:Focus",
                $"The interaction and view request contracts are not derived from the fingerprint: {counter.ToJsonString()}");
            var counterBridge = File.ReadAllText(Path.Combine(first, "Fixture.CounterViewModel.Bridge.g.cs"));
            Require(counterBridge.Contains($"\"{counterFingerprint}:interaction:Confirm\"", StringComparison.Ordinal)
                    && counterBridge.Contains($"\"{counterFingerprint}:request:Focus\"", StringComparison.Ordinal),
                $"The Bridge's interaction and view request descriptors do not carry the derived contracts.\n{counterBridge}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}
