using System.Reflection;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// #1320: a generated Bridge in explicit publication sends only the members its contract has, and the generator and the
// runtime's contract fingerprint (BridgeContractShape, which Hot Reload compares) agree on them.
internal sealed class PublicationTests
{
    [Test]
    public async Task AnExplicitBridgeSendsOnlyItsPublishedMembersAsync()
    {
        var model = new ImportResultViewModel();
        ImportResultBridge? bridge = null;
        using var host = new RunicWindowTestHost<ImportResultViewModel>(model,
            (transport, content, vm) => bridge = new ImportResultBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "importResult" });
        var driver = host.Root;
        var state = driver.Snapshot();
        foreach (var member in new[] { "title", "summary", "canRetry" })
            Require(state.Json.TryGetProperty(member, out _), $"The state lacks the published {member}: {state.Json}");
        foreach (var member in new[] { "imported", "skipped", "showSkipped", "retries", "helpShown", "canShowHelp", "isShowHelpExecuting" })
            Require(!state.Json.TryGetProperty(member, out _), $"The state has {member}, which the contract omits: {state.Json}");

        // A change of an unpublished member sends nothing of it; a published one still reaches the page.
        model.ShowSkipped = true;
        model.Title = "Done";
        state = driver.Snapshot();
        Require(state.Json.GetProperty("title").GetString() == "Done" && !state.Json.TryGetProperty("showSkipped", out _),
            $"The state after a change is not the published members: {state.Json}");
        (await driver.ExecuteAsync(vm => vm.RetryCommand)).ThrowIfFailed();
        Require(model.Retries == 1, "The included command did not run.");

        // The generator and the runtime select the same members.
        Require(BridgeContractShape.Compute(typeof(ImportResultViewModel)) == ContractFingerprint(bridge!),
            "The generated fingerprint differs from BridgeContractShape for an explicit ViewModel.");
        var members = BridgeContractShape.Parts(typeof(ImportResultViewModel))
            .Where(part => part.StartsWith($"member:{typeof(ImportResultViewModel).FullName}:", StringComparison.Ordinal))
            .Select(part => part.Split(':')[2]).Order(StringComparer.Ordinal).ToArray();
        Require(members.SequenceEqual(["RetryCommand", "Summary", "Title"]),
            $"The contract's members are not the included and shared ones: {string.Join(", ", members)}");
    }

    // The generated client and Bridge of the explicit ViewModel. Set RUNIC_UPDATE_GOLDEN=1 to rewrite them after an
    // intended change.
    [Test]
    public void ExplicitPublicationOutputMatchesTheGoldenFiles()
    {
        var project = Path.Combine(DomainFailureCodegenTests.FindWorkspaceRoot(), "tests", "dotnet", "Runic.Application.Testing.Tests");
        var generated = Path.Combine(project, "obj", "bridge-frontend", "generated");
        var configuration = typeof(PublicationTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var bridges = Path.Combine(project, "obj", configuration, "net10.0", "runic-bridge");
        // #1317: the command list offers the published Retry, not ShowHelp, which the native view kept.
        var contract = File.ReadAllText(Path.Combine(generated, "importResult.contract.d.ts"));
        Require(contract.Contains("export type ImportResultCommandName = \"retry\";", StringComparison.Ordinal)
                && contract.Contains("  readonly [viewCommands]?: ImportResultCommandName;", StringComparison.Ordinal),
            "The command list of the explicit ViewModel is not its published commands:\n" + contract);
        DomainFailureCodegenTests.CompareGolden(project, "explicit-publication",
        [
            ("importResult.contract.d.ts", Path.Combine(generated, "importResult.contract.d.ts")),
            ("importSummary.ts", Path.Combine(generated, "importSummary.ts")),
            ("ImportResultViewModel.Bridge.g.cs", Path.Combine(bridges, typeof(ImportResultViewModel).FullName + ".Bridge.g.cs")),
        ]);
    }

    private static string? ContractFingerprint(ViewModelBridge bridge) =>
        typeof(ViewModelBridge).GetField("_contractFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bridge) as string;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
