using System.Text.Json;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ToolkitTypedTests
{
    private const string Route = "toolkitTyped";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Encode(object? value) => JsonSerializer.Serialize(value, Json);

    [Test]
    public async Task ToolkitTypedCommandsCrossTheBridgeAsync()
    {
        var model = new ToolkitTypedViewModel();
        using var host = Host(model);

        AssertSyncCommands(host, model);
        await AssertAsyncCommandsAsync(host, model);
    }

    // The asynchronous commands started as operations, and the cancellation of a running one.
    [Test]
    [Skip("P5: needs start, watch and cancel served")]
    public async Task ToolkitTypedOperationsStartAndCancelAsync()
    {
        var model = new ToolkitTypedViewModel();
        using var host = Host(model);

        await StartAndCompleteAsync(host, vm => vm.AsyncDtoCommand, "toolkit-async-dto",
            Encode(new ToolkitCommandRequest("document-1", 2)));
        Require(model.AsyncDocumentId == "document-1" && model.Total == 2,
            "A typed asynchronous Toolkit DTO command did not complete.");
        await StartAndCompleteAsync(host, vm => vm.AsyncIntCommand, "toolkit-async-int", "3");
        Require(model.Total == 5, "A typed asynchronous Toolkit integer command did not complete.");
        await StartAndCompleteAsync(host, vm => vm.AsyncStringCommand, "toolkit-async-string", Encode("async label"));
        Require(model.AsyncLabel == "async label", "A typed asynchronous Toolkit string command did not complete.");
        await StartAndCompleteAsync(host, vm => vm.AsyncOptionalCommand, "toolkit-async-optional", "null");
        Require(model.AsyncOptionalDocumentId is null,
            "A nullable asynchronous Toolkit DTO command did not preserve null input.");

        var unavailable = host.Root.Start(vm => vm.AsyncDtoCommand, Encode(new ToolkitCommandRequest("other", 1)),
            "toolkit-async-unavailable");
        Require(unavailable.Admission == RunicOperationAdmissionKind.Rejected && unavailable.Reason == "unavailable",
            $"An asynchronous Toolkit command ignored its parameter-sensitive CanExecute: {unavailable.AdmissionJson}");

        await AssertCancellationAsync(host, model);
    }

    private static RunicWindowTestHost<ToolkitTypedViewModel> Host(ToolkitTypedViewModel model) =>
        new(model, (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = Route, ViewLocator = new TestViewLocator() });

    private static void AssertSyncCommands(RunicWindowTestHost<ToolkitTypedViewModel> host, ToolkitTypedViewModel model)
    {
        using var applied = JsonDocument.Parse(host.Transport.Command(Route, "apply", Encode(new ToolkitCommandRequest("document-1", 3))));
        Require(applied.RootElement.GetProperty("ok").GetBoolean() && model.Total == 3,
            "A typed Toolkit DTO command did not decode and execute.");

        using var unavailableDto = JsonDocument.Parse(host.Transport.Command(Route, "apply", Encode(new ToolkitCommandRequest("other", 3))));
        Require(!unavailableDto.RootElement.GetProperty("ok").GetBoolean()
            && unavailableDto.RootElement.GetProperty("error").GetProperty("kind").GetString() == "rejected"
            && model.Total == 3,
            "A parameter-sensitive Toolkit DTO CanExecute was bypassed.");

        using var added = JsonDocument.Parse(host.Transport.Command(Route, "add", "4"));
        Require(added.RootElement.GetProperty("ok").GetBoolean() && model.Total == 7,
            "A typed Toolkit integer command did not decode and execute.");
        using var unavailableInt = JsonDocument.Parse(host.Transport.Command(Route, "add", "0"));
        Require(!unavailableInt.RootElement.GetProperty("ok").GetBoolean() && model.Total == 7,
            "A parameter-sensitive Toolkit integer CanExecute was bypassed.");

        using var renamed = JsonDocument.Parse(host.Transport.Command(Route, "rename", Encode("sync label")));
        Require(renamed.RootElement.GetProperty("ok").GetBoolean() && model.Label == "sync label",
            "A typed Toolkit string command did not decode and execute.");

        using var optional = JsonDocument.Parse(host.Transport.Command(Route, "optional", "null"));
        Require(optional.RootElement.GetProperty("ok").GetBoolean() && model.OptionalDocumentId is null,
            "A nullable Toolkit DTO command did not accept a null input.");
    }

    // An asynchronous command sent as a command request: the reply follows its completion.
    private static async Task AssertAsyncCommandsAsync(RunicWindowTestHost<ToolkitTypedViewModel> host,
        ToolkitTypedViewModel model)
    {
        await CompleteAsync(host, "asyncDto", Encode(new ToolkitCommandRequest("document-1", 2)));
        Require(model.AsyncDocumentId == "document-1" && model.Total == 9,
            "A typed asynchronous Toolkit DTO command did not complete.");

        await CompleteAsync(host, "asyncInt", "3");
        Require(model.Total == 12, "A typed asynchronous Toolkit integer command did not complete.");

        await CompleteAsync(host, "asyncString", Encode("async label"));
        Require(model.AsyncLabel == "async label", "A typed asynchronous Toolkit string command did not complete.");

        await CompleteAsync(host, "asyncOptional", "null");
        Require(model.AsyncOptionalDocumentId is null,
            "A nullable asynchronous Toolkit DTO command did not preserve null input.");

        using var unavailable = JsonDocument.Parse(await host.Transport.CommandAsync(Route, "asyncDto",
            Encode(new ToolkitCommandRequest("other", 1))));
        var error = unavailable.RootElement.GetProperty("error");
        Require(error.GetProperty("kind").GetString() == "rejected" && error.GetProperty("reason").GetString() == "unavailable",
            $"An asynchronous Toolkit command ignored its parameter-sensitive CanExecute: {unavailable.RootElement}");
    }

    private static async Task CompleteAsync(RunicWindowTestHost<ToolkitTypedViewModel> host, string member, string input)
    {
        using var reply = JsonDocument.Parse(await host.Transport.CommandAsync(Route, member, input));
        Require(reply.RootElement.GetProperty("ok").GetBoolean() && reply.RootElement.TryGetProperty("revision", out _),
            $"The asynchronous Toolkit {member} command did not complete: {reply.RootElement}");
    }

    private static async Task AssertCancellationAsync(RunicWindowTestHost<ToolkitTypedViewModel> host,
        ToolkitTypedViewModel model)
    {
        _ = host.Root.TakePublications();
        var operation = await host.Root.StartAsync(vm => vm.CancelCommand, Encode("wait"), "toolkit-cancel");
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted,
            $"The cancellable Toolkit command was not admitted: {operation.AdmissionJson}");
        Require(await PublishedAsync(host, set => set.Field == "isCancelExecuting" && set.Value.GetBoolean()),
            "A running asynchronous Toolkit command did not publish isCancelExecuting.");
        Require(operation.Cancel() == RunicOperationCancelKind.CancellationRequested,
            "The bridge did not forward cancellation to the Toolkit operation.");
        var terminal = await operation.WaitAsync();
        Require(terminal.Kind == RunicOperationStatusKind.Cancelled && model.CancelledCount == 1,
            $"A cancelled Toolkit command did not observe command-level cancellation: {terminal.Json}");
        using var idle = host.RawSnapshot();
        Require(!idle.RootElement.GetProperty("state").GetProperty("isCancelExecuting").GetBoolean(),
            "A finished asynchronous Toolkit command still reported isCancelExecuting.");
    }

    private static async Task<bool> PublishedAsync(RunicWindowTestHost<ToolkitTypedViewModel> host, Func<RunicMemberSet, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (host.Root.TakePublications().Any(publication => publication.Sets.Any(predicate))) return true;
            await Task.Delay(10);
        }
        return false;
    }

    private static async Task StartAndCompleteAsync(RunicWindowTestHost<ToolkitTypedViewModel> host,
        System.Linq.Expressions.Expression<Func<ToolkitTypedViewModel, object?>> command, string requestId, string input)
    {
        var operation = await host.Root.StartAsync(command, input, requestId);
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted,
            $"The asynchronous Toolkit {operation.Member} command was not admitted: {operation.AdmissionJson}");
        var terminal = await operation.WaitAsync();
        Require(terminal.Kind == RunicOperationStatusKind.Succeeded,
            $"The asynchronous Toolkit {operation.Member} command did not complete: {terminal.Json}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
