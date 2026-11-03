using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// B4: the runner's power source, Low Power Mode and screen lock state, read and watched without asserting what they
// are; then the observers are removed. Sleep, wake and lock need a person: the PR's manual checks.
internal sealed class PowerEventsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativePower"), DisplayName("power events: IOKit, NSProcessInfo and the session read and watched")]
    public async Task NativePowerAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("IOKit requires macOS.");
        await using var events = MacOSPlatformProvider.CreatePowerEvents();
        Check(DesktopServiceStatus.Get(events) is CapabilityStatus.Available, "power events are available");
        var read = await events.ReadAsync().AsTask().WaitScaledAsync(Wait);
        Console.WriteLine($"ReadAsync: {read}");
        Check(read is PlatformResult<DesktopPowerState>.Success { Value.IsLowPower: not null }, "the state reads, with Low Power Mode on macOS 12 and later");
        for (int index = 0; index < 3; index++)
        {
            using var cancel = TestTimeouts.Cancellation(Wait);
            await using var watch = events.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
            Check(await watch.MoveNextAsync(), "the watch reports");
            Console.WriteLine($"WatchAsync first: {watch.Current}");
            Check(watch.Current is PlatformResult<DesktopPowerEvent>.Success { Value.Kind: DesktopPowerEventKind.StateChanged }, "the watch's initial state");
            cancel.Cancel();
            try { await watch.MoveNextAsync(); throw new InvalidOperationException("A cancelled watch continued."); }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS native macOS power events: read, watched and removed");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
