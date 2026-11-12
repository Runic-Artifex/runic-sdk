using System.Net;
using System.Net.Sockets;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using TUnit.Core;

// Browser (serve-only) hosts (host-protocol.md, Browser hosts; D059.10): a serve-only application hosts guests and shows
// their windows only in regions of its pages, which run in the user's browser. Browser-level checks (Chromium, cookies,
// framing) are in hosted-region-browser-smoke.mjs.
internal sealed class HostedRegionBrowserHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed class EmbedPolicy(string region) : IRunicHostedWindowPolicy
    {
        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(request.CanEmbed ? RunicHostedWindowDecision.Embed(region) : RunicHostedWindowDecision.Refuse("Region mode only."));
    }

    // A policy that lets every window open as a window of its own, as the default does.
    private sealed class AllowPolicy : IRunicHostedWindowPolicy
    {
        public List<RunicHostedWindowRequest> Requests { get; } = [];

        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return ValueTask.FromResult(RunicHostedWindowDecision.Allow());
        }
    }

    [Test, NotInParallel(Order = 51)]
    public async Task AServeOnlyHostShowsAGuestsWindowInARegionOfABrowserPage()
    {
        await using var fixture = await HostFixture.StartAsync(serveOnly: true);
        await fixture.RequireHostableAsync();
        // The host's page in the user's browser: no native window shows it.
        var page = new HostedRegionTestPage(fixture.Host.RegionDirectory, nativeWindow: null, browser: true);
        page.Declare("check");

        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"),
            new RunicHostedApplicationOptions { Policy = new EmbedPolicy("check") });
        // No native window owns dialogs or menus, or tells which window the user works in.
        Check(application.Features.SequenceEqual(["region/1"]), $"A browser host uses [{string.Join(", ", application.Features)}].");
        var window = await opened.WaitScaledAsync(Timeout);
        Check(window.PresentationKind == RunicHostedWindowPresentationKind.Region && window.Region is { Name: "check" },
            "The window does not show in the region.");
        Check(window.Features == (RunicWindowPresentationFeatures.DocumentEdited | RunicWindowPresentationFeatures.Activity),
            $"A browser host's region window has the features {window.Features}.");
        Check(fixture.Factory.Hosts.Count == 0, "A browser host opened a native window.");

        // The frame is cross-site to the page: the guest never falls back to the page's own loopback family.
        Check(await TestTimeouts.UntilAsync(() => page.Find("check") is { Shows: "content", Src: not null }, Timeout),
            $"The page shows {page.Find("check")?.Shows}.");
        var frame = new Uri(page.Get("check").Src!);
        Check(frame.Host == "[::1]" || !Ipv6Loopback, $"The frame of a 127.0.0.1 page is served from {frame.Host}.");
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        using (var launch = await client.GetAsync(frame))
        {
            Check(launch.StatusCode == HttpStatusCode.SeeOther && launch.Headers.Location?.OriginalString.Contains("#runic-frame=", StringComparison.Ordinal) == true,
                $"The frame launch answered {(int)launch.StatusCode}.");
            Check(!launch.Headers.Contains("Set-Cookie"), "The frame launch set a cookie.");
        }

        Check(await window.Region!.AsDepartureGuard().CanDepartAsync(null!, CancellationToken.None).AsTask().WaitScaledAsync(Timeout),
            "The guest refused the departure.");
        await window.Closed.WaitScaledAsync(Timeout);
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly after its Window closed.");
    }

    [Test, NotInParallel(Order = 52)]
    public async Task ABrowserHostRefusesAWindowOfTheGuestsOwn()
    {
        await using var fixture = await HostFixture.StartAsync(serveOnly: true);
        await fixture.RequireHostableAsync();
        var policy = new AllowPolicy();
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("refused"), new RunicHostedApplicationOptions { Policy = policy });
        Check(await application.Exited.WaitScaledAsync(Timeout) == HostedGuestProgram.RefusedExitCode, "A browser host opened a window of the guest's own.");
        lock (policy.Requests)
            Check(policy.Requests.Single() is { CanOpenWindow: false, CanEmbed: true }, "The policy was not told that only a region can show the window.");
        Check(fixture.Factory.Hosts.Count == 0, "A browser host opened a native window.");
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2020 && entry.Message.Contains("WindowModeUnavailable", StringComparison.Ordinal)),
            "The policy's window-mode decision was not logged.");
        Check(fixture.Logs.Entries.Any(entry => entry.EventId == 2022 && entry.Message.Contains("OPEN hosted-window-refused", StringComparison.Ordinal)),
            "The guest's open did not fail with hosted-window-refused.");
    }

    [Test]
    public async Task AWindowModeHostTellsThePolicyThatItCanOpenAWindow()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireHostableAsync();
        var policy = new AllowPolicy();
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        lock (policy.Requests) Check(policy.Requests.Single().CanOpenWindow, "A host with native windows says it cannot open one.");
        Check(application.Features.Contains("dialogs/1") && application.Features.Contains("window-services/1"), "A native host left out its features.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, "The guest did not exit cleanly.");
    }

    // A served page's region is framed by the origin of the page that declared it, as its browser sent it with its Views
    // connection, such as a development server's on localhost, else by the page's surface origin; the page ends with its
    // Window.
    [Test]
    public async Task AServedPagesRegionIsFramedByTheOriginOfItsConnection()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions());
        var regions = BridgeDispatcher.For(transport).HostedRegions;
        foreach (var connection in new[] { "c1", "c2" })
        {
            var hello = transport.Request($$"""{"kind":"hello","protocol":4,"session":"s-{{connection}}","client":{"name":"test-client","version":"1.0.0"},"features":["hosted-regions/1"]}""", connection);
            Check(hello.StartsWith("""{"ok":true""", StringComparison.Ordinal), $"The hello was answered {hello}.");
        }
        Check(transport.Request("""{"kind":"hosted-region-declare","name":"check"}""", "c1") == """{"ok":true}""", "The declaration failed.");

        var directory = new HostedRegionDirectory();
        var closed = new TaskCompletionSource();
        var page = DesktopHostedRegionPage.FollowServed(directory, regions, new HostedMenuWindow(), new Uri("http://127.0.0.1:38200/index.html"),
            // The in-memory transport makes its keys unique per connection (c1#1).
            key => key.StartsWith("c1#", StringComparison.Ordinal) ? "http://localhost:5173" : null, closed.Task);
        Check(page is { IsBrowser: true, AllowsSameSite: false, NativeWindow: null, Embedder: "http://127.0.0.1:38200" }, "The served page is not a browser page.");
        Check(directory.EmbedderOf(page, "check") == "http://localhost:5173", $"The region is framed by {directory.EmbedderOf(page, "check")}.");
        Check(transport.Request("""{"kind":"hosted-region-declare","name":"other"}""", "c2") == """{"ok":true}""", "The declaration failed.");
        await regions.WhenRaised();
        Check(directory.EmbedderOf(page, "other") == "http://127.0.0.1:38200", $"A region of a connection without an origin is framed by {directory.EmbedderOf(page, "other")}.");

        closed.SetResult();
        Check(await TestTimeouts.UntilAsync(() => directory.EmbedderOf(page, "check") is null, Timeout), "The page outlived its Window.");
    }

    private static bool Ipv6Loopback
    {
        get
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                return true;
            }
            catch (SocketException) { return false; }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
