using Microsoft.Testing.Platform.Builder;
using Runic.Application.Views.Desktop.Tests;

/// <summary>
/// The entry point is hand-written because the hosted application cases start this executable as the guest they host:
/// <c>--hosted-guest &lt;mode&gt;</c> runs <see cref="HostedGuestProgram"/> in hosted mode, and <c>--puppet-guest</c>
/// relays standard input and output to the host conformance runner, and <c>--region-browser-host &lt;folder&gt;</c> runs
/// <see cref="RegionBrowserHostProgram"/>, a browser (serve-only) host for hosted-region-browser-smoke.mjs. Every other
/// invocation is the TUnit test application.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The guest modes need their own Main; TUnit's generated entry point is disabled.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        // Before any await: RunicDesktopHost.Run needs the process main thread on macOS.
        if (args is [HostedGuestProgram.Argument, var mode]) return HostedGuestProgram.Run(mode);
        if (args is [HostedGuestProgram.PuppetArgument]) return HostedGuestProgram.RunPuppet();
        if (args is [RegionBrowserHostProgram.Argument, var folder]) return RegionBrowserHostProgram.Run(folder);
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
