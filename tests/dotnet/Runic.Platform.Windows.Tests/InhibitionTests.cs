using System.Diagnostics;
using Runic.Platform;
using Runic.Platform.Windows;

internal static class InhibitionTests
{
    internal static async Task<int> RunAsync(bool inspectPowerRequests)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native inhibition requires Windows.");
        var provider = WindowsPlatformProvider.CreateInhibition();
        string firstReason = "Runic system export " + Guid.NewGuid().ToString("N");
        string secondReason = "Runic display presentation " + Guid.NewGuid().ToString("N");
        await using var first = await AcquireAsync(provider, DesktopInhibitionEffects.SystemSleep, firstReason);
        await using var second = await AcquireAsync(provider, DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep, secondReason);
        if (inspectPowerRequests) await CheckRequestsAsync(firstReason, secondReason, true, true);
        await Task.WhenAll(first.DisposeAsync().AsTask(), first.DisposeAsync().AsTask());
        if (inspectPowerRequests) await CheckRequestsAsync(firstReason, secondReason, false, true);
        await second.DisposeAsync();
        if (inspectPowerRequests) await CheckRequestsAsync(firstReason, secondReason, false, false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await using var unexpected = await AcquireAsync(provider, DesktopInhibitionEffects.SystemSleep, "Cancelled operation", cancelled.Token);
            throw new InvalidOperationException("Cancelled acquisition succeeded.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS native Windows inhibition: independent acquisition, idempotent disposal and cancellation" +
            (inspectPowerRequests ? "; powercfg confirms both requests, independent removal and final cleanup." : ". OS power policy was not inspected."));
        return 0;
    }

    private static async Task<IDesktopInhibitionLease> AcquireAsync(IDesktopInhibition provider, DesktopInhibitionEffects effects, string reason, CancellationToken token = default)
    {
        var result = await provider.AcquireAsync(effects, reason, token);
        if (result is PlatformResult<IDesktopInhibitionLease>.Success success) return success.Value;
        throw new InvalidOperationException("Inhibition acquisition failed: " + result);
    }

    private static async Task CheckRequestsAsync(string firstReason, string secondReason, bool firstExpected, bool secondExpected)
    {
        // Optional diagnostic: powercfg /requests requires an elevated Windows
        // session. It reads existing requests and never changes power policy.
        using var process = Process.Start(new ProcessStartInfo("powercfg.exe")
        {
            ArgumentList = { "/requests" }, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Cannot start powercfg.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string text = await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("powercfg failed: " + text + await error);
        Console.WriteLine(text);
        if (text.Contains(firstReason, StringComparison.Ordinal) != firstExpected || text.Contains(secondReason, StringComparison.Ordinal) != secondExpected)
            throw new InvalidOperationException("OS power requests did not match the owned leases.");
    }
}
