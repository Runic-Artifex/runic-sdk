using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        TestHarness tests = new();
        PackageUrlTests.Register(tests);
        SpdxTests.Register(tests);
        CoreTests.Register(tests);
        PolicyTests.Register(tests);
        ManualEngineTests.Register(tests);
        SecurityTests.Register(tests);
        FixtureCorpusTests.Register(tests);
        return await tests.RunAsync().ConfigureAwait(false);
    }
}
