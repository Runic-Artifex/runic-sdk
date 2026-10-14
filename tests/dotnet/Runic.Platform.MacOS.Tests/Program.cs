using Microsoft.Testing.Platform.Builder;

namespace Runic.Platform.MacOS.Tests;

/// <summary>
/// The entry point is hand-written because the <c>NativePrivilegedRoot</c> case installs this executable as a launch
/// daemon: launchd starts it as root with <c>--privileged-daemon</c>, and it then serves the test actions through
/// <see cref="MacPrivilegedDaemon.Run"/>. Every other run is the ordinary test application.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also runs the privileged daemon.
    public static int Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is ["--privileged-daemon"]) return PrivilegedRootTests.RunDaemon();
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
