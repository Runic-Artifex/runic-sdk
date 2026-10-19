using Microsoft.Testing.Platform.Builder;

namespace Runic.Platform.Linux.Portal.Tests;

/// <summary>
/// The entry point is hand-written because the desktop application launch cases start this executable as the
/// application a desktop entry names: with <c>--launch-probe &lt;directory&gt;</c> it records its working directory and
/// arguments in a new file there and exits (<see cref="DesktopApplicationsTests"/>). Every other run is the ordinary
/// test application, as the generated entry point would build it.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also runs the launch probe.
    public static int Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is ["--launch-probe", var directory, ..]) return LaunchProbe.Record(directory, args[2..]);
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
