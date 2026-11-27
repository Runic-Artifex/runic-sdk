using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Generated;
using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// That specs/application/conformance/views.ir.json, from which the test Views take their contracts (ConformanceContracts),
// describes the hand-written Bridges, so the two cannot drift apart. Each View is opened on the wire, as a page opens it:
//
// - the open reply's contract (view-contract/1) is the IR's {id}:{fingerprint};
// - its state has the members the IR gives, in the IR's order: each member, then its {wire}Errors with memberErrors,
//   then validation, can{Name} of each command without an input and is{Name}Executing of each command with
//   executionState; each value has the JSON form of its IR type;
// - its fields are the IR's checked members;
// - a set of each value member is accepted exactly when the IR marks it settable;
// - its text members (state-changes.md, "Text members") are the IR's members with text: a set with a splice reaches a
//   settable member exactly when the IR marks it text, and the Bridge compares and splices exactly those members;
// - a can of each command is a request of this protocol exactly when it carries an input as the IR's command does;
// - a write with a path below a member of the first row of a keyed collection, or of an object member, reaches the
//   member exactly when the IR lists the path as a field path (field-paths/1): every path the IR lists, and each member
//   of the row's or object's type, such as a row's key or a member that holds a blob, which are never field paths.
public sealed class ConformanceContractTests
{
    private static readonly string[] Features =
    [
        "view-contract/1", "text-splice/1", "presentation-epoch/1", "presented/1", "field-flush/1", "view-requests/1",
        "region-collections/1", "query-collections/1", "field-paths/1", "translation-text/1", "validation-pending/1", "blobs/1",
    ];

