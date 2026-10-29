using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Checked writes with a text splice (specs/application/README.md, "Text splices in write"; text-splice/1): a write of a
// checked text member may carry a splice of the field's value at expectedVersion instead of expectedValue and value.
public sealed class WriteSpliceTests
{
    // 100,000 code units, as the analysis measured.
    private static readonly string Large = string.Concat(Enumerable.Repeat("0123456789", 10_000));

    [Test]
    public void ASpliceWriteIsTheFullWriteOfTheRebuiltValue()
    {
        using var window = new Window(Large);
        var write = WriteSplice("w-1", 0, length: 100_000, index: 5120, delete: 0, text: "x");
        Require(Encoding.UTF8.GetByteCount(write) < 200, "The request is not small.");
        var reply = Parse(window.Transport.Request(write));
        RequireReceipt(reply, "applied", "snapshot", Large.Insert(5120, "x"), 1);
        Require(window.Model.Body == Large.Insert(5120, "x") && window.Model.BodyWrites == 1, "The setter did not receive the rebuilt value once.");
        // A retry with the same splice gets the original receipt and does not write again.
        RequireReceipt(Parse(window.Transport.Request(write)), "applied", "snapshot", Large.Insert(5120, "x"), 1);
        Require(window.Model.BodyWrites == 1, "A retry wrote again.");
        // Another splice, another expectedVersion, or the same write in full under the same requestId is a conflict.
        RequireReceipt(Parse(window.Transport.Request(WriteSplice("w-1", 0, 100_000, 5120, 0, "y"))), "conflict", "incoming", Large.Insert(5120, "x"), 1);
        RequireReceipt(Parse(window.Transport.Request(WriteSplice("w-1", 1, 100_000, 5120, 0, "x"))), "conflict", "incoming", Large.Insert(5120, "x"), 1);
        RequireReceipt(Parse(window.Transport.Request(WriteFull("w-1", 0, Large, Large.Insert(5120, "x")))), "conflict", "incoming", Large.Insert(5120, "x"), 1);
        Require(window.Model.BodyWrites == 1, "A reused requestId wrote.");
        // The setter drops a trailing newline: the receipt carries its value, and the next splice applies to it.
        RequireReceipt(Parse(window.Transport.Request(WriteSplice("w-2", 1, 100_001, 100_001, 0, "\n"))), "applied", "snapshot", Large.Insert(5120, "x"), 2);
        RequireReceipt(Parse(window.Transport.Request(WriteSplice("w-3", 2, 100_001, 0, 10, ""))), "applied", "snapshot", Large.Insert(5120, "x")[10..], 3);
    }

    [Test]
    public void ASpliceWriteAgainstAnOlderVersionIsAConflict()
    {
        using var window = new Window(Large);
        RequireReceipt(Parse(window.Transport.Request(WriteFull("w-1", 0, Large, Large + "?"))), "applied", "snapshot", Large + "?", 1);
        // The splice fits neither version's value, but the version is checked first.
        RequireReceipt(Parse(window.Transport.Request(WriteSplice("w-2", 0, 7, 0, 0, "!"))), "conflict", "incoming", Large + "?", 1);
        Require(window.Model.Body == Large + "?", "A conflicting splice wrote.");
        // The conflict is retained like any receipt.
        RequireReceipt(Parse(window.Transport.Request(WriteSplice("w-2", 0, 7, 0, 0, "!"))), "conflict", "incoming", Large + "?", 1);
    }

    [Test]
    public void ASpliceThatDoesNotFitRetainsNothing()
    {
        using var window = new Window(Large + "\U0001F600");
        var value = Large + "\U0001F600";
        foreach (var (length, index, delete) in new[] { (100_001L, 0L, 0L), (100_002L, 100_000L, 3L), (100_002L, 100_001L, 0L) })
            RequireError(window.Transport.Request(WriteSplice("w-1", 0, length, index, delete, "x")), "rejected", "splice-base");
        Require(window.Model.Body == value && window.Model.BodyWrites == 0, "A splice that does not fit wrote.");
        // Nothing was retained: the same requestId writes in full.
        RequireReceipt(Parse(window.Transport.Request(WriteFull("w-1", 0, value, value + "!"))), "applied", "snapshot", value + "!", 1);
        Require(window.Model.BodyWrites == 1, "The full write was not applied.");
    }

    [Test]
    public void ASpliceOfANullFieldIsSpliceBase()
    {
        using var window = new Window(Large);
        RequireReceipt(Parse(window.Transport.Request(WriteFull("w-1", 0, Large, null))), "applied", "snapshot", null, 1);
        RequireError(window.Transport.Request(WriteSplice("w-2", 1, 0, 0, 0, "x")), "rejected", "splice-base");
        Require(window.Model.Body is null, "A splice of null wrote.");
    }

