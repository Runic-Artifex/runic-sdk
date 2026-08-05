using System.Collections.Generic;

namespace RunicToolkit.Hosting.ContractTests;

internal static partial class ScenarioCatalog
{
    public static IReadOnlyList<ContractScenario> All { get; } = Create();

    private static partial IReadOnlyList<ContractScenario> Create();
}
