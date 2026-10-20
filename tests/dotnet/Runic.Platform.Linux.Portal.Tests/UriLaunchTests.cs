using Runic.Platform;
using Runic.Platform.Linux.Portal;
using TUnit.Core;

// IDesktopFileLauncher.LaunchUriAsync on Linux is the OpenURI portal request of PortalPlatformProvider.OpenUriAsync. A
// rejected URI never reaches the portal (W319-003 desktop-05, plat-16).
internal sealed class UriLaunchTests
{
    private static Uri[] Rejected() =>
    [
        new("relative/page.html", UriKind.Relative),
        new("/usr/bin/xterm", UriKind.RelativeOrAbsolute),
        new(@"\\server\share\tool.exe", UriKind.RelativeOrAbsolute),
        new("file:///usr/bin/xterm"),
        new("javascript:alert(1)"),
        new("data:text/html,x"),
        new("ftp://example.org/file"),
        new("runic-test:open"),
    ];

    [Test, DisplayName("URI launch: the launcher asks the OpenURI portal for accepted URIs only, and maps its responses")]
    public async Task OpenUriAsync()
    {
        var owner = new Owner();
        var refused = new FakeTransport(new(0, []));
        var launcher = new PortalFileLauncher(owner, uriTransport: refused);
        foreach (var uri in Rejected())
        {
            await ThrowsAsync<ArgumentException>(() => launcher.LaunchUriAsync(uri).AsTask(), uri.OriginalString);
            await ThrowsAsync<ArgumentException>(() => PortalPlatformProvider.OpenUriAsync(owner, uri).AsTask(), "provider " + uri.OriginalString);
        }
        await ThrowsAsync<ArgumentNullException>(() => launcher.LaunchUriAsync(null!).AsTask(), "null");
        Check(refused.Requests == 0 && owner.Released == 0, "a rejected URI reached the portal or exported the parent");

        foreach (var (code, expected) in new (uint, Func<PlatformResult<PlatformUnit>, bool>)[]
        {
            (0, static result => result is PlatformResult<PlatformUnit>.Success),
            (1, static result => result is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed }),
            (2, static result => result is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Other }),
        })
        {
            var transport = new FakeTransport(new(code, []));
            var result = await new PortalFileLauncher(owner, uriTransport: transport).LaunchUriAsync(new Uri("HTTPS://Example.org/a b?c=d"));
            Check(expected(result), $"portal response {code}: {result}");
            Check(transport is { Method: "OpenURI", Argument: "https://example.org/a%20b?c=d", Requests: 1 }, "the OpenURI request carries the absolute URI");
        }
        var mail = new FakeTransport(new(0, []));
        Check(await new PortalFileLauncher(owner, uriTransport: mail).LaunchUriAsync(new Uri("mailto:someone@example.org")) is PlatformResult<PlatformUnit>.Success
            && mail.Argument == "mailto:someone@example.org", "mailto");
        owner.IsAvailable = false;
        Check(await new PortalFileLauncher(owner, uriTransport: new FakeTransport(new(0, []))).LaunchUriAsync(new Uri("https://example.org/"))
            is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "closed owner");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("URI launch check failed: " + message); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"{typeof(T).Name} expected: {label}");
    }
}
