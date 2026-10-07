using System.Text.Json;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Records real generated-bridge replies for declared failures (W130-029), so
/// the generated client harness decodes exactly what .NET writes.
/// </summary>
internal static class FailureGeneratedClientFixture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<FailureGeneratedClientTranscript> CreateAsync()
    {
        var model = new FailureToolkitViewModel();
        using var host = new RunicWindowTestHost<FailureToolkitViewModel>(model, "failureToolkit",
            (transport, content, vm) => new FailureToolkitBridge(transport, vm, content: content), new TestViewLocator());
        var snapshot = host.Transport.Call("failureToolkitSnapshot");
        var saveReply = host.Transport.Call("failureToolkitSave");
        const string reserveRequest = "5";
        var reserveReply = host.Transport.Call("failureToolkitReserve", new(StringValue: reserveRequest));
        model.Title = "archived";
        const string requestId = "failure-client-publish";
        var admission = host.Transport.Call("failureToolkitStartPublish", new(StringValue: requestId));
        using var document = JsonDocument.Parse(admission);
        var contract = document.RootElement.GetProperty("contract").GetString()!;
        var waitRequest = JsonSerializer.Serialize(new { contract, member = "Publish", requestId }, Json);
        var completion = await host.Transport.CallAsync("__runicOperationWait", new(StringValue: waitRequest)).ConfigureAwait(false);
        return new(snapshot, saveReply, reserveRequest, reserveReply, requestId, admission, waitRequest, completion);
    }
}

internal sealed record FailureGeneratedClientTranscript(
    string Snapshot,
    string SaveReply,
    string ReserveRequest,
    string ReserveReply,
    string PublishRequestId,
    string PublishAdmission,
    string PublishWaitRequest,
    string PublishCompletion);
