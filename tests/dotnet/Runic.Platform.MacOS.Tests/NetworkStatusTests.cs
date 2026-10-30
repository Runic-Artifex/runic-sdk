using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// The runner's network path through Network.framework: a read and a watch's first state, without asserting what the
// runner's network is; then the monitor is cancelled.
internal sealed class NetworkStatusTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativeNetwork"), DisplayName("network status: nw_path_monitor reads and watches the path")]
    public async Task NativeNetworkAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Network.framework requires macOS.");
        await using var status = MacOSPlatformProvider.CreateNetworkStatus();
        Check(DesktopServiceStatus.Get(status) is CapabilityStatus.Available, "the path monitor is available");
        var read = await status.ReadAsync().AsTask().WaitScaledAsync(Wait);
        Console.WriteLine($"ReadAsync: {read}");
        Check(read is PlatformResult<DesktopNetworkState>.Success { Value.Connectivity: DesktopNetworkConnectivity.Unknown or DesktopNetworkConnectivity.None },
            "the path reads, with unknown connectivity while available");
        using var cancel = TestTimeouts.Cancellation(Wait);
        await using var watch = status.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await watch.MoveNextAsync(), "the watch reports");
        Console.WriteLine($"WatchAsync first: {watch.Current}");
        Check(watch.Current is PlatformResult<DesktopNetworkState>.Success, "the watch's initial state");
        cancel.Cancel();
        try { await watch.MoveNextAsync(); throw new InvalidOperationException("A cancelled watch continued."); }
        catch (OperationCanceledException) { }
        // Several monitors start and stop in turn.
        for (int index = 0; index < 5; index++)
            Check(await status.ReadAsync().AsTask().WaitScaledAsync(Wait) is PlatformResult<DesktopNetworkState>.Success, "a repeated read");
        Console.WriteLine("PASS native macOS network status: read, watched and cancelled");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
