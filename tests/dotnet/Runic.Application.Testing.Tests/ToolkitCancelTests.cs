using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class ToolkitCancelTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Encode(object value) => JsonSerializer.Serialize(value, Json);

    internal static async Task RunAsync()
    {
        var model = new ToolkitCancelViewModel();
        using var host = CreateHost(model);
        using (var idle = host.Snapshot())
            Require(!idle.RootElement.GetProperty("state").GetProperty("canSaveCancel").GetBoolean(),
                "The generated cancel command must be unavailable while idle.");
        using (var unavailable = JsonDocument.Parse(host.Transport.Call("toolkitCancelSaveCancel")))
            Require(!unavailable.RootElement.GetProperty("ok").GetBoolean(),
                "An idle generated cancel command must reject execution.");

        await CancelOperationAsync(host, model, "Save", "bridge-cancel", nativeCancel: false);
        await CancelOperationAsync(host, model, "Export", "typed-native-cancel", nativeCancel: true);
        Require(model.ExportInput == "document", "The paired async command lost its typed input.");
        Require(model.CancelledCount == 2, "Generated cancellation did not reach both actual Toolkit commands.");
        await CancelOperationAsync(host, model, "Background", "static-source-cancel", nativeCancel: false);

        // A native invocation has no bridge operation, but the same generated
        // cancel property must cancel it and publish its CanExecuteChanged.
        var nativeExecution = model.SaveCommand.ExecuteAsync(null);
        Require(await PublishedAsync(host.Transport, state => state.GetProperty("canSaveCancel").GetBoolean()),
            "Native Toolkit execution did not publish generated cancel availability.");
        using (var cancellation = JsonDocument.Parse(host.Transport.Call("toolkitCancelSaveCancel")))
            Require(cancellation.RootElement.GetProperty("ok").GetBoolean(),
                "The bridge could not cancel a native Toolkit execution.");
        await RequireCancelledAsync(nativeExecution);
        Require(await PublishedAsync(host.Transport, state => !state.GetProperty("canSaveCancel").GetBoolean()),
            "Generated cancel availability did not return to false after cancellation.");

        var lifetimeModel = new ToolkitCancelViewModel();
        var lifetimeHost = CreateHost(lifetimeModel);
        using var admission = Start(lifetimeHost, "Save", "lifetime");
        Require(await PublishedAsync(lifetimeHost.Transport, state => state.GetProperty("canSaveCancel").GetBoolean()),
            "The lifetime-owned Toolkit operation did not begin execution.");
        var lifetimeExecution = lifetimeModel.SaveCommand.ExecutionTask!;
        lifetimeHost.Dispose();
        await RequireCancelledAsync(lifetimeExecution);
        Require(lifetimeModel.CancelledCount == 1, "View disposal no longer cancelled the actual Toolkit operation.");
    }

    private static RunicWindowTestHost<ToolkitCancelViewModel> CreateHost(ToolkitCancelViewModel model) =>
        new(model, "toolkitCancel", (transport, content, vm) => new ToolkitCancelBridge(transport, vm, content: content), new TestViewLocator());

    private static JsonDocument Start(RunicWindowTestHost<ToolkitCancelViewModel> host, string command, string requestId) =>
        JsonDocument.Parse(host.Transport.Call($"toolkitCancelStart{command}", new(StringValue:
            command == "Export" ? Encode(new { requestId, input = "document" }) : requestId)));

    private static async Task CancelOperationAsync(RunicWindowTestHost<ToolkitCancelViewModel> host,
        ToolkitCancelViewModel model, string command, string requestId, bool nativeCancel)
    {
        using var admission = Start(host, command, requestId);
        Require(admission.RootElement.GetProperty("kind").GetString() == "accepted", "The async Toolkit operation was not admitted.");
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        Require(contract.Split(':')[1] == BridgeContractShape.Compute(typeof(ToolkitCancelViewModel)),
            "The generated cancel property disagreed with the reconstructed bridge fingerprint.");
        Require(await PublishedAsync(host.Transport, state => state.GetProperty($"can{command}Cancel").GetBoolean()),
            "The generated Toolkit cancel command did not publish its availability.");
        if (nativeCancel)
            model.ExportCancelCommand.Execute(null);
        else
        {
            using var cancellation = JsonDocument.Parse(host.Transport.Call($"toolkitCancel{command}Cancel"));
            Require(cancellation.RootElement.GetProperty("ok").GetBoolean(), "The generated cancel command failed through the bridge.");
        }
        using var terminal = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait", new(StringValue:
            Encode(new { contract, member = command, requestId }))));
        Require(terminal.RootElement.GetProperty("kind").GetString() == "cancelled",
            "Toolkit cancel execution did not finish the original bridge operation as cancelled.");
        using var idle = host.Snapshot();
        Require(!idle.RootElement.GetProperty("state").GetProperty($"can{command}Cancel").GetBoolean(),
            "A finished Toolkit operation left its generated cancel command available.");
    }

    private static async Task RequireCancelledAsync(Task execution)
    {
        try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The actual Toolkit execution was not cancelled.");
    }

    private static async Task<bool> PublishedAsync(InMemoryViewTransport transport, Func<JsonElement, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var publication in transport.DrainPublications().Where(publication => publication.Route == "toolkitCancel"))
            {
                using var state = JsonDocument.Parse(publication.StateJson);
                if (predicate(state.RootElement)) return true;
            }
            await Task.Delay(10);
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
