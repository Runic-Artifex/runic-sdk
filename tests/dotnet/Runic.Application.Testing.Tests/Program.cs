using Microsoft.Testing.Platform.Builder;
using Runic.Application.Testing.Tests;

/// <summary>
/// The entry point is hand-written because GeneratedClientHarness starts this executable as a helper:
/// <c>--export-generated-client-fixture &lt;path&gt;</c> writes the C# wire payloads for the Bun harness instead of
/// running the tests. Every other invocation is the TUnit test application.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The helper mode needs its own Main; TUnit's generated entry point is disabled.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is ["--export-generated-client-fixture", var fixturePath])
        {
            await GeneratedClientFixtureExporter.WriteAsync(fixturePath).ConfigureAwait(false);
            return 0;
        }
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
