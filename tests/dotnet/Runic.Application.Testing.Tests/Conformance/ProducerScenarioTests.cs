using TUnit.Core;

namespace Runic.Application.Testing.Tests.Conformance;

// The producer scenarios of the Views protocol 4 conformance corpus (specs/application/conformance/scenarios), one
// case per scenario whose roles include producer, run against ConformanceViews.Producer.
public sealed class ProducerScenarioTests
{
    public static IEnumerable<Func<string>> Scenarios() =>
        ConformanceCorpus.Scenarios.Where(scenario => scenario.Roles.Contains("producer"))
            .Select(scenario => (Func<string>)(() => scenario.Id));

    [Test]
    [MethodDataSource(nameof(Scenarios))]
    [DisplayName("$id")]
    public async Task ProducerFollowsTheScenarioAsync(string id) =>
        await ProducerScenarioRunner.RunAsync(ConformanceCorpus.Scenario(id), ConformanceViews.Producer);
}
