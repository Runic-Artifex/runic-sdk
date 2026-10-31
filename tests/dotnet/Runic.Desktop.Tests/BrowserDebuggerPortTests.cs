using TUnit.Core;

namespace Runic.Desktop.Tests;

public sealed class BrowserDebuggerPortTests : IDisposable
{
    private readonly DirectoryInfo _profile = Directory.CreateTempSubdirectory("runic-debugger-port-");
    private string PortFile => Path.Combine(_profile.FullName, "DevToolsActivePort");

    [Test]
    public async Task WaitsForTheFileToBePublished()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(5));
        var pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token);
        Assert.False(pending.IsCompleted);
        await File.WriteAllTextAsync(PortFile, "52444\n/devtools/browser/test", deadline.Token);
        Assert.Equal(52444, await pending);
    }

    [Test]
    [DisplayName("WaitsForACompleteValidPortRecord(partial: $partial)")]
    [Arguments("")]
    [Arguments("5")]
    [Arguments("52444")]
    [Arguments("invalid\n")]
    [Arguments("0\n")]
    [Arguments("65536\n")]
    public async Task WaitsForACompleteValidPortRecord(string partial)
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(PortFile, partial, deadline.Token);
        // On the test's clock: each poll that found no complete record sleeps until the clock moves.
        var time = new ManualTime();
        var pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token, time);
        for (var poll = 0; poll < 3; poll++)
        {
            await time.WaitForTimerAsync(BrowserBridgeTests.PortPollInterval, deadline.Token);
            Assert.False(pending.IsCompleted, $"The reader accepted the partial record '{partial}'.");
            time.Advance(BrowserBridgeTests.PortPollInterval);
        }
        // The reader shares writes/deletion so publication can finish during polling.
        await File.WriteAllTextAsync(PortFile, "52444\r\n/devtools/browser/test", deadline.Token);
        await time.WaitForTimerAsync(BrowserBridgeTests.PortPollInterval, deadline.Token);
        time.Advance(BrowserBridgeTests.PortPollInterval);
        Assert.Equal(52444, await pending.WaitScaledAsync(TimeSpan.FromSeconds(5)));
    }

    [Test, WindowsFileLock]
    public async Task WaitsForChromiumToReleaseItsPublicationLock()
    {
        using var deadline = TestTimeouts.Cancellation(TimeSpan.FromSeconds(5));
        Task<int> pending;
        using (var writer = new FileStream(PortFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writer.Write("52444\n/devtools/browser/test"u8);
            writer.Flush();
            pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token);
            Assert.False(pending.IsCompleted);
        }
        Assert.Equal(52444, await pending);
    }

    [Test, WindowsFileLock]
    public async Task PersistentPublicationLockHonorsCancellation()
    {
        using var deadline = new CancellationTokenSource();
        using var writer = new FileStream(PortFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var time = new ManualTime();
        var pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token, time);
        // Cancelled while it polls the locked record.
        await time.WaitForTimerAsync(BrowserBridgeTests.PortPollInterval, CancellationToken.None).WaitScaledAsync(TimeSpan.FromSeconds(5));
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitScaledAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    [DisplayName("MissingOrIncompleteRecordHonorsCancellation(incomplete: $incomplete)")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingOrIncompleteRecordHonorsCancellation(bool incomplete)
    {
        if (incomplete) await File.WriteAllTextAsync(PortFile, "5");
        using var deadline = new CancellationTokenSource();
        var time = new ManualTime();
        var pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token, time);
        // Cancelled while it polls for the record.
        await time.WaitForTimerAsync(BrowserBridgeTests.PortPollInterval, CancellationToken.None).WaitScaledAsync(TimeSpan.FromSeconds(5));
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitScaledAsync(TimeSpan.FromSeconds(5)));
    }

    public void Dispose() => _profile.Delete(recursive: true);
}
