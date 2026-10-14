using Microsoft.Testing.Platform.Builder;
using Runic.Platform.Windows.Elevation;

namespace Runic.Elevation.Smoke;

/// <summary>
/// The entry point is hand-written because this executable is also the stand-in helper of the NativeElevation cases:
/// <c>--stand-in elevated|any|nativeaot ARGUMENTS...</c> runs <see cref="ElevatedHelper.Match"/> and the smoke's
/// actions, started with CreateProcess instead of runas. <c>elevated</c> keeps the elevation check, <c>nativeaot</c>
/// keeps both the elevation and the NativeAOT checks, and <c>any</c> drops both.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also serves the stand-in helper.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is ["--stand-in", var mode, .. var helperArguments])
        {
            ElevatedHelper.RequireElevation = mode is "elevated" or "nativeaot";
            ElevatedHelper.RequireNativeAot = mode is "nativeaot";
            // The stand-in runs from the build output, which the user can change.
            ElevatedHelper.RequireTrustedLocation = false;
            if (ElevatedHelper.Match(PrivilegedActions.All, helperArguments, out var arguments) is not { } action) return 2;
            return SmokeActions.Run(action, arguments);
        }
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
