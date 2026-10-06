using Microsoft.Extensions.Logging;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

public sealed class WindowCloseControllerTests
{
    [Fact]
    public async Task ConcurrentRequestsShareDecisionAndCanRetryAfterVeto()
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var closes = 0;
        using var controller = new WindowCloseController(_ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            return new(decision.Task);
        }, () => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; });

        var first = controller.RequestAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = controller.RequestAsync();
        controller.RequestFromPlatform();
        decision.SetResult(false);
        Assert.False(await first);
        Assert.False(await second);
        Assert.Equal(1, calls);
        Assert.Equal(0, closes);

        decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        decision.SetResult(true);
        Assert.True(await controller.RequestAsync());
        Assert.Equal(2, calls);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task CancellingOneWaitDoesNotApproveOrCancelSharedDecision()
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closes = 0;
        using var controller = new WindowCloseController(_ => new(decision.Task),
            () => { closes++; return ValueTask.CompletedTask; });
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = controller.RequestAsync(cancellation.Token);
        var otherWait = controller.RequestAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.False(otherWait.IsCompleted);
        Assert.Equal(0, closes);
        decision.SetResult(true);
        Assert.True(await otherWait);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task ForcedShutdownCancelsPromptAndIgnoresLateApproval()
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closes = 0;
        using var controller = new WindowCloseController(token =>
        {
            entered.SetResult(token);
            return new(decision.Task); // Deliberately ignore cancellation.
        }, () => { closes++; return ValueTask.CompletedTask; });
        var request = controller.RequestAsync();
        var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.Dispose();
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(token.IsCancellationRequested);
        decision.SetResult(true);
        Assert.False(await controller.RequestAsync());
        Assert.Equal(0, closes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledConfirmationKeepsWindowOpen(bool cancelled)
    {
        var closes = 0;
        using var controller = new WindowCloseController(_ => cancelled
            ? ValueTask.FromCanceled<bool>(new CancellationToken(true))
            : ValueTask.FromException<bool>(new InvalidOperationException("Prompt failed")),
            () => { closes++; return ValueTask.CompletedTask; });
        Assert.False(await controller.RequestAsync());
        Assert.False(await controller.RequestAsync());
        Assert.Equal(0, closes);
    }

    [Fact]
    public async Task FailedConfirmationIsLoggedWithItsException()
    {
        var logger = new CapturingLogger();
        using var controller = new WindowCloseController(
            _ => ValueTask.FromException<bool>(new InvalidOperationException("customer-secret")),
            () => ValueTask.CompletedTask, logger);
        Assert.False(await controller.RequestAsync());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(3001, entry.EventId.Id);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(typeof(InvalidOperationException).FullName!, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("customer-secret", entry.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, eventId, formatter(state, exception), exception));
        }
    }
}
