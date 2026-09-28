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
        var fixture = new GeneratedClientFixture(dataShape, typedReactive);

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

        return new(snapshot, duration, durationRequest, durationReply, amount, amountRequest, amountReply,
            wholeRequest, wholeReply, exactIdWriteRequest, exactIdWriteReply);
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

    private sealed record GeneratedClientFixture(DataShapeTranscript DataShape, TypedReactiveTranscript TypedReactive);

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
        string ExactIdWriteReply);

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
