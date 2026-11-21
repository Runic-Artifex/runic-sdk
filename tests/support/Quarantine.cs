// The quarantine of flaky tests (eng/ci/README.md, "Quarantine"; B2 of D077.25). Compiled into a test project by
// RunicFlakyTests=true (eng/build/flaky-tests.targets); internal to each project.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TUnit.Core;

/// <summary>
/// Skips a test that eng/ci/flakes.json quarantines, in the CI jobs that name that list in <c>RUNIC_QUARANTINE</c>
/// (.github/actions/flaky-tests). The job's quarantine step (eng/ci/quarantine.mjs run) then runs the test on its own,
/// with the variable empty, so it still runs and reports on the same leg without failing the job. Outside those jobs,
/// and with the variable empty, every test runs.
/// </summary>
static class QuarantinedTests
{
    sealed record Entry(string Class, string Method, int Issue, string[]? Legs);

    static readonly Lazy<Entry[]> Entries = new(Load);

    static Entry[] Load()
    {
        var path = Environment.GetEnvironmentVariable("RUNIC_QUARANTINE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];
        // JsonDocument, not JsonSerializer: the NativeAOT test hosts have no reflection-based serialization.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("quarantine", out var list)
            ? list.EnumerateArray().Select(entry => new Entry(
                entry.GetProperty("class").GetString() ?? "",
                entry.GetProperty("method").GetString() ?? "",
                entry.GetProperty("issue").GetInt32(),
                entry.TryGetProperty("legs", out var legs) ? legs.EnumerateArray().Select(leg => leg.GetString() ?? "").ToArray() : null)).ToArray()
            : [];
    }

    /// <summary>Skips the test when the list quarantines it on this leg.</summary>
    [BeforeEvery(HookType.Test)]
    public static void SkipQuarantined(TestContext context)
    {
        if (Entries.Value.Length == 0)
            return;
        var details = context.Metadata.TestDetails;
        var leg = Environment.GetEnvironmentVariable("RUNIC_RETRIED_TESTS_LEG");
        var entry = Entries.Value.FirstOrDefault(item => item.Method == details.MethodName
            && (item.Class == details.ClassType.Name || item.Class == details.ClassType.FullName)
            && (item.Legs is null || item.Legs.Contains(leg)));
        if (entry is not null)
            Skip.Test($"Quarantined under #{entry.Issue}: the job's quarantine step runs it without failing the job (eng/ci/flakes.json).");
    }
}
