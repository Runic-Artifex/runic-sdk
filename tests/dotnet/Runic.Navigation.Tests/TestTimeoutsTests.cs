using TUnit.Core;

namespace Runic.Navigation.Tests;

// The shared condition-wait helper (tests/support/TestTimeouts.cs, eng/ci/README.md "Test timeouts"), checked here once
// for every project that compiles it.
internal sealed class TestTimeoutsTests
{
    [Test]
    public void TheScaleIsAPositiveNumberThatDefaultsToOne()
    {
        Require(TestTimeouts.ReadFactor(null) == 1 && TestTimeouts.ReadFactor(" ") == 1, "An unset scale is not 1.");
        Require(TestTimeouts.ReadFactor("2.5") == 2.5, "A fractional scale was not read with the invariant culture.");
        foreach (var invalid in new[] { "0", "-1", "fast", "NaN", "Infinity" })
        {
            try
            {
                TestTimeouts.ReadFactor(invalid);
                throw new InvalidOperationException($"The scale '{invalid}' was accepted.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("must be a positive number", StringComparison.Ordinal)) { }
        }
        Require(TestTimeouts.Scale(Timeout.InfiniteTimeSpan) == Timeout.InfiniteTimeSpan && TestTimeouts.Scale(TimeSpan.Zero) == TimeSpan.Zero,
            "An infinite or zero limit was scaled.");
        Require(TestTimeouts.Scale(TimeSpan.FromSeconds(2)) >= TimeSpan.FromSeconds(2 * TestTimeouts.Factor) - TimeSpan.FromTicks(1),
            "A limit was not multiplied by the scale.");
    }

    [Test]
    public async Task AScaledWaitEndsWithItsConditionOrTimesOutNamingBothLimits()
    {
        Require(await Task.FromResult(7).WaitScaledAsync(TimeSpan.FromSeconds(5)) == 7, "A completed wait lost its result.");
        var never = new TaskCompletionSource().Task;
        try
        {
            await never.WaitScaledAsync(TimeSpan.FromMilliseconds(20));
            throw new InvalidOperationException("A wait that never completes did not time out.");
        }
        catch (TimeoutException error)
        {
            Require(error.Message.Contains(nameof(AScaledWaitEndsWithItsConditionOrTimesOutNamingBothLimits), StringComparison.Ordinal)
                && error.Message.Contains("TestTimeoutsTests.cs:", StringComparison.Ordinal)
                && error.Message.Contains(TestTimeouts.Variable, StringComparison.Ordinal), $"The timeout does not name the wait: {error.Message}");
        }
        var failing = Task.FromException(new TimeoutException("the product's own timeout"));
        try
        {
            await failing.WaitScaledAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("A failed task's exception was lost.");
        }
        catch (TimeoutException error) { Require(error.Message == "the product's own timeout", "A product TimeoutException was rewritten."); }
        var flag = false;
        _ = Task.Delay(30).ContinueWith(_ => Volatile.Write(ref flag, true), TaskScheduler.Default);
        Require(await TestTimeouts.UntilAsync(() => Volatile.Read(ref flag), TimeSpan.FromSeconds(5)), "UntilAsync missed its condition.");
        Require(!await TestTimeouts.UntilAsync(() => false, TimeSpan.FromMilliseconds(30)), "UntilAsync held a condition that never held.");
        var reads = 0;
        Require(await TestTimeouts.UntilAsync(async () => { await Task.Yield(); return ++reads == 3; }, TimeSpan.FromSeconds(5)),
            "UntilAsync missed an asynchronous condition.");
        Require(!await TestTimeouts.UntilAsync(() => Task.FromResult(false), TimeSpan.FromMilliseconds(30)),
            "UntilAsync held an asynchronous condition that never held.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
