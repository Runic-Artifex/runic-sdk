using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// The producer scenarios of the Views protocol 4 conformance corpus (specs/application/conformance/scenarios), one
// case per scenario whose roles include producer, run against ConformanceViews.Producer.
public sealed class ProducerScenarioTests
{
    // The scenarios the producer passes so far. Every other one is skipped at run time and names the pull request of
    // the protocol-4 plan that implements what it needs; that pull request adds it here.
    private static readonly HashSet<string> Passing = new(StringComparer.Ordinal)
    {
        "handshake.producer-refuses-another-protocol.v1",
        "handshake.second-hello.v1",
        "handshake.unknown-request-kind.v1",
        // An open of a route nothing serves is released, which the dispatcher answers.
        "presentations.unknown-route.v1",
    };

    public static IEnumerable<Func<string>> Scenarios() =>
        ConformanceCorpus.Scenarios.Where(scenario => scenario.Roles.Contains("producer"))
            .Select(scenario => (Func<string>)(() => scenario.Id));

    [Test]
    [MethodDataSource(nameof(Scenarios))]
    [DisplayName("$id")]
    public async Task ProducerFollowsTheScenarioAsync(string id)
    {
        var scenario = ConformanceCorpus.Scenario(id);
        if (!Passing.Contains(id)) Skip.Test(LaterPullRequest(scenario.Set));
        await ProducerScenarioRunner.RunAsync(scenario, ConformanceViews.Producer);
    }

    // What a scenario of the set waits for: P4 serves the test Views' state, edits, presentations, errors and replies;
    // P5 their operations and interactions.
    private static string LaterPullRequest(string set) => set switch
    {
        "handshake" => "P4: needs open or snapshot of the test Views",
        "state-changes" => "P4: needs the state and frames of the test Views",
        "replies" => "P4: needs replies with revisions from the test Views",
        "edits" => "P4: needs edits of the test Views",
        "errors" => "P4: needs error replies of the test Views",
        "presentations" => "P4: needs presentations of the test Views",
        "operations" => "P5: needs operations of the test Views",
        "interactions" => "P5: needs interactions of the test Views",
        _ => throw new InvalidOperationException($"Unknown scenario set {set}."),
    };
}
