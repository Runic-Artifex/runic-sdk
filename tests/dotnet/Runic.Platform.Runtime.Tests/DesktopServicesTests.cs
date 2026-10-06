using Runic.Platform;
using Runic.Platform.Runtime;

internal static class DesktopServicesTests
{
    internal static async Task RunAsync()
    {
        foreach (var path in new[] { "relative.txt", "file.txt\0" })
            Throws<ArgumentException>(() => DesktopServiceValidation.FilePath(path, DesktopFileOperation.Open));
        Throws<ArgumentOutOfRangeException>(() => DesktopServiceValidation.FilePath(Path.GetTempPath(), (DesktopFileOperation)50));
        if (OperatingSystem.IsWindows())
            foreach (var path in new[] { @"\\server\share\a.txt", "//server/share/a.txt", @"\\?\C:\a.txt", @"\\.\PhysicalDrive0", @"C:\a.txt:payload.exe" })
                Throws<ArgumentException>(() => DesktopServiceValidation.FilePath(path, DesktopFileOperation.Open));
        await LaunchPolicyAsync();
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
            Check(await launch is PlatformResult<PlatformUnit>.Success, "native outcome returned");
            await closed;
            Check(access.Disposed && launcher.Path == pathName, "grant released after path-free lease handoff");
        }
        finally { File.Delete(pathName); }
        await using var lifetime = new PresentationLifetime(() => true, Guid.NewGuid());
        var noBackend = new PresentationFileLauncher(lifetime, null);
        Check(await noBackend.LaunchAsync(Path.GetTempPath()) is PlatformResult<PlatformUnit>.Unavailable, "unconfigured launcher is explicitly unavailable");
    }
    // Policy runs before native dispatch, so a closing owner observes only accepted documents.
    private static async Task LaunchPolicyAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string document = Path.Combine(directory, "report.txt"), tool = Path.Combine(directory, "tool");
            await File.WriteAllTextAsync(document, "document");
            await File.WriteAllTextAsync(tool, "#!/bin/sh");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var owner = new ClosedOwner();
            var mac = new Runic.Platform.MacOS.MacDesktopFileLauncher(owner);
            (IDesktopFileLauncher Launcher, string[] Programs)[] cases =
            [
                (new Runic.Platform.Windows.WindowsFileLauncher(owner), ["setup.exe", "run.CMD", "shortcut.lnk", "page.url"]),
                (mac, ["Tool.app", "script.command", "Tool.app" + Path.DirectorySeparatorChar]),
            ];
            foreach (var (launcher, programs) in cases)
            {
                foreach (var name in programs)
                    Check(await launcher.LaunchAsync(Path.Combine(directory, name)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
                        $"{launcher.GetType().Name} opened program {name}");
                Check(await launcher.LaunchAsync(document) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "document handoff reaches the owner");
                Check(await launcher.LaunchAsync(Path.Combine(directory, "setup.exe"), DesktopFileOperation.Reveal) is PlatformResult<PlatformUnit>.Unavailable,
                    "revealing a program is permitted");
            }
            int dispatched = owner.Dispatches;
            if (!OperatingSystem.IsWindows())
            {
                Check(await mac.LaunchAsync(tool) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "extensionless executable opened");
                string alias = Path.Combine(directory, "notes.txt");
                File.CreateSymbolicLink(alias, tool);
                Check(await mac.LaunchAsync(alias) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "symlinked executable opened");
                Check(owner.Dispatches == dispatched, "refused handoff reached native dispatch");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private sealed class ClosedOwner : INativePickerOwner
    {
        internal int Dispatches;
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        { Dispatches++; return ValueTask.FromException(new OwnerClosedException()); }
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
            ValueTask.FromResult<PlatformResult<DesktopAppearance>>(Available ? new PlatformResult<DesktopAppearance>.Success(new()) : new PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
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
        public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
        { Path = path; Entered.TrySetResult(); await Release.Task; return new PlatformResult<PlatformUnit>.Success(new()); }
    }
}