    [Test]
    public async Task TheIrDescribesEachTestViewAsync()
    {
        var ir = ConformanceContracts.Ir;
        var views = ir["views"]!.AsArray().Select(view => view!.AsObject()).ToArray();
        var opened = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var root in views.Where(view => view["route"]!.GetValue<string>() is not ("detail" or "page")))
        {
            using var transport = new ScenarioTransport();
            using var window = ConformanceViews.Producer.CreateWindow(root["route"]!.GetValue<string>(), Model(root["route"]!.GetValue<string>()),
                new(transport, new FakeTimeProvider(), null));
            var page = new Page(transport, transport.Connect("c1"));
            var hello = await page.RequestAsync(new JsonObject
            {
                ["kind"] = "hello", ["protocol"] = 4, ["session"] = "s-1",
                ["client"] = new JsonObject { ["name"] = "conformance-contract", ["version"] = "1.0.0" },
                ["features"] = new JsonArray([.. Features.Select(feature => (JsonNode)feature)]),
            });
            Require(hello["ok"]!.GetValue<bool>(), $"The hello failed: {hello.ToJsonString()}");
            var state = await CheckAsync(ir, root, root["route"]!.GetValue<string>(), page, failures);
            opened.Add(root["id"]!.GetValue<string>());
            // The content Views the root View presents: notes' detail and the tabs View's first page.
            foreach (var member in root["members"]!.AsArray().Select(member => member!.AsObject()))
            {
                var reference = member["kind"]!.GetValue<string>() switch
                {
                    "content" => state?[member["wire"]!.GetValue<string>()],
                    "region-collection" => state?[member["wire"]!.GetValue<string>()]?["regions"]?.AsArray().FirstOrDefault()?["content"],
                    _ => null,
                };
                if (reference is null) continue;
                var presented = views.Single(view => view["id"]!.GetValue<string>() == member["pages"]![0]!["view"]!.GetValue<string>());
                await CheckAsync(ir, presented, $"content{reference["id"]!.GetValue<string>()}", page, failures);
                opened.Add(presented["id"]!.GetValue<string>());
            }
        }
        failures.AddRange(views.Select(view => view["id"]!.GetValue<string>()).Where(id => !opened.Contains(id))
            .Select(id => $"{id}: not opened by this test"));
        Require(failures.Count == 0, $"specs/application/conformance/views.ir.json and the test Views differ:\n{string.Join("\n", failures)}");
    }

    // A model with a row in each collection, so that the rows' form is checked too, and an avatar.
    private static JsonObject Model(string view) => view switch
    {
        "list" or "notes" or "catalog" => new() { ["rows"] = new JsonArray(new JsonObject { ["id"] = 1, ["label"] = "a" }) },
        "invoice" => new() { ["lines"] = new JsonArray(new JsonObject { ["id"] = 1, ["label"] = "a", ["quantity"] = 2 }) },
        "files" => new()
        {
            ["avatar"] = new JsonObject { ["type"] = "text/plain", ["text"] = "a", ["name"] = "a.txt" },
            ["attachments"] = new JsonArray(new JsonObject
            {
                ["id"] = 1, ["name"] = "a.txt", ["preview"] = new JsonObject { ["type"] = "text/plain", ["text"] = "a", ["name"] = "a.txt" },
                ["cover"] = new JsonObject
                {
                    ["caption"] = "c", ["image"] = new JsonObject { ["type"] = "image/png", ["text"] = "i", ["name"] = "c.png" },
                },
                ["pages"] = new JsonArray(new JsonObject { ["type"] = "text/plain", ["text"] = "p", ["name"] = "p.txt" }),
            }),
        },
        _ => [],
    };

    // Opens the View's route and checks it against the IR View; returns the state.
    private static async Task<JsonObject?> CheckAsync(JsonObject ir, JsonObject view, string route, Page page, List<string> failures)
    {
        var id = view["id"]!.GetValue<string>();
        void Fail(string message) => failures.Add($"{id} ({route}): {message}");
        var reply = await page.RequestAsync(new JsonObject { ["kind"] = "open", ["route"] = route, ["presentation"] = $"p-{route}" });
        if (reply["state"] is not JsonObject state)
        {
            Fail($"the open failed: {reply.ToJsonString()}");
            return null;
        }
        var contract = $"{id}:{view["fingerprint"]!.GetValue<string>()}";
        if (reply["contract"]?.GetValue<string>() != contract)
            Fail($"the open reply's contract is {reply["contract"]?.ToJsonString() ?? "missing"}, not {contract}");

        var members = view["members"]!.AsArray().Select(member => member!.AsObject()).ToArray();
        var commands = view["commands"]!.AsArray().Select(command => command!.AsObject()).ToArray();
        static string Wire(JsonObject item) => item["wire"]!.GetValue<string>();
        static string Upper(string name) => char.ToUpperInvariant(name[0]) + name[1..];
        var expected = new List<string>();
        foreach (var member in members)
        {
            expected.Add(Wire(member));
            if (view["memberErrors"] is not null) expected.Add(Wire(member) + "Errors");
        }
        if (view["validation"] is not null) expected.Add("validation");
        expected.AddRange(commands.Where(command => command["input"] is null).Select(command => $"can{Upper(Wire(command))}"));
        expected.AddRange(commands.Where(command => command["executionState"] is not null).Select(command => $"is{Upper(Wire(command))}Executing"));
        var actual = state.Select(entry => entry.Key).ToArray();
        if (!actual.SequenceEqual(expected)) Fail($"its state has [{string.Join(", ", actual)}], the IR [{string.Join(", ", expected)}]");

        foreach (var member in members)
        {
            var value = state[Wire(member)];
            var form = member["kind"]!.GetValue<string>() switch
            {
                "value" or "selection" => Matches(ir, member["value"]!["type"]!.AsObject(), value),
                "content" => value is null ? member["nullable"] is not null
                    : value is JsonObject { Count: 2 } reference && reference["kind"] is not null && reference["id"] is not null,
                "region-collection" => value is JsonObject regions && regions["regions"] is JsonArray,
                "query-collection" => value is JsonObject,
                _ => false,
            };
            if (!form) Fail($"{Wire(member)} is {value?.ToJsonString() ?? "null"}, which is not of its IR type");
        }

        var fields = (reply["fields"] as JsonObject)?.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray() ?? [];
        var checkedMembers = members.Where(member => member["checked"] is not null).Select(Wire).Order(StringComparer.Ordinal).ToArray();
        if (!fields.SequenceEqual(checkedMembers))
            Fail($"its fields are [{string.Join(", ", fields)}], the IR's checked members [{string.Join(", ", checkedMembers)}]");

        foreach (var member in members.Where(member => member["kind"]!.GetValue<string>() == "value"))
        {
            var set = await page.RequestAsync(new JsonObject
            {
                ["kind"] = "set", ["route"] = route, ["member"] = Wire(member), ["edit"] = 1, ["value"] = state[Wire(member)]?.DeepClone(),
            });
            var accepted = set["ok"]!.GetValue<bool>();
            if (accepted != member["settable"] is not null)
                Fail($"a set of {Wire(member)} answers {set.ToJsonString()}, but the IR has it {(accepted ? "not " : "")}settable");
        }

        // A splice that changes nothing in the member's value at the open's revision (README, "Text splices in set"):
        // only a settable text member takes it; any other member answers invalid-request.
        var revision = reply["revision"]!.GetValue<long>();
        foreach (var member in members.Where(member => member["kind"]!.GetValue<string>() == "value"))
        {
            var length = state[Wire(member)] is JsonValue text && text.TryGetValue<string>(out var value) ? value.Length : 0;
            var splice = await page.RequestAsync(new JsonObject
            {
                ["kind"] = "set", ["route"] = route, ["member"] = Wire(member), ["edit"] = 2,
                ["splice"] = new JsonObject { ["baseRevision"] = revision, ["length"] = length, ["index"] = 0, ["delete"] = 0, ["text"] = "" },
            });
            var reached = splice["error"]?["reason"]?.GetValue<string>() != "invalid-request";
            if (reached != (member["settable"] is not null && member["text"] is not null))
                Fail($"a set of {Wire(member)} with a splice answers {splice.ToJsonString()}, but the IR has it {(reached ? "not " : "")}a settable text member");
        }
        var textMembers = page.TextMembers(route);
        var irText = members.Where(member => member["text"] is not null).Select(Wire).Order(StringComparer.Ordinal).ToArray();
        if (!textMembers.SequenceEqual(irText))
            Fail($"its text members are [{string.Join(", ", textMembers)}], the IR's [{string.Join(", ", irText)}]");

        // A path write whose expected value no member holds conflicts, or is rejected below a null object, when the path
        // is a field path, so nothing changes; any other path is invalid-request (README, "Field paths", step 2).
        var writes = 0;
        foreach (var member in members.Where(member => member["kind"]!.GetValue<string>() == "value"))
        {
            var type = member["value"]!["type"]!.AsObject();
            var keyed = member["collectionKey"] is not null;
            var objectType = keyed ? type["element"]!.AsObject() : type;
            if (objectType["kind"]!.GetValue<string>() != "named"
                || ir["types"]![objectType["ref"]!.GetValue<string>()]!["kind"]!.GetValue<string>() != "object") continue;
            if (!keyed && member["settable"] is null) continue;
            string[] prefix = [];
            if (keyed)
            {
                if (state[Wire(member)] is not JsonArray { Count: > 0 } rows) continue;
                prefix = [rows[0]![member["collectionKey"]!.GetValue<string>()]!.ToJsonString().Trim('"')];
            }
            var declared = (member["fieldPaths"] as JsonArray ?? []).Select(path => string.Join('/', path!["path"]!.AsArray().Select(segment => segment!.GetValue<string>())))
                .ToHashSet(StringComparer.Ordinal);
            var candidates = ir["types"]![objectType["ref"]!.GetValue<string>()]!["members"]!.AsArray().Select(item => item!["wire"]!.GetValue<string>())
                .Concat(declared).Distinct(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var write = await page.RequestAsync(new JsonObject
                {
                    ["kind"] = "write", ["route"] = route, ["member"] = Wire(member),
                    ["path"] = new JsonArray([.. prefix.Concat(candidate.Split('/')).Select(segment => (JsonNode)segment)]),
                    ["requestId"] = $"drift-{++writes}", ["expectedVersion"] = revision,
                    ["expectedValue"] = new JsonObject { ["drift"] = true }, ["value"] = new JsonObject { ["drift"] = true },
                });
                var reached = write["error"]?["reason"]?.GetValue<string>() != "invalid-request";
                if (reached != declared.Contains(candidate))
                    Fail($"a write of {Wire(member)} at {string.Join('/', prefix.Append(candidate))} answers {write.ToJsonString()}, but the IR {(reached ? "does not list" : "lists")} it as a field path");
            }
        }

        foreach (var command in commands)
        {
            foreach (var withInput in new[] { command["input"] is not null, command["input"] is null })
            {
                var can = new JsonObject { ["kind"] = "can", ["route"] = route, ["member"] = Wire(command) };
                if (withInput) can["input"] = command["input"] is { } input ? Sample(ir, input["type"]!.AsObject()) : "x";
                var answer = await page.RequestAsync(can);
                var invalid = answer["error"]?["reason"]?.GetValue<string>() == "invalid-request";
                if (invalid == (withInput == command["input"] is not null))
                    Fail($"a can of {Wire(command)} {(withInput ? "with" : "without")} an input answers {answer.ToJsonString()}, but the IR's command takes {(command["input"] is null ? "none" : "one")}");
            }
        }
        return state;
    }

    // Whether a state value has the JSON form of the IR type.
    private static bool Matches(JsonObject ir, JsonObject type, JsonNode? value)
    {
        if (value is null) return type["nullable"] is not null || type["kind"]!.GetValue<string>() == "json";
        switch (type["kind"]!.GetValue<string>())
        {
            case "boolean": return value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
            case "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "float32" or "float64":
                return value.GetValueKind() == JsonValueKind.Number;
            case "list": return value is JsonArray list && list.All(item => Matches(ir, type["element"]!.AsObject(), item));
            case "blob": return value is JsonObject;
            case "named":
                var definition = ir["types"]![type["ref"]!.GetValue<string>()]!.AsObject();
                switch (definition["kind"]!.GetValue<string>())
                {
                    case "enum":
                        return value.GetValueKind() == JsonValueKind.String
                            && definition["cases"]!.AsArray().Any(item => item!["wire"]!.GetValue<string>() == value.GetValue<string>());
                    case "object":
                        if (value is not JsonObject fields) return false;
                        var declared = definition["members"]!.AsArray().Select(member => member!.AsObject()).ToArray();
                        return fields.Count == declared.Length && declared.All(member =>
                            fields.ContainsKey(member["wire"]!.GetValue<string>())
                            && Matches(ir, member["type"]!.AsObject(), fields[member["wire"]!.GetValue<string>()]));
                    default: return value is JsonObject;
                }
            // string, the decimal, date and time kinds, uuid, duration, int64, uint64 and big-integer.
            default: return value.GetValueKind() == JsonValueKind.String;
        }
    }

    // A value of the IR type for a can's input; it only has to reach the command.
    private static JsonNode? Sample(JsonObject ir, JsonObject type)
    {
        switch (type["kind"]!.GetValue<string>())
        {
            case "boolean": return false;
            case "int8" or "uint8" or "int16" or "uint16" or "int32" or "uint32" or "float32" or "float64": return 0;
            case "named":
                var definition = ir["types"]![type["ref"]!.GetValue<string>()]!.AsObject();
                return definition["kind"]!.GetValue<string>() switch
                {
                    "enum" => definition["cases"]![0]!["wire"]!.GetValue<string>(),
                    "object" => new JsonObject(definition["members"]!.AsArray().Select(member =>
                        KeyValuePair.Create(member!["wire"]!.GetValue<string>(), Sample(ir, member["type"]!.AsObject())))),
                    _ => null,
                };
            default: return "x";
        }
    }

    // One page's connection: a request and its reply, skipping pushes.
    private sealed class Page(ScenarioTransport transport, ScenarioConnection connection)
    {
        private int _requests;

        // The text members of the Bridge that serves the route. Read-only text members show on the wire only in a
        // frame's splices, which no generic change of a test View produces, so this reads them from the Bridge.
        internal string[] TextMembers(string route)
        {
            var handler = BridgeDispatcher.For(transport).FindRoute(route)
                ?? throw new InvalidOperationException($"No Bridge serves {route}.");
            var owner = handler.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => typeof(ViewModelBridge).IsAssignableFrom(field.FieldType)).GetValue(handler)!;
            var text = typeof(ViewModelBridge).GetField("_textMembers", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ViewModelBridge has no _textMembers: update ConformanceContractTests.");
            return [.. ((PropertyDescriptor[])text.GetValue(owner)!).Select(member => member.WireName).Order(StringComparer.Ordinal)];
        }

        internal async Task<JsonObject> RequestAsync(JsonObject request)
        {
            var tag = $"r{++_requests}";
            transport.Send(connection, Encoding.UTF8.GetBytes(request.ToJsonString()), tag);
            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await connection.Messages.Reader.ReadAsync(timeout.Token);
                if (message.ReplyTo == tag) return JsonNode.Parse(message.Json)!.AsObject();
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
