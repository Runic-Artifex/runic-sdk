using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Npm.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        TestHarness tests = new();
        NpmInventoryTests.Register(tests);
        NpmSecurityTests.Register(tests);
        return await tests.RunAsync().ConfigureAwait(false);
    }
}
