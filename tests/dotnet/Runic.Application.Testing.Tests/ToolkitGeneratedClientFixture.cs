using System.Text.Json;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Captures real generated Toolkit bridge routes for the browser harness. The
/// transcript is deliberately finite: it contains direct command replies and
/// terminal operation replies, never an emulated long-poll loop.
/// </summary>
internal static class ToolkitGeneratedClientFixture
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<ToolkitGeneratedClientTranscript> CreateAsync()
    {
        var model = new ToolkitTypedViewModel();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model, "toolkitTyped",
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content), new TestViewLocator());

        var snapshot = host.Transport.Call("toolkitTypedSnapshot");
        var stringRequest = JsonSerializer.Serialize("client string", Json);
        var stringReply = host.Transport.Call("toolkitTypedRename", new(StringValue: (stringRequest)));
        var dtoRequest = JsonSerializer.Serialize(new ToolkitCommandRequest("document-1", 4), Json);
        var dtoReply = host.Transport.Call("toolkitTypedApply", new(StringValue: (dtoRequest)));
        const string nullableRequest = "null";
        var nullableReply = host.Transport.Call("toolkitTypedOptional", new(StringValue: (nullableRequest)));

        var asyncString = await StartAndWaitAsync(host, "AsyncString", "toolkit-client-string", "client async");
        var asyncDto = await StartAndWaitAsync(host, "AsyncDto", "toolkit-client-dto",
            new ToolkitCommandRequest("document-1", 5));
        var asyncNullable = await StartAndWaitAsync(host, "AsyncOptional", "toolkit-client-nullable", input: null);

        return new(snapshot, stringRequest, stringReply, dtoRequest, dtoReply, nullableRequest, nullableReply,
            asyncString, asyncDto, asyncNullable);
    }

    private static async Task<ToolkitGeneratedOperationTranscript> StartAndWaitAsync(
        RunicWindowTestHost<ToolkitTypedViewModel> host, string member, string requestId, object? input)
    {
        var startRequest = JsonSerializer.Serialize(new { requestId, input }, Json);
        var admission = host.Transport.Call($"toolkitTypedStart{member}", new(StringValue: (startRequest)));
        using var document = JsonDocument.Parse(admission);
        var contract = document.RootElement.GetProperty("contract").GetString()
            ?? throw new InvalidOperationException($"Toolkit {member} did not return an operation contract.");
        var waitRequest = JsonSerializer.Serialize(new { contract, member, requestId }, Json);
        var completion = await host.Transport.CallAsync("__runicOperationWait", new(StringValue: (waitRequest))).ConfigureAwait(false);
        return new(requestId, startRequest, admission, waitRequest, completion);
    }
}

internal sealed record ToolkitGeneratedClientTranscript(
    string Snapshot,
    string StringRequest,
    string StringReply,
    string DtoRequest,
    string DtoReply,
    string NullableRequest,
    string NullableReply,
    ToolkitGeneratedOperationTranscript AsyncString,
    ToolkitGeneratedOperationTranscript AsyncDto,
    ToolkitGeneratedOperationTranscript AsyncNullable);

internal sealed record ToolkitGeneratedOperationTranscript(
    string RequestId,
    string StartRequest,
    string Admission,
    string WaitRequest,
    string Completion);
