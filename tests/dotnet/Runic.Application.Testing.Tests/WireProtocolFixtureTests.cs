using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Views protocol 3 (specs/application/README.md): .NET answers a released View `released`, never
// `disconnected`, and a rejected reply carries an open `reason`. .NET writes the wire of each
// scenario of specs/application/fixtures/{released,error-reasons}; the views package parses the
// same files. Scenarios of another producer (a host's unbound-route envelope, a protocol-2 host)
// are client-only here.
public sealed class WireProtocolFixtureTests
{
    [Test]
    public void SnapshotRepliesStateProtocolThree() =>
        Require(BridgeProtocol.Version == 3, $"BridgeProtocol.Version is {BridgeProtocol.Version}.");

    [Test]
    public async Task PortableFixturesMatchAsync()
    {
        var produced = 0;
        foreach (var folder in new[] { "released", "error-reasons" })
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", folder);
            var files = Directory.GetFiles(directory, "*.json").OrderBy(file => file, StringComparer.Ordinal).ToArray();
            Require(files.Length > 0, $"The {folder} fixtures were not copied to {directory}.");
            foreach (var file in files)
            {
                var fixture = JsonNode.Parse(File.ReadAllText(file))!;
                if (fixture["producer"]!.GetValue<string>() != "Runic.Application.Views") continue;
                var scenario = fixture["scenario"]!.GetValue<string>();
                var expected = fixture["wire"]!;
                JsonNode actual = scenario switch
                {
                    "snapshot-reply" => SnapshotReply(),
                    "checked-write-reply" => CheckedWriteReply(),
                    "mount-ack" => MountAck(),
                    "unavailable" => await ToolkitReplyAsync("30"),
                    "invalid-input" => await ToolkitReplyAsync("\"many\""),
                    "unresolved-key" => await UnresolvedKeyReplyAsync(),
                    _ => throw new InvalidOperationException($"Unknown wire fixture scenario {scenario}."),
                };
                Require(JsonNode.DeepEquals(expected, actual),
                    $"{folder}/{Path.GetFileName(file)}: .NET wrote {actual.ToJsonString()}, the fixture expects {expected.ToJsonString()}.");
                produced++;
            }
        }
        Require(produced == 6, $"Expected six .NET-produced wire fixtures, matched {produced}.");
    }

    private static JsonObject SnapshotReply()
    {
        using var fixture = new ReleasedViewReplyTests.Fixture();
        fixture.Page.LeaveOnSnapshot = true;
        var reply = JsonNode.Parse(fixture.Transport.Call($"{fixture.Route}Snapshot"))!.AsObject();
        // The package version is not portable.
        Require(reply.Remove("version"), $"The snapshot reply has no version: {reply.ToJsonString()}");
        return reply;
    }

    private static JsonNode CheckedWriteReply()
    {
        using var fixture = new ReleasedViewReplyTests.Fixture();
        const string write = """{"requestId":"leave","expectedVersion":0,"expectedValue":0,"value":3}""";
        return JsonNode.Parse(fixture.Transport.Call($"{fixture.Route}WriteCount", new(StringValue: write)))!;
    }

    private static JsonValue MountAck()
    {
        using var fixture = new ReleasedViewReplyTests.Fixture();
        fixture.Session.ReleaseConnection("connection");
        // The closed connection's browser session cannot mount again.
        return JsonValue.Create(fixture.Transport.Call($"{fixture.Route}Mount",
            new(StringValue: "browser:page", ClientKey: "client", ConnectionKey: "connection")));
    }

    private static async Task<JsonObject> ToolkitReplyAsync(string argument)
    {
        var model = new ToolkitTypedViewModel();
        using var host = new RunicWindowTestHost<ToolkitTypedViewModel>(model,
            (transport, content, vm) => new ToolkitTypedBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var reply = await host.Root.ExecuteAsync(vm => vm.AddCommand, argument);
        Require(reply.ErrorKind == "rejected", $"The command was not rejected: {reply}");
        var node = JsonNode.Parse(reply.Json)!.AsObject();
        // The state is the fixture model's; only its presence is portable. Detail is development-only.
        node["state"] = node["state"] is null ? null : new JsonObject();
        node["error"]!.AsObject().Remove("detail");
        return node;
    }

    // #472: a RunicCommandKey command whose key names no object.
    private static async Task<JsonObject> UnresolvedKeyReplyAsync()
    {
        using var host = new RunicWindowTestHost<KeyedToolkitViewModel>(new KeyedToolkitViewModel(),
            (transport, content, vm) => new KeyedToolkitBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var reply = await host.Root.ExecuteAsync(vm => vm.LockCommand, "\"77\"");
        Require(reply.ErrorReason == "unresolved-key", $"The key was not unresolved: {reply}");
        var node = JsonNode.Parse(reply.Json)!.AsObject();
        node["state"] = node["state"] is null ? null : new JsonObject();
        return node;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
