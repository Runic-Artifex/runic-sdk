using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

public sealed class NativeDispatchWorkTests
{
    [Fact]
    public async Task CancelledQueuedCallbackNeverRuns()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var work = new NativeDispatchWork(() => calls++, cancellation.Token);
        var waiting = work.WaitAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        work.Run(); work.Run();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancellationCannotAbandonRunningNativeCallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new NativeDispatchWork(() => { entered.SetResult(); release.Wait(); }, cancellation.Token);
        var waiting = work.WaitAsync();
        var running = Task.Run(work.Run);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await cancellation.CancelAsync(); Assert.False(waiting.IsCompleted); }
        finally { release.Set(); }
        await Task.WhenAll(waiting, running).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NativeCallbackFailureIsObservedOnce()
    {
        int calls = 0;
        var work = new NativeDispatchWork(() => { calls++; throw new IOException("Injected native failure."); }, default);
        work.Run(); work.Run();
        await Assert.ThrowsAsync<IOException>(() => work.WaitAsync());
        Assert.Equal(1, calls);
    }
}
