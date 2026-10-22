using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;

// IDesktopFileLauncher.LaunchUriAsync: only absolute http, https, mailto and ms-settings URIs reach native dispatch; a
// relative string, a path or any other scheme is rejected before it, on every implementation (W319-003 desktop-05,
// plat-16, D028.5). ms-settings: is exactly ms-settings:<page> on every platform (D031.6); it reaches only the Windows
// shell, and macOS and Linux report it unavailable.
internal sealed class UriLaunchTests
{
    internal static readonly string[] Accepted = ["https://example.org/a?b=c#d", "http://example.org", "HTTPS://EXAMPLE.ORG/", "mailto:someone@example.org?subject=Hi"];

    // Windows Settings pages: the scheme in any case, a page of letters, digits and hyphens, compared lower-case.
    internal static readonly (string Text, string Target)[] Settings =
    [
        ("ms-settings:display", "ms-settings:display"), ("MS-SETTINGS:Privacy-Webcam", "ms-settings:privacy-webcam"),
        ("ms-settings:defaultapps", "ms-settings:defaultapps"), ("ms-settings:" + new string('a', 64), "ms-settings:" + new string('a', 64)),
    ];

    // Settings URIs outside the page rule (SEC-08): a path, escapes, a query, a fragment, an empty or long page,
    // non-ASCII letters, and -action pages, which act instead of showing a page.
    internal static Uri[] RejectedSettings() =>
    [
        new("ms-settings:"),
        new("ms-settings:../../Windows/System32/cmd.exe"),
        new(@"ms-settings:..\..\Windows\System32\cmd.exe"),
        new("ms-settings:display/advanced"),
        new("ms-settings:display?x=1"),
        new("ms-settings:display#top"),
        new("ms-settings:%64isplay"),
        new("ms-settings:dis play"),
        new("ms-settings:" + new string('a', 65)),
        new("ms-settings:" + new string('a', 8000)),
        new("ms-settings:windowsupdate-action"),
        new("MS-SETTINGS:WindowsUpdate-Action"),
        new("ms-settings:dis\u212Aplay"),
        new("ms-settings://display"),
    ];

