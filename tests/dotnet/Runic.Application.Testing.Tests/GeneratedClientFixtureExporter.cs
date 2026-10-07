using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Produces a small, real route transcript for the generated-client harness.
/// It deliberately uses generated bridges and an in-memory transport instead
/// of duplicating their wire representation in JavaScript.
/// </summary>
internal static class GeneratedClientFixtureExporter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task WriteAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var dataShape = CreateDataShapeTranscript();
        var typedReactive = await CreateTypedReactiveTranscriptAsync();
        var toolkitTyped = await ToolkitGeneratedClientFixture.CreateAsync();
        var validationModel = new ValidationViewModel();
        validationModel.SetErrors(null, new BridgeValidationMessage("Entity failure", "entity", "error"));
        validationModel.Profile.SetErrors(nameof(ValidationProfile.PostalCode), new BridgeValidationMessage("Invalid postal code", "postal"));
        validationModel.Items[0].SetErrors(nameof(ValidationItem.Name), "Missing label");
        using var validationHost = new RunicWindowTestHost<ValidationViewModel>(validationModel, "validation",
            (transport, content, vm) => new ValidationBridge(transport, vm, content: content), new TestViewLocator());
        var fixture = new GeneratedClientFixture(dataShape, typedReactive, toolkitTyped, validationHost.Transport.Call("validationSnapshot"),
            CreateDtoListTranscript(), await CreateDtoInteractionTranscriptAsync());

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture, Json)).ConfigureAwait(false);
    }

    private static DataShapeTranscript CreateDataShapeTranscript()
    {
        var model = new DataShapeViewModel();
        using var host = new RunicWindowTestHost<DataShapeViewModel>(model, "dataShape",
            (transport, content, vm) => new DataShapeBridge(transport, vm, content: content), new TestViewLocator());

        var snapshot = host.Transport.Call("dataShapeSnapshot");
        const string duration = "-1.02:03:04.5000007";
        var durationRequest = JsonSerializer.Serialize(duration, Json);
        var durationReply = host.Transport.Call("dataShapeSetDuration", new(StringValue: durationRequest));

        const string amount = "99.2500";
        var amountRequest = JsonSerializer.Serialize(amount, Json);
        var amountReply = host.Transport.Call("dataShapeSetAmount", new(StringValue: amountRequest));

        var whole = new Dictionary<string, object?>
        {
            ["source"] = "base",
            ["__proto__"] = "whole-from-client",
            ["retry-after"] = 11,
        };
        var wholeRequest = JsonSerializer.Serialize(whole, Json);
        var wholeReply = host.Transport.Call("dataShapeSetWhole", new(StringValue: wholeRequest));

        using var state = JsonDocument.Parse(wholeReply);
        var baselineVersion = state.RootElement.GetProperty("state").GetProperty("__runicFields")
            .GetProperty("exact-id").GetProperty("version").GetInt64();
        var exactIdWrite = new
        {
            requestId = "generated-client-exact-id",
            expectedVersion = baselineVersion,
            expectedValue = "9007199254740993",
            value = "9007199254740992",
        };
        var exactIdWriteRequest = JsonSerializer.Serialize(exactIdWrite, Json);
        var exactIdWriteReply = host.Transport.Call("dataShapeWriteExactId", new(StringValue: exactIdWriteRequest));

        var conflictRequest = JsonSerializer.Serialize(new { requestId = "generated-client-conflict",
            expectedVersion = baselineVersion, expectedValue = "9007199254740993", value = "2" }, Json);
        var conflictReply = host.Transport.Call("dataShapeWriteExactId", new(StringValue: conflictRequest));
        using var applied = JsonDocument.Parse(exactIdWriteReply);
        var nextVersion = applied.RootElement.GetProperty("receipt").GetProperty("snapshot").GetProperty("version").GetInt64();
        var failedRequest = JsonSerializer.Serialize(new { requestId = "generated-client-post-apply",
            expectedVersion = nextVersion, expectedValue = "9007199254740992", value = "-1" }, Json);
        var failedReply = host.Transport.Call("dataShapeWriteExactId", new(StringValue: failedRequest));
        using var afterFailure = JsonDocument.Parse(failedReply);
        var payloadState = afterFailure.RootElement.GetProperty("state");
        var unionRequest = JsonSerializer.Serialize(new { requestId = "generated-client-union",
            expectedVersion = payloadState.GetProperty("__runicFields").GetProperty("payload").GetProperty("version").GetInt64(),
            expectedValue = payloadState.GetProperty("payload"),
            value = new Dictionary<string, object> { ["$case"] = "count", ["count"] = 42 } }, Json);
        var unionReply = host.Transport.Call("dataShapeWritePayload", new(StringValue: unionRequest));
        return new(snapshot, duration, durationRequest, durationReply, amount, amountRequest, amountReply,
            wholeRequest, wholeReply, exactIdWriteRequest, exactIdWriteReply, conflictRequest, conflictReply, failedRequest, failedReply,
            unionRequest, unionReply);
    }

    private static DtoListTranscript CreateDtoListTranscript()
    {
        var model = new DtoListViewModel();
        using var host = new RunicWindowTestHost<DtoListViewModel>(model, "dtoList",
            (transport, content, vm) => new DtoListBridge(transport, vm, content: content), new TestViewLocator());
        var snapshot = host.Transport.Call("dtoListSnapshot");
        var setRequest = JsonSerializer.Serialize(new[] { new DtoListEntry("set", 2) }, Json);
        var setReply = Ok(host.Transport.Call("dtoListSetEntries", new(StringValue: setRequest)));
        using var state = JsonDocument.Parse(setReply);
        var writeRequest = JsonSerializer.Serialize(new
        {
            requestId = "generated-client-entries",
            expectedVersion = state.RootElement.GetProperty("state").GetProperty("__runicFields").GetProperty("entries").GetProperty("version").GetInt64(),
            expectedValue = new[] { new DtoListEntry("set", 2) },
            value = new[] { new DtoListEntry("written", 3), new DtoListEntry("second", 4) },
        }, Json);
        var writeReply = Ok(host.Transport.Call("dtoListWriteEntries", new(StringValue: writeRequest)));
        var replaceRequest = JsonSerializer.Serialize(new[] { new DtoListEntry("replaced", 5) }, Json);
        var replaceReply = Ok(host.Transport.Call("dtoListReplace", new(StringValue: replaceRequest)));
        if (model.Entries is not [{ Name: "replaced", Count: 5 }])
            throw new InvalidOperationException("The DTO list command did not reach the ViewModel.");
        return new(snapshot, setRequest, setReply, writeRequest, writeReply, replaceRequest, replaceReply);

        static string Ok(string reply)
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.GetProperty("ok").GetBoolean() ? reply
                : throw new InvalidOperationException($"A DTO list route failed: {reply}");
        }
    }

    // A .NET interaction request whose DTO output the generated client answers.
    // The recorded output is the reply .NET accepted for that request.
    private static async Task<DtoInteractionTranscript> CreateDtoInteractionTranscriptAsync()
    {
        using var model = new DtoInteractionViewModel();
        using var host = new RunicWindowTestHost<DtoInteractionViewModel>(model, "dtoInteraction",
            (transport, content, vm) => new DtoInteractionBridge(transport, vm, content: content), new TestViewLocator());
        const string route = "dtoInteraction", presentation = "dto:interaction", client = "dto-client", connection = "dto-connection";
        var contract = $"{BridgeContractShape.Compute(typeof(DtoInteractionViewModel))}:interaction:ChooseEntry";
        var snapshot = host.Transport.Call(route + "Snapshot");
        if (host.Transport.Call(route + "Mount", new(StringValue: presentation, ClientKey: client, ConnectionKey: connection)) != "ok")
            throw new InvalidOperationException("The DTO interaction root did not mount.");
        var wait = host.Transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: JsonSerializer.Serialize(new
        {
            route,
            presentationId = presentation,
            handlers = new[] { new { name = "chooseEntry", contract } },
        }), ClientKey: client, ConnectionKey: connection)).AsTask();
        await Task.Yield();
        var command = host.Transport.CallAsync(route + "Pick", new(ClientKey: client, ConnectionKey: connection)).AsTask();
        var request = await wait.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        using var requestDocument = JsonDocument.Parse(request);
        var output = new DtoListEntry("picked", 6);
        var reply = JsonSerializer.Serialize(new
        {
            kind = "answered",
            requestId = requestDocument.RootElement.GetProperty("requestId").GetString(),
            route,
            presentationId = presentation,
            ownerEpoch = requestDocument.RootElement.GetProperty("ownerEpoch").GetInt64(),
            name = "chooseEntry",
            contract,
            output,
        }, Json);
        using var replyResult = JsonDocument.Parse(host.Transport.Call(BridgeInteractionRouter.ReplyRoute,
            new(StringValue: reply, ClientKey: client, ConnectionKey: connection)));
        using var completed = JsonDocument.Parse(await command.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
        if (replyResult.RootElement.GetProperty("kind").GetString() != "ok" || !completed.RootElement.GetProperty("ok").GetBoolean()
            || model.Picked is not { Name: "picked", Count: 6 })
            throw new InvalidOperationException("The DTO interaction output did not reach the ViewModel.");
        return new(snapshot, request, JsonSerializer.Serialize(output, Json));
    }

    private static async Task<TypedReactiveTranscript> CreateTypedReactiveTranscriptAsync()
    {
        await using var context = new RunicModelContext();
        var model = new TypedReactiveViewModel(context);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<TypedReactiveViewModel>(model, "typedReactive",
            (transport, content, vm) => new TypedReactiveBridge(transport, vm, content: content), new TestViewLocator());

        var snapshot = host.Transport.Call("typedReactiveSnapshot");
        const string requestId = "generated-client-last-result";
        var request = JsonSerializer.Serialize(new
        {
            requestId,
            input = new TypedSaveRequest("document-1", "from-client", 7),
        }, Json);
        var admission = host.Transport.Call("typedReactiveStartSave", new(StringValue: request));
        using var admissionDocument = JsonDocument.Parse(admission);
        var contract = admissionDocument.RootElement.GetProperty("contract").GetString()
            ?? throw new InvalidOperationException("The generated typed command did not return a contract.");
        var wrongMemberStatusRequest = JsonSerializer.Serialize(new { contract, member = "LastResult", requestId }, Json);
        var wrongMemberStatus = host.Transport.Call("__runicOperationStatus", new(StringValue: wrongMemberStatusRequest));

        model.ReleaseSave();
        var waitRequest = JsonSerializer.Serialize(new { contract, member = "Save", requestId }, Json);
        var completion = await host.Transport.CallAsync("__runicOperationWait", new(StringValue: waitRequest)).ConfigureAwait(false);

        return new(snapshot, requestId, request, admission, waitRequest, completion, wrongMemberStatusRequest, wrongMemberStatus);
    }

    private sealed record GeneratedClientFixture(DataShapeTranscript DataShape, TypedReactiveTranscript TypedReactive,
        ToolkitGeneratedClientTranscript ToolkitTyped, string ValidationSnapshot, DtoListTranscript DtoList,
        DtoInteractionTranscript DtoInteraction);

    private sealed record DtoInteractionTranscript(string Snapshot, string Request, string Output);

    private sealed record DtoListTranscript(string Snapshot, string SetRequest, string SetReply, string WriteRequest, string WriteReply,
        string ReplaceRequest, string ReplaceReply);

    private sealed record DataShapeTranscript(
        string Snapshot,
        string Duration,
        string DurationRequest,
        string DurationReply,
        string Amount,
        string AmountRequest,
        string AmountReply,
        string WholeRequest,
        string WholeReply,
        string ExactIdWriteRequest,
        string ExactIdWriteReply,
        string ConflictRequest, string ConflictReply, string FailedRequest, string FailedReply,
        string UnionRequest, string UnionReply);

    private sealed record TypedReactiveTranscript(
        string Snapshot,
        string RequestId,
        string StartRequest,
        string Admission,
        string WaitRequest,
        string Completion,
        string WrongMemberStatusRequest,
        string WrongMemberStatus);
}
