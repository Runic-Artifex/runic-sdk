using System.Reflection;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// D072.12/D072.13 (#1109 request 3): development-only views and members. Builds, dotnet run and untrimmed publishes keep
// them; a trimmed or NativeAOT publish turns RunicDevelopmentOnly.IsSupported off, which these tests stand in for with
// the feature switch (the assembly's tests run one at a time). The generated client is the same in both
// (developmentTools.contract.d.ts in CodegenShapeTests' sense: optional state members, notes on the calls).
internal sealed class DevelopmentOnlyTests
{
    // The development-only members of DevelopmentToolsViewModel, in declaration order.
    private static readonly string[] MarkedMembers = ["Debug", "Traces", "ResetCommand", "InjectCommand"];

    [Test]
    public async Task DevelopmentKeepsDevelopmentOnlyMembersAsync()
    {
        var model = new DevelopmentToolsViewModel();
        DevelopmentToolsBridge? bridge = null;
        using var host = new RunicWindowTestHost<DevelopmentToolsViewModel>(model,
            (transport, content, vm) => bridge = new DevelopmentToolsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "developmentTools", ViewLocator = new TestViewLocator() });
        var driver = host.Root;
        var state = driver.Snapshot();
        foreach (var member in new[] { "title", "debug", "traces", "resets", "canReset", "isInjectExecuting" })
            Require(state.Json.TryGetProperty(member, out _), $"The state lacks {member}: {state.Json}");
        (await driver.ExecuteAsync(vm => vm.ResetCommand)).ThrowIfFailed();
        (await driver.ExecuteAsync(vm => vm.InjectCommand, "\"fault\"")).ThrowIfFailed();
        (await driver.SetAsync(vm => vm.Debug, "noted")).ThrowIfFailed();
        Require(model.Resets == 1 && model.Debug == "noted", "A development-only member did not run.");
        // Marking a member changes the contract, and the generator and the runtime agree on it.
        Require(BridgeContractShape.Compute(typeof(DevelopmentToolsViewModel)) == ContractFingerprint(bridge!),
            "The generated fingerprint differs from BridgeContractShape for development-only members.");
        // The parts cover every ViewModel of the assembly; these are this one's.
        var marked = BridgeContractShape.Parts(typeof(DevelopmentToolsViewModel))
            .Where(part => part.StartsWith($"development-only:{typeof(DevelopmentToolsViewModel).FullName}:", StringComparison.Ordinal)).ToArray();
        Require(marked.SequenceEqual(MarkedMembers.Select(member => $"development-only:{typeof(DevelopmentToolsViewModel).FullName}:{member}")),
            $"The contract does not name exactly the four development-only members: {string.Join(", ", marked)}");
    }

    [Test]
    public async Task APublishLeavesDevelopmentOnlyMembersOutAsync()
    {
        AppContext.SetSwitch(RunicDevelopmentOnly.FeatureSwitch, false);
        try
        {
            var model = new LeftOutToolsViewModel();
            using var host = new RunicWindowTestHost<LeftOutToolsViewModel>(model,
                (transport, content, vm) => new LeftOutToolsBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { RootRoute = "leftOutTools", ViewLocator = new TestViewLocator() });
            var driver = host.Root;
            var state = driver.Snapshot();
            Require(state.Json.TryGetProperty("title", out _) && state.Json.TryGetProperty("resets", out _),
                $"The state lost a member that is not development-only: {state.Json}");
            foreach (var member in new[] { "debug", "traces", "canReset", "isInjectExecuting" })
                Require(!state.Json.TryGetProperty(member, out _), $"The state has the left-out {member}: {state.Json}");

            // The commands keep their names and are rejected as unavailable without running.
            foreach (var reply in new[] { await driver.ExecuteAsync(vm => vm.ResetCommand), await driver.ExecuteAsync(vm => vm.InjectCommand, "\"fault\"") })
                Require(reply is { ErrorKind: "rejected", ErrorReason: "unavailable" }, $"A left-out command answered {reply}.");
            var operation = await driver.StartAsync(vm => vm.InjectCommand, "\"fault\"");
            Require(operation is { Admission: RunicOperationAdmissionKind.Rejected, Reason: "unavailable" },
                $"A left-out operation was not rejected as unavailable: {operation.AdmissionJson}");
            Require(model.Resets == 0, "A left-out command ran.");
            // A left-out property cannot be set.
            Require((await driver.SetAsync(vm => vm.Debug, "noted")).ErrorKind == "rejected" && model.Debug == "debug",
                "A left-out property was set.");
            // The rest of the ViewModel works.
            (await driver.ExecuteAsync(vm => vm.RenameCommand, "\"renamed\"")).ThrowIfFailed();
            Require(model.Title == "renamed", "A command that is not development-only did not run.");

            // A development-only ViewModel has no Bridge: attaching one throws and names it.
            var error = Catch(() => new DevelopmentPanelWindow().AttachRunicBridge(null!, "developmentPanel", null!));
            Require(error is InvalidOperationException && error.Message.Contains(typeof(DevelopmentPanelViewModel).FullName!, StringComparison.Ordinal),
                $"Attaching a left-out ViewModel did not fail with its name: {error}");
        }
        finally
        {
            AppContext.SetSwitch(RunicDevelopmentOnly.FeatureSwitch, true);
        }
    }

    // The generated client, its declarations and the Bridge of development-only members. Set RUNIC_UPDATE_GOLDEN=1 to
    // rewrite them after an intended change.
    [Test]
    public void DevelopmentOnlyOutputMatchesTheGoldenFiles()
    {
        var project = Path.Combine(DomainFailureCodegenTests.FindWorkspaceRoot(), "tests", "dotnet", "Runic.Application.Testing.Tests");
        var generated = Path.Combine(project, "obj", "bridge-frontend", "generated");
        var configuration = typeof(DevelopmentOnlyTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var bridges = Path.Combine(project, "obj", configuration, "net10.0", "runic-bridge");
        DomainFailureCodegenTests.CompareGolden(project, "development-only",
        [
            ("developmentTools.ts", Path.Combine(generated, "developmentTools.ts")),
            ("developmentTools.contract.d.ts", Path.Combine(generated, "developmentTools.contract.d.ts")),
            ("DevelopmentToolsViewModel.Bridge.g.cs", Path.Combine(bridges, typeof(DevelopmentToolsViewModel).FullName + ".Bridge.g.cs")),
            ("DevelopmentPanelWindow.View.g.cs", Path.Combine(bridges, typeof(DevelopmentPanelWindow).FullName + ".View.g.cs")),
        ]);
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static string? ContractFingerprint(ViewModelBridge bridge) =>
        typeof(ViewModelBridge).GetField("_contractFingerprint", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(bridge) as string;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
