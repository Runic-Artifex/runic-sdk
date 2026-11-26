using System.Net;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// hosted-region-loading/1 on the host (specs/application/README.md, "Loading and failure"; sdk#1377): Runic's host embeds
// the window of Runic's guest in a region of a host page that the test plays (HostedRegionTestPage). The host counts the
// frame loads its page reports until the guest's page connects, tells the page when it did, and the application decides
// when the region failed and when it loads again; the page may ask for a failed region again within the host's limits.
internal sealed class HostedRegionLoadingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class EmbedPolicy(string region) : IRunicHostedWindowPolicy
    {
        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(request.CanEmbed ? RunicHostedWindowDecision.Embed(region) : RunicHostedWindowDecision.Refuse("Region mode only."));
    }

    [Test, NotInParallel(Order = 89)]
    public async Task FrameLoadsAndTheLoadingTimeCountUntilTheGuestsPageConnectsWhichThePageIsTold()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireHostableAsync();
        var (page, window, hosted, src) = await EmbedAsync(fixture);
        var region = page.Get("check");
        Check(hosted.State == RunicHostedRegionState.Loading && hosted.FrameLoads == 0 && hosted.LoadingTime is { } started && started >= TimeSpan.Zero,
            $"The region is {hosted.State} with {hosted.FrameLoads} loads after {hosted.LoadingTime}.");
        var loaded = 0;
        hosted.FrameLoaded += (_, e) => { if (e.Region == hosted) Interlocked.Increment(ref loaded); };

        // The page's frame loaded twice, as after a failed navigation and a reload, without the guest's page connecting.
        page.FrameLoaded("check");
        page.FrameLoaded("check");
        Check(await TestTimeouts.UntilAsync(() => hosted.FrameLoads == 2 && Volatile.Read(ref loaded) == 2, Timeout),
            $"The region counted {hosted.FrameLoads} loads and raised {loaded} events.");
        var first = hosted.LoadingTime!.Value;
        await Task.Delay(50);
        Check(hosted.LoadingTime is { } later && later > first, "The loading time does not grow.");
        Check(region.Connections == 0, "The page was told the guest's page connected before it did.");

        // The guest's page connects: the region is attached, and the page is told, so it shows the frame.
        using var cancel = TestTimeouts.Cancellation(Timeout);
        var (guestPage, _) = await DesktopWireClient.ConnectFrameAsync(new Uri(src), cancel.Token);
        using (guestPage)
        {
            Check(await TestTimeouts.UntilAsync(() => hosted.State == RunicHostedRegionState.Attached && region.Connections == 1, Timeout),
                $"The region is {hosted.State}, and the page was told {region.Connections} times.");
            Check(hosted.LoadingTime is null, $"An attached region loads for {hosted.LoadingTime}.");
            // A load the page reports for an attached region counts nothing.
            page.FrameLoaded("check");
            await Task.Delay(100);
            Check(hosted.FrameLoads == 2 && Volatile.Read(ref loaded) == 2, $"An attached region counted {hosted.FrameLoads} loads.");
        }

        // The guest's page lost its connection: the region loads again, from no loads.
        Check(await TestTimeouts.UntilAsync(() => hosted.State == RunicHostedRegionState.Loading, Timeout), $"The region is {hosted.State}.");
        Check(hosted.FrameLoads == 0 && hosted.LoadingTime is not null, $"The region loads with {hosted.FrameLoads} loads after {hosted.LoadingTime}.");
        _ = await window.RequestCloseAsync(force: true);
    }

    [Test, NotInParallel(Order = 90)]
    public async Task TheApplicationFailsARegionAndRetriesItAndThePageRetriesWithinTheHostsLimits()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireHostableAsync();
        var (page, window, hosted, src) = await EmbedAsync(fixture);
        var region = page.Get("check");
        Check(Throws<ArgumentException>(() => _ = hosted.FailAsync(new string('r', 257)).AsTask()) && Throws<ArgumentException>(() => _ = hosted.FailAsync("a\0b").AsTask()),
            "An invalid reason was accepted.");
        page.FrameLoaded("check");
        Check(await TestTimeouts.UntilAsync(() => hosted.FrameLoads == 1, Timeout), "The load was not counted.");

        // The application gives up: the page shows its failure content, and the guest revoked the frame's address.
        Check(await hosted.FailAsync("unreachable").AsTask().WaitScaledAsync(Timeout), "The failure was refused.");
        Check(hosted.State == RunicHostedRegionState.Failed && hosted.FailureReason == "unreachable" && hosted.LoadingTime is null,
            $"The region is {hosted.State} ({hosted.FailureReason}).");
        Check(region is { Shows: "failed", Failure: "unreachable", Src: null }, $"The page shows {region.Shows} ({region.Failure}).");
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        using (var revoked = await client.GetAsync(new Uri(src)))
            Check(revoked.StatusCode == HttpStatusCode.Forbidden, $"A failed region's address answered {(int)revoked.StatusCode}.");

        // The page asks again: the host loads the region with a new address, from no loads.
        var contents = region.Contents;
        page.Retry("check");
        var second = await WaitForSourceAsync(page, src);
        Check(hosted.State == RunicHostedRegionState.Loading && hosted.FailureReason is null && hosted.FrameLoads == 0 && region.Contents == contents + 1,
            $"The region is {hosted.State} with {hosted.FrameLoads} loads.");

        // A page cannot ask for a region that has not failed: the host's later work in order shows that it did nothing.
        contents = region.Contents;
        page.Retry("check");
        Check(await hosted.FailAsync("timeout").AsTask().WaitScaledAsync(Timeout), "The failure was refused.");
        Check(region.Contents == contents && region is { Shows: "failed", Failure: "timeout" }, $"The page shows {region.Shows} after {region.Contents} contents.");

        // At most 3 retries of the page a minute: the third goes ahead, the fourth is refused.
        page.Retry("check");
        var third = await WaitForSourceAsync(page, second);
        Check(await hosted.FailAsync().AsTask().WaitScaledAsync(Timeout) && hosted.FailureReason is null && region is { Shows: "failed", Failure: null },
            $"The page shows {region.Shows} ({region.Failure}).");
        page.Retry("check");
        var fourth = await WaitForSourceAsync(page, third);
        Check(await hosted.FailAsync("again").AsTask().WaitScaledAsync(Timeout), "The failure was refused.");
        contents = region.Contents;
        page.Retry("check");
        Check(await hosted.FailAsync("again").AsTask().WaitScaledAsync(Timeout), "The failure was refused.");
        Check(region.Contents == contents && hosted.State == RunicHostedRegionState.Failed, $"The page's fourth retry within a minute loaded the region ({hosted.State}).");
        Check(await TestTimeouts.UntilAsync(() => fixture.Logs.Entries.Any(static entry => entry.EventId == 2031), Timeout), "The refused retry was not logged.");

        // The application's own retry is not the page's: it loads the region again.
        Check(await hosted.RetryAsync().AsTask().WaitScaledAsync(Timeout), "The application's retry failed.");
        var fifth = await WaitForSourceAsync(page, fourth);
        Check(hosted.State == RunicHostedRegionState.Loading && fifth != src, $"The region is {hosted.State}.");
        // A retry of a loading region loads it again too.
        Check(await hosted.RetryAsync().AsTask().WaitScaledAsync(Timeout), "The application's retry of a loading region failed.");
        var sixth = await WaitForSourceAsync(page, fifth);
        using (var stale = await client.GetAsync(new Uri(fifth)))
            Check(stale.StatusCode == HttpStatusCode.Forbidden, $"The address before the retry answered {(int)stale.StatusCode}.");

        // Without a region of a page, neither fails nor retries.
        page.Remove("check");
        Check(await TestTimeouts.UntilAsync(() => hosted.State == RunicHostedRegionState.Detached, Timeout), $"The region is {hosted.State}.");
        Check(!await hosted.FailAsync("unreachable").AsTask().WaitScaledAsync(Timeout) && !await hosted.RetryAsync().AsTask().WaitScaledAsync(Timeout),
            "A region that no page shows failed or retried.");
        Check(hosted.State == RunicHostedRegionState.Detached && sixth.Length > 0, $"The region is {hosted.State}.");
        _ = await window.RequestCloseAsync(force: true);
    }

    // A failed region whose page removes it is detached; and one that ends is ended.
    [Test, NotInParallel(Order = 91)]
    public async Task AFailedRegionThatThePageRemovesIsDetached()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireHostableAsync();
        var (page, window, hosted, _) = await EmbedAsync(fixture);
        Check(await hosted.FailAsync("unreachable").AsTask().WaitScaledAsync(Timeout), "The failure was refused.");
        page.Remove("check");
        Check(await TestTimeouts.UntilAsync(() => hosted.State == RunicHostedRegionState.Detached && hosted.FailureReason is null, Timeout),
            $"The region is {hosted.State} ({hosted.FailureReason}).");
        _ = await window.RequestCloseAsync(force: true);
        Check(await TestTimeouts.UntilAsync(() => hosted.State == RunicHostedRegionState.Ended, Timeout), $"The region is {hosted.State}.");
    }

    // A host window whose page declares the region "check", and Runic's guest whose window the host embeds there: the region
    // loads the frame launch address the page shows.
    private static async Task<(HostedRegionTestPage Page, RunicHostedWindow Window, RunicHostedRegion Region, string Src)> EmbedAsync(HostFixture fixture)
    {
        var surface = await fixture.Desktop.CreateSurfaceAsync();
        var hostWindow = await surface.OpenWindowAsync(RunicDesktopHost.DefaultWindowOptions);
        var page = new HostedRegionTestPage(fixture.Host.RegionDirectory, hostWindow);
        page.Declare("check");
        var opened = fixture.Next(opened: true);
        _ = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"), new RunicHostedApplicationOptions { Policy = new EmbedPolicy("check") });
        var window = await opened.WaitScaledAsync(Timeout);
        var hosted = window.Region ?? throw new InvalidOperationException("The window does not show in a region.");
        return (page, window, hosted, await WaitForSourceAsync(page, null));
    }

    // The frame launch address the page shows, once it is another than previous.
    private static async Task<string> WaitForSourceAsync(HostedRegionTestPage page, string? previous)
    {
        string? Source() => page.Find("check") is { Shows: "content", Src: { } src } && src != previous ? src : null;
        Check(await TestTimeouts.UntilAsync(() => Source() is not null, Timeout), $"The page shows {page.Find("check")?.Shows} {page.Find("check")?.Src}.");
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
