using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// The producer scenarios of the Views protocol 4 conformance corpus (specs/application/conformance/scenarios), one
// case per scenario whose roles include producer, run against ConformanceViews.Producer.
public sealed class ProducerScenarioTests
{
    // The scenarios the producer passes so far. Every other one is skipped at run time and names the pull request of
    // the protocol-4 plan that implements what it needs; that pull request adds it here.
    // The sets of P5 (operations and interactions) are skipped at run time until P5 serves them; every other
    // producer scenario runs.
    private static readonly HashSet<string> LaterSets = new(StringComparer.Ordinal) { "operations", "interactions" };

    public static IEnumerable<Func<string>> Scenarios() =>
        ConformanceCorpus.Scenarios.Where(scenario => scenario.Roles.Contains("producer"))
            .Select(scenario => (Func<string>)(() => scenario.Id));

    [Test]
    [MethodDataSource(nameof(Scenarios))]
    [DisplayName("$id")]
    public async Task ProducerFollowsTheScenarioAsync(string id)
    {
        var scenario = ConformanceCorpus.Scenario(id);
        if (LaterSets.Contains(scenario.Set)) Skip.Test(LaterPullRequest(scenario.Set));
        await ProducerScenarioRunner.RunAsync(scenario, ConformanceViews.Producer);
    }

    // What a scenario of the set waits for: P5 serves the test Views' operations and interactions.
    private static string LaterPullRequest(string set) => set switch
    {
        "operations" => "P5: needs operations of the test Views",
        "interactions" => "P5: needs interactions of the test Views",
        _ => throw new InvalidOperationException($"Unknown scenario set {set}."),
    };
}
