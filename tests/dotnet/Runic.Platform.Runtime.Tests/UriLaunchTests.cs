using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

// IDesktopFileLauncher.LaunchUriAsync: only absolute http, https and mailto URIs reach native dispatch; a relative
// string, a path or any other scheme is rejected before it, on every implementation (W319-003 desktop-05, plat-16).
internal sealed class UriLaunchTests
{
    internal static readonly string[] Accepted = ["https://example.org/a?b=c#d", "http://example.org", "HTTPS://EXAMPLE.ORG/", "mailto:someone@example.org?subject=Hi"];

    internal static Uri[] Rejected() =>
    [
        new("relative/page.html", UriKind.Relative),
        new("//example.org/no-scheme", UriKind.RelativeOrAbsolute),
        // Paths: an absolute path is a file: URI (or relative on Windows), a UNC path too, and a drive path.
        new("/usr/bin/xterm", UriKind.RelativeOrAbsolute),
        new(@"C:\Windows\System32\cmd.exe", UriKind.RelativeOrAbsolute),
        new(@"\\server\share\tool.exe", UriKind.RelativeOrAbsolute),
        new("file:///etc/passwd"),
        new("file://server/share/tool.exe"),
        new("javascript:alert(1)"),
        new("data:text/html,<script>alert(1)</script>"),
        new("vbscript:msgbox"),
        new("ms-settings:privacy"),
        new("shell:AppsFolder"),
        new("search-ms:query=x"),
        new("ftp://example.org/file"),
        new("tel:+15550100"),
        new("runic-test:open"),
    ];

    [Test, DisplayName("URI launch: the scheme and path policy runs before native dispatch on every launcher")]
    public async Task PolicyAsync()
    {
        foreach (var uri in Rejected())
        {
            Throws<ArgumentException>(() => DesktopUriPolicy.Require(uri), uri.OriginalString);
            Check(!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"), $"a rejected case has an allowed scheme: {uri}");
        }
        Throws<ArgumentNullException>(() => DesktopUriPolicy.Require(null!), "null");
        foreach (var text in Accepted)
            Check(DesktopUriPolicy.Require(new Uri(text)) == new Uri(text).AbsoluteUri, $"accepted {text}");

        // The Windows and macOS launchers: a rejected URI never reaches the owner's thread; an accepted one does.
        var owner = new CountingOwner();
        IDesktopFileLauncher[] launchers = [new Runic.Platform.Windows.WindowsFileLauncher(owner), new Runic.Platform.MacOS.MacDesktopFileLauncher(owner)];
        foreach (var launcher in launchers)
        {
            foreach (var uri in Rejected())
                await ThrowsAsync<ArgumentException>(() => launcher.LaunchUriAsync(uri).AsTask(), $"{launcher.GetType().Name} {uri.OriginalString}");
            await ThrowsAsync<ArgumentNullException>(() => launcher.LaunchUriAsync(null!).AsTask(), $"{launcher.GetType().Name} null");
            Check(owner.Dispatches == 0, $"{launcher.GetType().Name} dispatched a rejected URI");
            foreach (var text in Accepted)
                Check(await launcher.LaunchUriAsync(new Uri(text)) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
                    $"{launcher.GetType().Name} {text} reaches the owner");
            Check(owner.Dispatches == Accepted.Length, $"{launcher.GetType().Name} dispatched every accepted URI once");
            owner.Dispatches = 0;
        }

        // The window services' lifetime wrapper: the policy, then the lifetime, then the backend.
        var backend = new RecordingLauncher();
        await using (var lifetime = new PresentationLifetime(() => true, Guid.NewGuid()))
        {
            var launcher = new PresentationFileLauncher(lifetime, backend);
            foreach (var uri in Rejected())
                await ThrowsAsync<ArgumentException>(() => launcher.LaunchUriAsync(uri).AsTask(), "presentation " + uri.OriginalString);
            Check(backend.Uris.Count == 0, "the presentation launcher passed a rejected URI to its backend");
            Check(await launcher.LaunchUriAsync(new Uri(Accepted[0])) is PlatformResult<PlatformUnit>.Success && backend.Uris.SequenceEqual([new Uri(Accepted[0])]),
                "an accepted URI reaches the backend");
            Check(launcher.GetSnapshot().Statuses["platform.uris.launch"] is CapabilityStatus.Available, "the URI capability is available with an owner");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => launcher.LaunchUriAsync(new Uri(Accepted[0]), cancelled.Token).AsTask(), "caller cancellation");
            var unconfigured = new PresentationFileLauncher(lifetime, null);
            Check(await unconfigured.LaunchUriAsync(new Uri(Accepted[0])) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
                "no provider");
            Check(unconfigured.GetSnapshot().Statuses["platform.uris.launch"] is CapabilityStatus.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
                "no provider capability");
            await lifetime.DisposeAsync();
            Check(await launcher.LaunchUriAsync(new Uri(Accepted[0])) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "closed owner");
            await ThrowsAsync<ArgumentException>(() => launcher.LaunchUriAsync(new Uri("file:///etc/passwd")).AsTask(), "a closed launcher still rejects paths");
        }
        Check(backend.Uris.Count == 1, "only the accepted URI reached the backend");

        // Hosts without a native owner: every URI is unavailable, and the same URIs are rejected.
        Check(WindowPlatformServices.CapabilityIds.Contains("platform.uris.launch"), "window services report the URI capability");
        var unavailable = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        Check(unavailable.GetSnapshot().Statuses["platform.uris.launch"] is CapabilityStatus.Unavailable, "unavailable URI capability");
        foreach (var uri in Rejected())
            await ThrowsAsync<ArgumentException>(() => unavailable.FileLauncher.LaunchUriAsync(uri).AsTask(), "unavailable " + uri.OriginalString);
        Check(await unavailable.FileLauncher.LaunchUriAsync(new Uri(Accepted[3])) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "unavailable services report their reason");
    }

    private sealed class CountingOwner : INativeWindowOwner
    {
        internal int Dispatches;
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => true;
        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        { Dispatches++; return ValueTask.FromException(new OwnerClosedException()); }
    }

    private sealed class RecordingLauncher : IDesktopFileLauncher
    {
        internal readonly List<Uri> Uris = [];
        public ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask<PlatformResult<PlatformUnit>> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Uris.Add(uri);
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new()));
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("URI launch check failed: " + message); }
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name} expected: {label}");
    }
    private static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name} expected: {label}");
    }
}
