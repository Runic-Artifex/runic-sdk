using Microsoft.Testing.Platform.Builder;

namespace Runic.Platform.Windows.Tests;

/// <summary>
/// The entry point is hand-written because this executable is also started by others: the native clipboard case
/// starts it again as an independent peer (<c>--native-hold</c>, <c>--native-write &lt;text&gt;</c>), and Windows
/// launches it as the registered protocol handler with <c>--notification-activation &lt;uri&gt; &lt;receipt&gt;</c>
/// (see tests/native/windows-notifications/Configure-TestIdentity.ps1). The desktop applications case registers it as
/// a test-owned file and protocol handler that records its arguments (<c>--record-launch &lt;log&gt; &lt;items&gt;</c>).
/// Those invocations run before the test platform parses arguments.
/// </summary>
internal static class Program
{
#pragma warning disable TUnit0034 // The generated test entry point is disabled; this one also serves the peer and activation modes.
    public static async Task<int> Main(string[] args)
#pragma warning restore TUnit0034
    {
        if (args is ["--native-hold", ..] or ["--native-write", ..]) return NativeTests.Run(args);
        if (args is ["--record-launch", ..]) return ApplicationsTests.RecordLaunch(args);
        if (ApplicationsTests.RecordStrayLaunch(args)) return 0;
        if (args is ["--notification-activation", ..]) return await NotificationTests.RunAsync(args).ConfigureAwait(false);
        ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(args).ConfigureAwait(false);
        builder.AddSelfRegisteredExtensions(args);
        using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
        return await application.RunAsync().ConfigureAwait(false);
    }
}
