using System.Runtime.Versioning;
using TUnit.Core;

namespace Runic.Desktop.Tests;

// DesktopEventLoop.InvokeOnMainThreadAsync without a running loop: the argument, platform, cancellation and "Run first"
// checks. The macOS native smoke (--main-thread) runs the work on the main thread under the loop.
#pragma warning disable CA1416 // The cases check what other platforms get, too.
public sealed class MainThreadDispatchTests
{
    [Test]
    public async Task NullWorkIsRefusedOnEveryPlatform()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await DesktopEventLoop.InvokeOnMainThreadAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await DesktopEventLoop.InvokeOnMainThreadAsync<int>(null!));
    }

    [Test]
    public async Task OtherPlatformsReportTheApiUnsupported()
    {
        if (OperatingSystem.IsMacOS()) return;
        var ran = false;
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await DesktopEventLoop.InvokeOnMainThreadAsync(() => { ran = true; }));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await DesktopEventLoop.InvokeOnMainThreadAsync(() => 1));
        Assert.False(ran);
    }

    [Test]
    public async Task MacOSRefusesAnotherThreadBeforeTheLoopRuns()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Test cases run on the thread pool, and this test application starts no Desktop event loop.
        var ran = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await DesktopEventLoop.InvokeOnMainThreadAsync(() => { ran = true; }));
        Assert.Contains("DesktopEventLoop.Run", error.Message, StringComparison.Ordinal);
        Assert.False(ran);
    }

    [Test]
    public void ALambdaWithAValueBindsToTheFuncOverload()
    {
        // As with Task.Run: an expression lambda with a value returns it; a statement lambda without one is an action.
        if (!OperatingSystem.IsMacOS()) return;
        _ = (Func<ValueTask<int>>)(() => DesktopEventLoop.InvokeOnMainThreadAsync(() => 1));
        _ = (Func<ValueTask>)(() => DesktopEventLoop.InvokeOnMainThreadAsync(() => { }));
    }

    [Test]
    public async Task MacOSRefusesACancelledRequestBeforeQueueingIt()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await DesktopEventLoop.InvokeOnMainThreadAsync(static () => { }, cancelled.Token));
    }
}
#pragma warning restore CA1416