    // mailto URIs that name a file for the mail client to attach (SEC-07), in any case and escaping, also twice escaped.
    internal static Uri[] RejectedMail() =>
    [
        new("mailto:someone@example.org?attach=/home/user/.ssh/id_ed25519"),
        new("mailto:someone@example.org?subject=Hi&ATTACHMENT=C:%5CUsers%5Cme%5Csecret.txt"),
        new("mailto:someone@example.org?body=x;Attach=~/.ssh/id_rsa"),
        new("mailto:?%61ttach=/etc/passwd"),
        new("mailto:?%2561ttach=/etc/passwd"),
        new("mailto:someone@example.org?%20attach%20=/etc/passwd"),
        new("mailto:someone@example.org?attachments=/etc/passwd&subject=x"),
    ];

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
        // Look-alikes of the accepted schemes.
        new("ms-settingsx:display"),
        new("ms-setting:display"),
        new("ms-settings-app:display"),
        new("httpx://example.org"),
        new("mailtox:someone@example.org"),
        new("shell:AppsFolder"),
        new("search-ms:query=x"),
        new("ftp://example.org/file"),
        new("tel:+15550100"),
        new("runic-test:open"),
        .. RejectedSettings(),
    ];

    [Test, DisplayName("URI launch: the scheme and path policy runs before native dispatch on every launcher")]
    public async Task PolicyAsync()
    {
        foreach (var uri in Rejected())
        {
            Throws<ArgumentException>(() => DesktopUriPolicy.Require(uri), uri.OriginalString);
            Check(!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "mailto"), $"a rejected case has an allowed scheme: {uri}");
        }
        foreach (var uri in RejectedMail())
        {
            try
            {
                DesktopUriPolicy.Require(uri);
                Check(false, $"accepted {uri}");
            }
            catch (ArgumentException error)
            {
                Check(error.Message.StartsWith(DesktopUriPolicy.MailtoAttachmentRejectedMessage, StringComparison.Ordinal), $"the attachment message for {uri}");
            }
        }
        Check(DesktopUriPolicy.Require(new Uri("mailto:someone@example.org?subject=attach%20this&body=attachment")) is { Length: > 0 },
            "a value that mentions attachments is not a field name");
        Throws<ArgumentNullException>(() => DesktopUriPolicy.Require(null!), "null");
        foreach (var text in Accepted)
            Check(DesktopUriPolicy.Require(new Uri(text)) == new Uri(text).AbsoluteUri && !DesktopUriPolicy.IsWindowsOnly(new Uri(text)), $"accepted {text}");
        foreach (var (text, target) in Settings)
            Check(DesktopUriPolicy.Require(new Uri(text)) == target && DesktopUriPolicy.IsWindowsOnly(new Uri(text)), $"accepted Settings URI {text}");
        foreach (var uri in RejectedSettings())
        {
            try { DesktopUriPolicy.Require(uri); throw new InvalidOperationException($"accepted {uri.OriginalString}"); }
            catch (ArgumentException error) { Check(error.Message.StartsWith(DesktopUriPolicy.SettingsRejectedMessage, StringComparison.Ordinal), $"the page rule names {uri.OriginalString}"); }
        }

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

        // ms-settings: reaches the Windows shell; macOS reports it unavailable without reaching the owner's thread.
        var windows = new Runic.Platform.Windows.WindowsFileLauncher(owner);
        foreach (var (text, _) in Settings)
            Check(await windows.LaunchUriAsync(new Uri(text)) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
                $"Windows dispatches {text[..Math.Min(text.Length, 60)]}");
        Check(owner.Dispatches == Settings.Length, "Windows dispatched every Settings URI once");
        owner.Dispatches = 0;
        var mac = new Runic.Platform.MacOS.MacDesktopFileLauncher(owner);
        foreach (var (text, _) in Settings)
            Check(await mac.LaunchUriAsync(new Uri(text)) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                $"macOS reports {text[..Math.Min(text.Length, 60)]} unavailable");
        Check(owner.Dispatches == 0, "macOS dispatched a Settings URI");
        using (var cancelledSettings = new CancellationTokenSource())
        {
            cancelledSettings.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => mac.LaunchUriAsync(new Uri(Settings[0].Text), cancelledSettings.Token).AsTask(), "macOS Settings URI, cancelled");
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
            Check(await launcher.LaunchUriAsync(new Uri(Settings[0].Text)) is PlatformResult<PlatformUnit>.Success && backend.Uris.Count == 2, "a Settings URI reaches the backend");
            Check(launcher.GetSnapshot().Statuses[PlatformCapabilityIds.UrisLaunch] is CapabilityStatus.Available, "the URI capability is available with an owner");
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
        Check(backend.Uris.Count == 2, "only the accepted URIs reached the backend");

        // Hosts without a native owner: every URI is unavailable, and the same URIs are rejected.
        Check(WindowPlatformServices.CapabilityIds.Contains("platform.uris.launch"), "window services report the URI capability");
        var unavailable = WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable);
        Check(unavailable.GetSnapshot().Statuses["platform.uris.launch"] is CapabilityStatus.Unavailable, "unavailable URI capability");
        foreach (var uri in Rejected())
            await ThrowsAsync<ArgumentException>(() => unavailable.FileLauncher.LaunchUriAsync(uri).AsTask(), "unavailable " + uri.OriginalString);
        Check(await unavailable.FileLauncher.LaunchUriAsync(new Uri(Accepted[3])) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "unavailable services report their reason");
        Check(await unavailable.FileLauncher.LaunchUriAsync(new Uri(Settings[0].Text)) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "unavailable services report their reason for a Settings URI");
    }

    [Test, DisplayName("Capability identifiers: the constants are the window services' identifiers and the snapshot keys")]
    public async Task CapabilityIdsAsync()
    {
        string[] constants = [.. typeof(PlatformCapabilityIds).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(static field => (string)field.GetRawConstantValue()!)];
        Check(constants.Length == 12 && constants.Distinct().Count() == constants.Length, "twelve distinct constants");
        Check(WindowPlatformServices.CapabilityIds.Order(StringComparer.Ordinal).SequenceEqual(constants.Order(StringComparer.Ordinal)), "CapabilityIds holds every constant");
        Check(WindowPlatformServices.Unavailable(PlatformUnavailableReason.OwnerUnavailable).GetSnapshot().Statuses.Keys.Order(StringComparer.Ordinal)
            .SequenceEqual(constants.Order(StringComparer.Ordinal)), "the unavailable services report every constant");
        await using var lifetime = new PresentationLifetime(() => true, Guid.NewGuid());
        IPlatformCapabilities[] parts = [new PresentationFileLauncher(lifetime), new PresentationClipboard(lifetime), new PresentationFiles(lifetime)];
        // The menu and monitor capabilities come from the window host, which shows menus and lists monitors without a
        // platform provider.
        string[] menus = [PlatformCapabilityIds.MenuBar, PlatformCapabilityIds.ContextMenu, PlatformCapabilityIds.Monitors];
        Check(parts.SelectMany(static part => part.GetSnapshot().Statuses.Keys).Order(StringComparer.Ordinal)
            .SequenceEqual(constants.Except(menus).Order(StringComparer.Ordinal)), "the presentation services report every constant but the menus");
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
