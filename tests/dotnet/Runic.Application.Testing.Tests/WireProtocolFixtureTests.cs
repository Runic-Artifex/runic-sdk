using System.Text.Json.Nodes;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Views protocol 4 (specs/application/README.md): the rejected replies of generated Bridges carry an open `reason` and
// no revision, because the request did not run. The protocol-3 wire fixtures these tests compared with
// (specs/application/fixtures/{released,error-reasons}) are superseded by the conformance corpus
// (specs/application/conformance/scenarios/errors and replies, run by Conformance/ProducerScenarioTests against the
// hand-written test Views); here the generated Bridges of the Toolkit fixtures write the same replies. The released
// replies of a snapshot, a checked write and an open are in ReleasedViewReplyTests.
public sealed class WireProtocolFixtureTests
{
    [Test]
    public void TheProducerSpeaksProtocolFour() =>
        Require(BridgeProtocol.Version == 4, $"BridgeProtocol.Version is {BridgeProtocol.Version}.");

    [Test]
    [Arguments("30", "unavailable", "Add is unavailable.")]
    [Arguments("\"many\"", "invalid-input", "Add has an invalid input.")]
    public async Task ARejectedToolkitCommandRepliesItsReasonAsync(string argument, string reason, string message)
    {
        var model = new ToolkitTypedViewModel();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var reply = await host.Root.ExecuteAsync(vm => vm.AddCommand, argument);
        RequireRejected(reply.Json, reason, message);
    }

    // #472: a RunicCommandKey command whose key names no object.
    [Test]
    public async Task AnUnresolvedKeyRepliesItsReasonAsync()
    {
        using var host = new RunicWindowTestHost<KeyedToolkitViewModel>(new KeyedToolkitViewModel(),
            (transport, content, vm) => new KeyedToolkitBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var reply = await host.Root.ExecuteAsync(vm => vm.LockCommand, "\"77\"");
        RequireRejected(reply.Json, "unresolved-key", "Lock has no object for this key.");
    }

    // The reply is exactly {"ok":false,"error":{"kind":"rejected",reason,message}}; detail is development-only.
    private static void RequireRejected(string json, string reason, string message)
    {
        var actual = JsonNode.Parse(json)!.AsObject();
        actual["error"]!.AsObject().Remove("detail");
        var expected = new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject { ["kind"] = "rejected", ["message"] = message, ["reason"] = reason },
        };
        Require(JsonNode.DeepEquals(expected, actual), $".NET wrote {actual.ToJsonString()}, expected {expected.ToJsonString()}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
