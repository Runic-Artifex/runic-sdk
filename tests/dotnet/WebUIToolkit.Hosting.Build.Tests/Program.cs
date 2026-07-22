using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.Build.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        TestScenario[] scenarios = args.Length == 0
            ? ScenarioCatalog.All.ToArray()
            : ScenarioCatalog.All
                .Where(scenario => scenario.Id.Contains(args[0], StringComparison.Ordinal))
                .ToArray();

        if (scenarios.Length == 0)
        {
            Console.Error.WriteLine("No build scenario matched the supplied filter.");
            return 2;
        }

        return await TestRunner.RunAsync(scenarios).ConfigureAwait(false);
    }
}
