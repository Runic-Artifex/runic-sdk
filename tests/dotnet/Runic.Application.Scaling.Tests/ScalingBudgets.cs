using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Runic.Application.Scaling.Tests;

// The budgets of tests/scaling/budgets.json and the results the suite writes to artifacts/scaling/results.json
// (tests/scaling/README.md). A case measures its metrics at the two sizes of its budget entries, twice each, and checks
// each metric's growth class and its limit at the larger size. RUNIC_SCALING_UPDATE=1 (bun run scaling --update)
// records the results without failing on a limit or a class, so the script can rewrite the budgets from them.
internal static class ScalingBudgets
{
    // Each case runs twice at each size and fails when a count differs between the runs (determinism, §3.5).
    private const int Repetitions = 2;
    // A metric is an improvement when it is below this share of its limit; the job summary asks to lower the limit.
    private const double ImprovementShare = 0.75;

    private static readonly object Gate = new();
    private static readonly JsonObject Results = new();
    private static readonly string Root = FindRoot();

    internal static string BudgetsPath { get; } =
        Environment.GetEnvironmentVariable("RUNIC_SCALING_BUDGETS") is { Length: > 0 } budgets ? budgets : Path.Combine(Root, "tests", "scaling", "budgets.json");

    internal static string ResultsPath { get; } =
        Environment.GetEnvironmentVariable("RUNIC_SCALING_RESULTS") is { Length: > 0 } results ? results : Path.Combine(Root, "artifacts", "scaling", "results.json");

    internal static bool Updating { get; } = Environment.GetEnvironmentVariable("RUNIC_SCALING_UPDATE") == "1";

    private static readonly Lazy<JsonObject> Budgets = new(() =>
        JsonNode.Parse(File.ReadAllText(BudgetsPath))?["cases"] as JsonObject
            ?? throw new InvalidOperationException($"{BudgetsPath} has no cases."));

    // A run starts with no results, so results.json holds only the cases of this run.
    static ScalingBudgets()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ResultsPath)!);
        File.Delete(ResultsPath);
    }

    // Runs measure at both sizes of the metrics' budget entries (the same for every metric of a case), Repetitions
    // times each, and checks every metric. measure returns one value per metric, in order.
    internal static async Task RunAsync(string[] metrics, Func<int, Task<long[]>> measure)
    {
        var entries = metrics.Select(Entry).ToArray();
        var sizes = Sizes(metrics[0], entries[0]);
        foreach (var (metric, index) in metrics.Select((metric, index) => (metric, index)))
            if (!Sizes(metric, entries[index]).SequenceEqual(sizes))
                throw new InvalidOperationException($"The metrics of one case need the same sizes: {metric} differs from {metrics[0]}.");

        var values = new long[metrics.Length, 2];
        var nondeterministic = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30 * TestTimeouts.Factor));
        for (var size = 0; size < 2; size++)
        {
            long[]? first = null;
            for (var repetition = 0; repetition < Repetitions; repetition++)
            {
                var measured = await measure(sizes[size]).WaitAsync(timeout.Token);
                if (measured.Length != metrics.Length)
                    throw new InvalidOperationException($"The case returned {measured.Length} values for {metrics.Length} metrics.");
                if (first is null)
                {
                    first = measured;
                    continue;
                }
                for (var metric = 0; metric < metrics.Length; metric++)
                    if (!Repeats(first[metric], measured[metric], Margin(entries[metric])))
                        nondeterministic.Add($"{metrics[metric]} at {sizes[size]}: {first[metric]}, then {measured[metric]}");
            }
            for (var metric = 0; metric < metrics.Length; metric++) values[metric, size] = first![metric];
        }

        var failures = new List<string>(nondeterministic.Select(text => $"not deterministic: {text}"));
        for (var metric = 0; metric < metrics.Length; metric++)
            if (Check(metrics[metric], entries[metric], sizes, values[metric, 0], values[metric, 1]) is { } failure)
                failures.Add(failure);
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }

    private static JsonObject? Entry(string metric) => Budgets.Value[metric] as JsonObject;

    private static int[] Sizes(string metric, JsonObject? entry) =>
        entry?["sizes"]?.AsArray().Select(size => size!.GetValue<int>()).ToArray() is [_, _] sizes
            ? sizes
            : throw new InvalidOperationException($"{metric} has no entry with two sizes in {BudgetsPath}. Add one with its class and sizes, then run bun run scaling --update.");

    private static double Margin(JsonObject? entry) => entry?["margin"]?.GetValue<double>() ?? 0;

    // Counts repeat exactly; a metric with a margin (allocated bytes) repeats within it.
    private static bool Repeats(long first, long second, double margin) =>
        margin == 0 ? first == second : Math.Abs(first - second) <= margin * Math.Max(first, second);

    // Records the metric and returns why it fails, or null.
    private static string? Check(string metric, JsonObject? entry, int[] sizes, long small, long large)
    {
        var growth = entry?["class"]?.GetValue<string>() ?? "";
        var limit = entry?["limit"]?.GetValue<long>();
        var margin = Margin(entry);
        var slack = entry?["slack"]?.GetValue<long>() ?? 0;
        var k = (double)sizes[1] / sizes[0];
        double? factor = growth switch { "O(1)" => 1, "O(n)" => k, "O(n²)" => k * k, _ => null };

        string? failure = null;
        var notes = new List<string>();
        if (factor is null) failure = $"{metric}: unknown class '{growth}' in {BudgetsPath}";
        else
        {
            var upper = small * factor.Value * (1 + margin) + slack;
            if (large > upper)
                failure = $"{metric} grows faster than {growth}: {small} at {sizes[0]}, {large} at {sizes[1]} (at most {Format(upper)}).";
            // The lower bound only shows that a case still scales as recorded: below it is an improvement (D064.3).
            else if (factor > 1 && large < 0.5 * factor.Value * small * (1 - margin))
                notes.Add($"grows slower than {growth}");
        }
        if (failure is null && limit is null) failure = $"{metric} has no limit in {BudgetsPath}; run bun run scaling --update.";
        else if (failure is null && large > limit) failure = $"{metric} is {large} at {sizes[1]}, over its limit {limit}.";
        else if (limit is { } recorded && large < ImprovementShare * recorded) notes.Add($"below {ImprovementShare:P0} of its limit {recorded}");

        // An update records the values; run.mjs then writes the limits from them.
        var status = Updating ? "recorded" : failure is not null ? "failed" : notes.Count > 0 ? "improved" : "ok";
        lock (Gate)
        {
            Results[metric] = new JsonObject
            {
                ["class"] = growth,
                ["sizes"] = new JsonArray(sizes[0], sizes[1]),
                ["values"] = new JsonArray(small, large),
                ["limit"] = limit,
                ["margin"] = margin == 0 ? null : margin,
                ["status"] = status,
                ["message"] = failure ?? (notes.Count > 0 ? string.Join("; ", notes) : null),
            };
            var json = new JsonObject { ["schema"] = 1, ["cases"] = Results.DeepClone() };
            File.WriteAllText(ResultsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        // An update records every value, but not a metric whose class it cannot check.
        return Updating && factor is not null ? null : failure;
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // The checkout's root: the directory above the test's output that holds tests/scaling.
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "scaling"))) return directory.FullName;
        throw new InvalidOperationException($"No tests/scaling directory above {AppContext.BaseDirectory}.");
    }
}
