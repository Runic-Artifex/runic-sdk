using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Policy.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        TestHarness tests = new();
        ParserTests.Register(tests);
        EvaluatorTests.Register(tests);
        FixtureTests.Register(tests);
        return await tests.RunAsync().ConfigureAwait(false);
    }
}
