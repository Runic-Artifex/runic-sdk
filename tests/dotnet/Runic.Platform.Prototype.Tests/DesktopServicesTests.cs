using Runic.Platform;
using Runic.Platform.Runtime;

internal static class DesktopServicesTests
{
    internal static async Task RunAsync()
    {
        foreach (var path in new[] { "relative.txt", "file.txt\0" })
            Throws<ArgumentException>(() => DesktopServiceValidation.FilePath(path, DesktopFileOperation.Open));
        Throws<ArgumentOutOfRangeException>(() => DesktopServiceValidation.FilePath(Path.GetTempPath(), (DesktopFileOperation)50));
        Throws<ArgumentException>(() => DesktopServiceValidation.Notification(new("id", "Title", "Body") { Actions = [new("same", "A"), new("same", "B")] }));
        Throws<ArgumentException>(() => DesktopServiceValidation.Notification(new("id", "Title", "Body") { ActivationUri = new Uri("file:///tmp/a") }));
        Throws<ArgumentException>(() => DesktopServiceValidation.Notification(new("id", "Title", "Body") { Actions = default }));
        DesktopServiceValidation.Notification(new("id", "<title>", "Body & text") { Actions = [new("open", "Open")] });
        var source = new PendingSettings();
        var reading = source.ReadAsync().AsTask();
        await source.Entered.Task;
        var disposing = source.DisposeAsync().AsTask();
        Check(!disposing.IsCompleted, "settings disposal must drain the native read");
        source.Release.TrySetResult();
        await reading; await disposing;
        try { await source.ReadAsync(); throw new InvalidOperationException("disposed settings accepted a read"); } catch (ObjectDisposedException) { }
        await source.DisposeAsync();
        await using (var changes = new ChangingSettings())
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var watch = changes.WatchAsync(cancellation.Token).GetAsyncEnumerator();
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopAppearance>.Success, "initial preferences");
            changes.Available = false;
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopAppearance>.Unavailable, "backend loss emitted");
            changes.Available = true;
            Check(await watch.MoveNextAsync() && watch.Current is PlatformResult<DesktopAppearance>.Success, "backend recovery emitted");
        }
        var pathName = Path.Combine(Path.GetTempPath(), "runic-handoff-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(pathName, "retained");
        try
        {
            var access = new Access();
            var lease = await SaveFileLease.CreateAsync(pathName, new LocalAtomicFileReplacement(), access);
            var launcher = new PendingLauncher();
            var launch = lease.LaunchAsync(launcher).AsTask();
            await launcher.Entered.Task;
            var closed = lease.DisposeAsync().AsTask();
            Check(!closed.IsCompleted && !access.Disposed, "handoff retains security-scoped access while disposing");
            launcher.Release.TrySetResult();
            Check(await launch is PlatformResult<Unit>.Success, "native outcome returned");
            await closed;
            Check(access.Disposed && launcher.Path == pathName, "grant released after path-free lease handoff");
        }
        finally { File.Delete(pathName); }
        await using var lifetime = new PresentationLifetime(() => true, Guid.NewGuid());
        var noBackend = new PresentationFileLauncher(lifetime, null);
        Check(await noBackend.LaunchAsync(Path.GetTempPath()) is PlatformResult<Unit>.Unavailable, "unconfigured launcher is explicitly unavailable");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException(typeof(T).Name + " expected"); }
    private sealed class PendingSettings : DesktopSettingsSource
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task; return new PlatformResult<DesktopAppearance>.Success(new()); }
    }
    private sealed class ChangingSettings : DesktopSettingsSource
    {
        internal bool Available = true;
        protected override ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<PlatformResult<DesktopAppearance>>(Available ? new PlatformResult<DesktopAppearance>.Success(new()) : new PlatformResult<DesktopAppearance>.Unavailable(UnavailableReason.BackendUnavailable));
    }
    private sealed class Access : IAsyncDisposable
    {
        internal bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class PendingLauncher : IDesktopFileLauncher
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Path;
        public async ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
        { Path = path; Entered.TrySetResult(); await Release.Task; return new PlatformResult<Unit>.Success(new()); }
    }
}
