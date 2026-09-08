namespace Runic.Desktop.Tests;

public sealed class BrowserDebuggerPortTests : IDisposable
{
    private readonly DirectoryInfo _profile = Directory.CreateTempSubdirectory("runic-debugger-port-");
    private string PortFile => Path.Combine(_profile.FullName, "DevToolsActivePort");

    [Fact]
    public async Task WaitsForTheFileToBePublished()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token);
        Assert.False(pending.IsCompleted);
        await File.WriteAllTextAsync(PortFile, "52444\n/devtools/browser/test", deadline.Token);
        Assert.Equal(52444, await pending);
    }

    [Theory]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("52444")]
    [InlineData("invalid\n")]
    [InlineData("0\n")]
    [InlineData("65536\n")]
    public async Task WaitsForACompleteValidPortRecord(string partial)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await File.WriteAllTextAsync(PortFile, partial, deadline.Token);
        var pending = BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromMilliseconds(75)));
        // The reader shares writes/deletion so publication can finish during polling.
        await File.WriteAllTextAsync(PortFile, "52444\r\n/devtools/browser/test", deadline.Token);
        Assert.Equal(52444, await pending);
    }

    [WindowsFileLockFact]
    public async Task WaitsForChromiumToReleaseItsPublicationLock()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    [WindowsFileLockFact]
    public async Task PersistentPublicationLockHonorsCancellation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var writer = new FileStream(PortFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIncompleteRecordHonorsCancellation(bool incomplete)
    {
        if (incomplete) await File.WriteAllTextAsync(PortFile, "5");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BrowserBridgeTests.ReadDebuggerPortAsync(_profile.FullName, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public void Dispose() => _profile.Delete(recursive: true);
}
