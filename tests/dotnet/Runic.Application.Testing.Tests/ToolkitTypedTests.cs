using System.Text.Json;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

internal static class ToolkitTypedTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Encode(object? value) => JsonSerializer.Serialize(value, Json);

    internal static async Task RunAsync()
    {
        var model = new ToolkitTypedViewModel();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model, "toolkitTyped",
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content), new TestViewLocator());

        AssertSyncCommands(host, model);
        await AssertAsyncCommandsAsync(host, model);
        await AssertCancellationAsync(host, model);
    }

    private static void AssertSyncCommands(RunicWindowTestHost<ToolkitTypedViewModel> host, ToolkitTypedViewModel model)
    {
        using var applied = JsonDocument.Parse(host.Transport.Call("toolkitTypedApply",
            new(StringValue: (Encode(new ToolkitCommandRequest("document-1", 3))))));
        Require(applied.RootElement.GetProperty("ok").GetBoolean() && model.Total == 3,
            "A typed Toolkit DTO command did not decode and execute.");

        using var unavailableDto = JsonDocument.Parse(host.Transport.Call("toolkitTypedApply",
            new(StringValue: (Encode(new ToolkitCommandRequest("other", 3))))));
        Require(!unavailableDto.RootElement.GetProperty("ok").GetBoolean()
            && unavailableDto.RootElement.GetProperty("error").GetProperty("kind").GetString() == "rejected"
            && model.Total == 3,
            "A parameter-sensitive Toolkit DTO CanExecute was bypassed.");

        using var added = JsonDocument.Parse(host.Transport.Call("toolkitTypedAdd", new(StringValue: ("4"))));
        Require(added.RootElement.GetProperty("ok").GetBoolean() && model.Total == 7,
            "A typed Toolkit integer command did not decode and execute.");
        using var unavailableInt = JsonDocument.Parse(host.Transport.Call("toolkitTypedAdd", new(StringValue: ("0"))));
        Require(!unavailableInt.RootElement.GetProperty("ok").GetBoolean() && model.Total == 7,
            "A parameter-sensitive Toolkit integer CanExecute was bypassed.");

        using var renamed = JsonDocument.Parse(host.Transport.Call("toolkitTypedRename",
            new(StringValue: (Encode("sync label")))));
        Require(renamed.RootElement.GetProperty("ok").GetBoolean() && model.Label == "sync label",
            "A typed Toolkit string command did not decode and execute.");

        using var optional = JsonDocument.Parse(host.Transport.Call("toolkitTypedOptional", new(StringValue: ("null"))));
        Require(optional.RootElement.GetProperty("ok").GetBoolean() && model.OptionalDocumentId is null,
            "A nullable Toolkit DTO command did not accept a null input.");
    }

    private static async Task AssertAsyncCommandsAsync(RunicWindowTestHost<ToolkitTypedViewModel> host,
        ToolkitTypedViewModel model)
    {
        await StartAndCompleteAsync(host, "AsyncDto", "toolkit-async-dto",
            new ToolkitCommandRequest("document-1", 2));
        Require(model.AsyncDocumentId == "document-1" && model.Total == 9,
            "A typed asynchronous Toolkit DTO command did not complete.");

        await StartAndCompleteAsync(host, "AsyncInt", "toolkit-async-int", 3);
        Require(model.Total == 12, "A typed asynchronous Toolkit integer command did not complete.");

        await StartAndCompleteAsync(host, "AsyncString", "toolkit-async-string", "async label");
        Require(model.AsyncLabel == "async label", "A typed asynchronous Toolkit string command did not complete.");

        await StartAndCompleteAsync(host, "AsyncOptional", "toolkit-async-optional", input: null);
        Require(model.AsyncOptionalDocumentId is null,
            "A nullable asynchronous Toolkit DTO command did not preserve null input.");

        using var unavailable = JsonDocument.Parse(host.Transport.Call("toolkitTypedStartAsyncDto", new(StringValue: (
            Encode(new { requestId = "toolkit-async-unavailable", input = new ToolkitCommandRequest("other", 1) })))));
        Require(unavailable.RootElement.GetProperty("kind").GetString() == "rejected"
            && unavailable.RootElement.GetProperty("reason").GetString() == "unavailable",
            "An asynchronous Toolkit command ignored its parameter-sensitive CanExecute.");
    }

    private static async Task AssertCancellationAsync(RunicWindowTestHost<ToolkitTypedViewModel> host,
        ToolkitTypedViewModel model)
    {
        const string requestId = "toolkit-cancel";
        using var admitted = JsonDocument.Parse(host.Transport.Call("toolkitTypedStartCancel", new(StringValue: (
            Encode(new { requestId, input = "wait" })))));
        Require(admitted.RootElement.GetProperty("kind").GetString() == "accepted",
            "The cancellable Toolkit command was not admitted.");
        var contract = admitted.RootElement.GetProperty("contract").GetString()
            ?? throw new InvalidOperationException("The cancellable Toolkit command did not return its contract.");
        using var cancellation = JsonDocument.Parse(host.Transport.Call("__runicOperationCancel", new(StringValue: (
            Encode(new { contract, member = "Cancel", requestId })))));
        Require(cancellation.RootElement.GetProperty("kind").GetString() == "cancellation-requested",
            "The bridge did not forward cancellation to the Toolkit operation.");
        using var terminal = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait", new(StringValue: (
            Encode(new { contract, member = "Cancel", requestId })))));
        Require(terminal.RootElement.GetProperty("kind").GetString() == "cancelled" && model.CancelledCount == 1,
            "A cancelled Toolkit command did not observe command-level cancellation.");
    }

    private static async Task StartAndCompleteAsync(RunicWindowTestHost<ToolkitTypedViewModel> host,
        string command, string requestId, object? input)
    {
        using var admitted = JsonDocument.Parse(host.Transport.Call($"toolkitTypedStart{command}", new(StringValue: (
            Encode(new { requestId, input })))));
        Require(admitted.RootElement.GetProperty("kind").GetString() == "accepted",
            $"The asynchronous Toolkit {command} command was not admitted.");
        var contract = admitted.RootElement.GetProperty("contract").GetString()
            ?? throw new InvalidOperationException($"The asynchronous Toolkit {command} command did not return its contract.");
        using var terminal = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait", new(StringValue: (
            Encode(new { contract, member = command, requestId })))));
        Require(terminal.RootElement.GetProperty("kind").GetString() == "succeeded",
            $"The asynchronous Toolkit {command} command did not complete.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
