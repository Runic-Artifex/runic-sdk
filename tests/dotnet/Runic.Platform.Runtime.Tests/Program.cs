using Microsoft.Testing.Platform.Builder;
using Runic.Desktop;

namespace Runic.Platform.Runtime.Tests;

/// <summary>
/// The entry point is hand-written because the opt-in native cases on macOS need the AppKit event loop on the process
/// main thread (<see cref="DesktopEventLoop.Run(Func{Task})"/>), which the generated entry point does not start. When a
/// native category is selected, the whole test application runs inside that loop, as the former
/// <c>--native-services</c> and <c>--native-select</c> modes did. Every other run is the ordinary test application.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also starts the macOS event loop.
    public static int Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (OperatingSystem.IsMacOS() && args.Any(static arg => arg.Contains("Category=NativeServices", StringComparison.Ordinal)
            || arg.Contains("Category=NativeSelect", StringComparison.Ordinal)))
        {
            int exitCode = 1;
            DesktopEventLoop.Run(async () => exitCode = await RunTestsAsync(args));
            return exitCode;
        }
        return RunTestsAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> RunTestsAsync(string[] args)
    {
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
