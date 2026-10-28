using System.Globalization;
using System.Text.Json.Nodes;

namespace Runic.Application.Testing.Tests.Conformance;

// The Views protocol 4 conformance corpus in specs/application/conformance (its README.md is normative): the
// scenarios of scenarios/<set>/*.json and the message vectors of vectors/messages/*.json, read from the repository
// rather than copied to the output, so the runner always checks the corpus of the checkout.
internal static class ConformanceCorpus
{
    private static readonly Lazy<string> RootDirectory = new(FindRoot);
    private static readonly Lazy<IReadOnlyList<ScenarioEntry>> ScenarioEntries = new(ReadScenarioEntries);

    internal static string Root => RootDirectory.Value;

    // Every scenario, by id, with what the test cases need before they read the whole file.
    internal static IReadOnlyList<ScenarioEntry> Scenarios => ScenarioEntries.Value;

    // The scenario with the id, its generators expanded.
    internal static ConformanceScenario Scenario(string id)
    {
        var entry = Scenarios.SingleOrDefault(candidate => candidate.Id == id)
            ?? throw new InvalidOperationException($"No scenario has the id {id}.");
        return ConformanceScenario.Read(entry.Path);
    }

    // Every case of vectors/messages/*.json, in file order, as (file id, case).
    internal static IEnumerable<(string File, JsonObject Case)> MessageVectors()
    {
        var files = Directory.GetFiles(Path.Combine(Root, "vectors", "messages"), "*.json")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(files.Length > 0, $"No message vectors under {Root}.");
        foreach (var file in files)
        {
            var vector = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            foreach (var testCase in vector["cases"]!.AsArray()) yield return (vector["id"]!.GetValue<string>(), testCase!.AsObject());
        }
    }

    // Expands the generators of README "Comparing messages": an array element that is a single-member object
    // {"$rows": [start, count, width?]} is count rows of the list View, {"$adds": [start, count, width?]} one add
    // change per such row. Everything else is copied.
    internal static JsonNode? Expand(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                var result = new JsonArray();
                foreach (var item in array)
                {
                    if (item is JsonObject { Count: 1 } rows && rows["$rows"] is JsonArray rowRange)
                        foreach (var (id, width) in Range(rowRange)) result.Add(Row(id, width));
                    else if (item is JsonObject { Count: 1 } adds && adds["$adds"] is JsonArray addRange)
                        foreach (var (id, width) in Range(addRange))
                            result.Add(new JsonObject
                            {
                                ["field"] = "rows", ["kind"] = "add", ["index"] = id, ["oldIndex"] = -1,
                                ["keys"] = new JsonArray(id.ToString(CultureInfo.InvariantCulture)),
                                ["items"] = new JsonArray(Row(id, width)),
                            });
                    else result.Add(Expand(item));
                }
                return result;
            case JsonObject value:
                var copy = new JsonObject();
                foreach (var (key, child) in value) copy[key] = Expand(child);
                return copy;
            default:
                return node?.DeepClone();
        }
    }

    // The row of the list View with the id: {id, label: "row {id}"}, the label padded with '.' to width.
    internal static JsonObject Row(int id, int width = 0) =>
        new() { ["id"] = id, ["label"] = $"row {id}".PadRight(width, '.') };

    private static IEnumerable<(int Id, int Width)> Range(JsonArray range)
    {
        var start = range[0]!.GetValue<int>();
        var width = range.Count > 2 ? range[2]!.GetValue<int>() : 0;
        return Enumerable.Range(start, range[1]!.GetValue<int>()).Select(id => (id, width));
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "specs", "application", "conformance");
            if (File.Exists(Path.Combine(candidate, "scenario.schema.json"))) return candidate;
        }
        throw new InvalidOperationException($"specs/application/conformance was not found above {AppContext.BaseDirectory}.");
    }

    private static List<ScenarioEntry> ReadScenarioEntries()
    {
        var entries = new List<ScenarioEntry>();
        foreach (var path in Directory.GetFiles(Path.Combine(Root, "scenarios"), "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            var scenario = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            entries.Add(new(scenario["id"]!.GetValue<string>(), scenario["set"]!.GetValue<string>(),
                [.. scenario["roles"]!.AsArray().Select(role => role!.GetValue<string>())], path));
        }
        Require(entries.Count > 0, $"No scenarios under {Root}.");
        return entries;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed record ScenarioEntry(string Id, string Set, IReadOnlyList<string> Roles, string Path);

// One identifier of a scenario's ids (README "Identifiers"): a connection, request, call, wire id with its value or
// provided id.
internal sealed record ScenarioId(string Name, string Kind, string? Value);

// A scenario (scenario.schema.json), its arrange and steps with their generators expanded.
internal sealed record ConformanceScenario(
    string Id,
    string Set,
    string View,
    IReadOnlyDictionary<string, ScenarioId> Ids,
    JsonObject Model,
    int? InputBytes,
    IReadOnlyList<JsonObject> Steps)
{
    internal static ConformanceScenario Read(string path)
    {
        var scenario = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var ids = new Dictionary<string, ScenarioId>(StringComparer.Ordinal);
        foreach (var (name, id) in scenario["ids"]!.AsObject())
            ids.Add(name, new(name, id!["kind"]!.GetValue<string>(), id["value"]?.GetValue<string>()));
        return new(
            scenario["id"]!.GetValue<string>(),
            scenario["set"]!.GetValue<string>(),
            scenario["view"]!.GetValue<string>(),
            ids,
            ConformanceCorpus.Expand(scenario["arrange"]!["model"])!.AsObject(),
            scenario["options"]?["inputBytes"]?.GetValue<int>(),
            [.. ConformanceCorpus.Expand(scenario["steps"])!.AsArray().Select(step => step!.AsObject())]);
    }
}
