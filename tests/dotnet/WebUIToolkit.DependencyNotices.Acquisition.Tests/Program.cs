using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Acquisition.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        TestHarness tests = new();
        AcquisitionTests.Register(tests);
        CacheTests.Register(tests);
        OriginIndexTests.Register(tests);
        return await tests.RunAsync().ConfigureAwait(false);
    }
}
