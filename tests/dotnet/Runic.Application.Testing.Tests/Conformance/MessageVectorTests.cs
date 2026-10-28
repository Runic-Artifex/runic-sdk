using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// The read-request message vectors of the Views protocol 4 conformance corpus
// (specs/application/conformance/vectors/messages, README "Messages"): the dispatcher's envelope checks. Each case runs
// in a fresh window, after a valid hello unless the case is a hello itself. A valid request is not rejected for its
// envelope; a route nothing serves answers released, which is fine here because scenarios check routes and members.
public sealed class MessageVectorTests
{
    private const string Hello = """{"kind":"hello","protocol":4,"session":"s-vector","client":{"name":"conformance-client","version":"1.0.0"}}""";
    private static readonly string[] EnvelopeReasons = ["invalid-request", "invalid-input", "no-hello"];
    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IEnumerable<Func<string>> Cases() =>
        ConformanceCorpus.MessageVectors()
            .Where(vector => vector.Case["operation"]!.GetValue<string>() == "read-request")
            .Select(vector => vector.Case["id"]!.GetValue<string>())
            .Select(id => (Func<string>)(() => id));

    [Test]
    [MethodDataSource(nameof(Cases))]
    [DisplayName("$id")]
    public async Task DispatcherReadsTheRequestAsync(string id)
    {
        var vector = ConformanceCorpus.MessageVectors().Single(candidate => candidate.Case["id"]!.GetValue<string>() == id).Case;
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions
        {
            MaximumInputBytes = vector["limits"]?["inputBytes"]?.GetValue<int>(),
            IncludeFailureDetail = false,
        });
        var input = Input(vector["input"]!.AsObject());
        if (Kind(input) != "hello")
            Require(JsonNode.Parse(transport.Request(Hello))!["ok"]!.GetValue<bool>(), $"{id}: the hello before the request failed.");

        // The bytes reach the window's handler as they are, including bytes that are not UTF-8.
        var reply = await BridgeDispatcher.For(transport)
            .HandleAsync(new BridgeRequest(input, InMemoryBridgeTransport.DefaultClientKey, InMemoryBridgeTransport.DefaultConnectionKey),
                CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var replyJson = Encoding.UTF8.GetString(reply.Span);
        var answer = JsonNode.Parse(replyJson)!.AsObject();
        var error = answer["ok"]!.GetValue<bool>() ? null : answer["error"]!.AsObject();
        var kind = error?["kind"]?.GetValue<string>();
        var reason = error?["reason"]?.GetValue<string>();

        var expect = vector["expect"]!.AsObject();
        if (expect["result"]!.GetValue<string>() == "valid")
            Require(!(kind == "rejected" && EnvelopeReasons.Contains(reason)), $"{id}: a valid request was answered {replyJson}.");
        else
        {
            var expected = expect["error"]!;
            Require(kind == expected["kind"]!.GetValue<string>() && reason == expected["reason"]!.GetValue<string>(),
                $"{id}: expected {expected.ToJsonString()}, the reply was {replyJson}.");
        }
    }

    // The input bytes: json is the value serialized, utf8 the string's UTF-8 bytes, base64 the decoded bytes.
    private static byte[] Input(JsonObject input)
    {
        var value = input["value"];
        return input["encoding"]!.GetValue<string>() switch
        {
            "json" => Encoding.UTF8.GetBytes(value?.ToJsonString(WriteOptions) ?? "null"),
            "utf8" => Encoding.UTF8.GetBytes(value!.GetValue<string>()),
            "base64" => Convert.FromBase64String(value!.GetValue<string>()),
            var encoding => throw new InvalidOperationException($"Unknown input encoding {encoding}."),
        };
    }

    // The request's kind, when the input is a JSON object with a string kind.
    private static string? Kind(byte[] input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                ? kind.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
