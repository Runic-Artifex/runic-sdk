using System.Net;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using TUnit.Core;

// region/1 end to end: Runic's host embeds the window of Runic's guest in a region of a host page that the test plays
// (HostedRegionTestPage), and loads the frame launch address the guest answers without a browser.
internal sealed class HostedRegionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class EmbedPolicy(string region) : IRunicHostedWindowPolicy
    {
        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(request.CanEmbed ? RunicHostedWindowDecision.Embed(region) : RunicHostedWindowDecision.Refuse("Region mode only."));
    }

    [Test, NotInParallel(Order = 50)]
    public async Task EmbedsTheGuestsWindowInARegionAndAttachesItAgainByName()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
        await using var surface = await fixture.Desktop.CreateSurfaceAsync();
        var hostWindow = await surface.OpenWindowAsync(RunicDesktopHost.DefaultWindowOptions);
        var page = new HostedRegionTestPage(fixture.Host.RegionDirectory, hostWindow);
        var region = page.Declare("check");
        Check(region.Shows == "waiting", $"The page shows {region.Shows} before a window.");

        var opened = fixture.Next(opened: true);
        var attached = new TaskCompletionSource<RunicHostedRegionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Applications.RegionAttached += (_, e) => attached.TrySetResult(e);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"),
            new RunicHostedApplicationOptions { Policy = new EmbedPolicy("check") });
        var window = await opened.WaitScaledAsync(Timeout);
        Check(window.PresentationKind == RunicHostedWindowPresentationKind.Region && window.Region is { Name: "check" } && window.IsOpen,
            "The window does not show in the region.");
        Check(Throws<InvalidOperationException>(() => _ = window.Presentation), "A region window reports a native window of its own.");
        Check(fixture.Factory.Hosts.Count == 1, "The host opened a native window for the region window.");
        var hosted = window.Region!;
        Check((await attached.Task.WaitScaledAsync(Timeout)).Region == hosted, "RegionAttached named another region.");

        // The page loads the frame launch address: the guest redirects to its page with the frame token in the fragment.
        var first = await WaitForSourceAsync(page, "check", null);
        Check(Uri.TryCreate(first, UriKind.Absolute, out var frame) && frame.AbsolutePath.Contains("/_runic_frame/", StringComparison.Ordinal),
            $"The frame launch address is {first}.");
        Check(hosted.State == RunicHostedRegionState.Loading, $"The region is {hosted.State} before its page connected.");
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        using (var launch = await client.GetAsync(frame))
        {
            Check(launch.StatusCode == HttpStatusCode.SeeOther && launch.Headers.Location?.OriginalString.Contains("#runic-frame=", StringComparison.Ordinal) == true,
                $"The frame launch answered {(int)launch.StatusCode} {launch.Headers.Location}.");
        }

        // A second load of the spent code revokes the token: the host detaches and attaches again with a new code.
        using (var again = await client.GetAsync(frame)) Check(again.StatusCode == HttpStatusCode.Forbidden, $"A spent code answered {(int)again.StatusCode}.");
        var second = await WaitForSourceAsync(page, "check", first);

        // The page removes the region: the window stays open and detached, and its code no longer loads.
        var detached = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        hosted.Detached += (_, e) => detached.TrySetResult(e.Reason);
        page.Remove("check");
        Check(await detached.Task.WaitScaledAsync(Timeout) == "removed", "The region did not detach as removed.");
        Check(window.IsOpen && hosted.State == RunicHostedRegionState.Detached, $"The window is {hosted.State} after the page removed the region.");
        // The guest refuses it, whatever the status (its frame surface may be gone).
        using (var revoked = await client.GetAsync(new Uri(second)))
            Check((int)revoked.StatusCode >= 400, $"A detached window's code answered {(int)revoked.StatusCode}.");

        // The region of the same name comes back in the same host window: the window attaches again.
        page.Declare("check");
        var third = await WaitForSourceAsync(page, "check", second);
        Check(third != first && third != second, "The window attached again with a code it had used.");

        // The host's departure asks the guest, whose main Window does not guard its close: the window closes.
        Check(await hosted.AsDepartureGuard().CanDepartAsync(null!, CancellationToken.None).AsTask().WaitScaledAsync(Timeout), "The guest refused the departure.");
        await window.Closed.WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => hosted.State == RunicHostedRegionState.Ended && page.Get("check").Shows == "ended", Timeout),
            $"The region is {hosted.State} and the page shows {page.Find("check")?.Shows} after the window closed.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly after its Window closed.");
    }

    // A host Window's page as the host sees it (DesktopHostedRegionPage): the regions its Views connections declare through
    // hosted-regions/1, also those declared before the host followed it, reach the region directory, which answers the
    // page through the same connection; the page's origin is the embedder.
    [Test]
    public async Task AHostWindowsPageReachesTheRegionDirectoryThroughItsViewsConnection()
    {
        await using var fixture = await HostFixture.StartAsync();
        await using var surface = await fixture.Desktop.CreateSurfaceAsync();
        var hostWindow = await surface.OpenWindowAsync(RunicDesktopHost.DefaultWindowOptions);
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions());
        var regions = BridgeDispatcher.For(transport).HostedRegions;
        var hello = transport.Request("""{"kind":"hello","protocol":4,"session":"s1","client":{"name":"test-client","version":"1.0.0"},"features":["hosted-regions/1"]}""", "c1");
        Check(hello.StartsWith("""{"ok":true""", StringComparison.Ordinal), $"The hello was answered {hello}.");
        Check(transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c1") == """{"ok":true}""", "The declaration failed.");

        var directory = new HostedRegionDirectory();
        var page = DesktopHostedRegionPage.Follow(directory, regions, new HostedMenuWindow(), hostWindow, new Uri("http://127.0.0.1:38200/index.html"));
        Check(page.Embedder == "http://127.0.0.1:38200" && page.AllowsSameSite && ReferenceEquals(page.NativeWindow, hostWindow),
            $"The page's embedder is {page.Embedder}.");
        Check(transport.Request("""{"kind":"hosted-region-declare","name":"other","sizing":"content-height"}""", "c1") == """{"ok":true}""", "The declaration failed.");
        await regions.WhenRaised();
        await BridgeTargetedPushes.For(transport).WhenSent();
        // No window waits for either region: the directory empties both as waiting, the earlier one too.
        var pushes = transport.TakeMessages().Select(static message => message.Json).ToArray();
        Check(pushes.SequenceEqual([
            """{"kind":"hosted-region-empty","name":"check","reason":"waiting"}""",
            """{"kind":"hosted-region-empty","name":"other","reason":"waiting"}""",
        ]), $"The page received [{string.Join(", ", pushes)}].");
        Check(DesktopHostedRegionPage.Origin(new Uri("http://[::1]:40117/a/b")) == "http://[::1]:40117", "An IPv6 origin is not serialized as browsers do.");
        await hostWindow.CloseAsync();
    }

    // The frame launch address the page shows, once it is another than previous.
    private static async Task<string> WaitForSourceAsync(HostedRegionTestPage page, string name, string? previous)
    {
        string? Source() => page.Find(name) is { Shows: "content", Src: { } src } && src != previous ? src : null;
        Check(await TestTimeouts.UntilAsync(() => Source() is not null, Timeout), $"The page shows {page.Find(name)?.Shows} {page.Find(name)?.Src}.");
        return Source()!;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