    [Test]
    public void SpliceWritesNeedTheFeatureACheckedTextMemberAndAValidSplice()
    {
        using var window = new Window(Large, features: false);
        RequireError(window.Transport.Request(WriteSplice("w-1", 0, 100_000, 0, 0, "x")), "rejected", "invalid-request");

        window.Transport.Request(Hello("s-2", features: true), "c2");
        RequireError(window.Transport.Request(WriteSplice("w-1", 0, 1, 0, 0, "x", member: "count"), "c2"), "rejected", "invalid-request");
        const string Head = "{\"kind\":\"write\",\"route\":\"splice-write\",\"member\":\"body\",\"requestId\":\"w-1\",\"expectedVersion\":0,";
        foreach (var invalid in new[]
        {
            Head + "\"value\":\"x\",\"splice\":{\"length\":100000,\"index\":0,\"delete\":0,\"text\":\"x\"}}",
            Head + "\"expectedValue\":\"\",\"splice\":{\"length\":100000,\"index\":0,\"delete\":0,\"text\":\"x\"}}",
            Head + "\"splice\":{\"baseRevision\":0,\"length\":100000,\"index\":0,\"delete\":0,\"text\":\"x\"}}",
            Head + "\"splice\":{\"baseEdit\":1,\"length\":100000,\"index\":0,\"delete\":0,\"text\":\"x\"}}",
            Head + "\"splice\":{\"length\":100000,\"index\":-1,\"delete\":0,\"text\":\"x\"}}",
            Head + "\"splice\":{\"length\":100000,\"index\":0,\"delete\":0}}",
            Head + "\"splice\":[]}",
            Head + "\"expectedValue\":\"\"}",
        })
            RequireError(window.Transport.Request(invalid, "c2"), "rejected", "invalid-request");
        Require(window.Model.Body == Large && window.Model.BodyWrites == 0, "An invalid splice write changed the model.");
        // A full write is unchanged on both connections.
        RequireReceipt(Parse(window.Transport.Request(WriteFull("w-1", 0, Large, "x"))), "applied", "snapshot", "x", 1);
    }

    private static string Hello(string session, bool features) =>
        """{"kind":"hello","protocol":4,"session":""" + "\"" + session + "\"" + ""","client":{"name":"test","version":"1.0.0"}"""
        + (features ? ""","features":["text-splice/1"]""" : "") + "}";

    private static string WriteSplice(string requestId, long expectedVersion, long length, long index, long delete, string text,
        string member = "body") =>
        $$$"""{"kind":"write","route":"splice-write","member":"{{{member}}}","requestId":"{{{requestId}}}","expectedVersion":{{{expectedVersion}}},"splice":{"length":{{{length}}},"index":{{{index}}},"delete":{{{delete}}},"text":{{{JsonSerializer.Serialize(text)}}}}}""";

    private static string WriteFull(string requestId, long expectedVersion, string expectedValue, string? value) =>
        $$$"""{"kind":"write","route":"splice-write","member":"body","requestId":"{{{requestId}}}","expectedVersion":{{{expectedVersion}}},"expectedValue":{{{JsonSerializer.Serialize(expectedValue)}}},"value":{{{JsonSerializer.Serialize(value)}}}}""";

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void RequireReceipt(JsonElement reply, string kind, string snapshot, string? value, long version)
    {
        var text = reply.ToString();
        Require(reply.GetProperty("ok").GetBoolean(), $"The write failed: {Truncate(text)}");
        var receipt = reply.GetProperty("receipt");
        Require(receipt.GetProperty("kind").GetString() == kind, $"Expected a {kind} receipt: {Truncate(text)}");
        var field = receipt.GetProperty(snapshot);
        Require(field.GetProperty("value").GetString() == value && field.GetProperty("version").GetInt64() == version,
            $"Expected {snapshot} at version {version}: {Truncate(text)}");
    }

    private static void RequireError(string json, string kind, string reason)
    {
        var reply = Parse(json);
        Require(!reply.GetProperty("ok").GetBoolean() && reply.GetProperty("error").GetProperty("kind").GetString() == kind
            && reply.GetProperty("error").GetProperty("reason").GetString() == reason, $"Expected {kind}/{reason}, got {Truncate(json)}.");
    }

    private static string Truncate(string json) => json.Length > 300 ? json[..300] + "…" : json;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A window whose root View has a checked text member body (its setter drops a trailing newline) and a checked
    // int32 member count.
    private sealed class Window : IDisposable
    {
        private readonly WindowContentSession _session;
        private readonly SpliceWriteBridge _bridge;

        public Window(string body, bool features = true)
        {
            Model = new() { Body = body };
            Model.BodyWrites = 0;
            Transport = new InMemoryBridgeTransport();
            _session = new WindowContentSession(Transport, new WindowContentSessionOptions { RootModel = Model });
            _bridge = new SpliceWriteBridge(Transport, Model, _session);
            Transport.Request(Hello("s-1", features));
            Transport.Request("""{"kind":"open","route":"splice-write","presentation":"p-1"}""");
        }

        public SpliceWriteModel Model { get; }

        public InMemoryBridgeTransport Transport { get; }

        public void Dispose()
        {
            _bridge.Dispose();
            _session.Dispose();
            Transport.Dispose();
        }
    }

    private sealed class SpliceWriteModel : INotifyPropertyChanged
    {
        private string? _body = "";
        private int _count;

        public int BodyWrites { get; set; }

        public string? Body
        {
            get => _body;
            set
            {
                BodyWrites++;
                _body = value?.TrimEnd('\n');
                PropertyChanged?.Invoke(this, new(nameof(Body)));
            }
        }

        public int Count
        {
            get => _count;
            set { _count = value; PropertyChanged?.Invoke(this, new(nameof(Count))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class SpliceWriteBridge(IBridgeTransport transport, SpliceWriteModel model, WindowContentSession content)
        : ViewModelBridge<SpliceWriteModel>(transport, model, "splice-write",
            [
                new("body", (writer, vm) => { if (vm.Body is null) writer.WriteNullValue(); else writer.WriteStringValue(vm.Body); }),
                new("count", (writer, vm) => writer.WriteNumberValue(vm.Count)),
            ],
            [
                new("Body", "body", vm => vm.Body, null) { TextMember = true },
                new("Count", "count", vm => vm.Count, null),
            ],
            [
                new("Body", "body", CheckedFieldValueKind.NullableString, vm => vm.Body, (vm, value) => vm.Body = (string?)value),
                new("Count", "count", CheckedFieldValueKind.Int32, vm => vm.Count, (vm, value) => vm.Count = (int)value!),
            ],
            [], contractFingerprint: "splice-write", content: content);
}
