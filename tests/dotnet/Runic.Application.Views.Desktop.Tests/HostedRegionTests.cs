using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views.Desktop;
using TUnit.Core;
using static HostedApplicationTests;

// region/1 on the guest (host-protocol.md, Regions) between a real guest process (HostedGuestProgram "main") and
// RunicTestHost as a region host: the Window's page loads only through a frame launch address, and what the page
// reports goes to the host.
internal sealed class HostedRegionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static ProcessStartInfo Process(RunicGuestStartInfo start)
    {
        var info = new ProcessStartInfo(start.FileName) { WorkingDirectory = start.WorkingDirectory };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    [Test, NotInParallel(Order = 74)]
    public async Task AWindowInARegionLoadsThroughItsFrameAndReportsItsPageToTheHost()
    {
        await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("main")), new RunicTestHostOptions
        {
            StartTimeout = TestTimeouts.Scale(Timeout),
            Features = ["menus/1", "dialogs/1", "window-services/1", "region/1"],
            EmbedWindow = static _ => true,
        });
        if (host.Exited.IsCompleted) Skip.Test("The guest refused hosted mode (an elevated test process).");
        Check(host.Features.Contains("region/1"), $"The guest's hello listed [{string.Join(", ", host.Features)}].");
        var window = await host.WaitForWindowAsync(nameof(ConformanceMainWindow), TestTimeouts.Scale(Timeout));
        Check(window.IsRegion && !window.IsAttached, "The window is not an unattached region window.");
        using var cancel = TestTimeouts.Cancellation(Timeout);

        // The window-mode launch address admits nobody: a region window's page loads only in a frame.
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        using (var launch = await http.GetAsync(window.LaunchUrl, cancel.Token))
            Check(launch.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"The launch address was answered {(int)launch.StatusCode}.");

        var frame = await window.AttachRegionAsync("http://127.0.0.1:38200", width: 640, height: 400, sizing: "content-height",
            allowSameSite: true, cancellationToken: cancel.Token);
        Check(System.Text.RegularExpressions.Regex.IsMatch(frame.AbsoluteUri, @"^http://(\[::1\]|127\.0\.0\.1):\d+/[A-Za-z0-9_-]+/_runic_frame/[A-Za-z0-9_-]{43}$"),
            $"The frame launch address is {frame}.");
        Check(frame.Host == "[::1]" || window.FrameIsSameSite, "An IPv4 frame was not reported as same-site.");

        var (page, token) = await DesktopWireClient.ConnectFrameAsync(frame, cancel.Token);
        using (page)
        {
            Check(token.Length == 43, "The redirect carried no frame token.");
            Check(await TestTimeouts.UntilAsync(() => window.RegionPages.SequenceEqual(["connected"]), Timeout),
                $"The guest reported [{string.Join(", ", window.RegionPages)}].");

            // The page's title and content height (WebUiBridge's region reports): set-title and the preferred size.
            _ = await page.CallAsync("__webui_core_api__", cancel.Token, "region", "{\"title\":\"System\\u0000 check\",\"height\":420.2}");
            Check(await TestTimeouts.UntilAsync(() => window.Titles.SequenceEqual(["System check"]), Timeout),
                $"The guest sent the titles [{string.Join(", ", window.Titles)}].");
            Check(await TestTimeouts.UntilAsync(() => Resizes(window).SequenceEqual([(640, 421)]), Timeout),
                $"The guest sent the sizes [{string.Join(", ", Resizes(window))}].");
            // The same title again is not sent again.
            _ = await page.CallAsync("__webui_core_api__", cancel.Token, "region", "{\"title\":\"System check\"}");

            // The host moves the focus into the region: the page is told the direction.
            await window.FocusRegionAsync("backward", cancel.Token);
            var (command, payload) = await page.NextCommandAsync(cancel.Token);
            Check(command == 0xF8 && Encoding.ASCII.GetString(payload).TrimEnd('\0') == "__runicRegion\0backward",
                $"The page received command {command:X2} {Encoding.ASCII.GetString(payload)}.");

            // Detaching disconnects the page and revokes its token; the Window stays open.
            await window.DetachRegionAsync(cancellationToken: cancel.Token);
            Check(await page.ClosedAsync(cancel.Token), "The page stayed connected.");
        }
        Check(window.Titles.Count == 1, $"The guest sent the titles [{string.Join(", ", window.Titles)}].");
        Check(!window.IsClosed, "The Window closed with its region.");

        // A new attachment has a new address; the earlier one admits nobody.
        var next = await window.AttachRegionAsync("http://[::1]:38200", cancellationToken: cancel.Token);
        Check(next != frame && next.Host == "127.0.0.1" && !window.FrameIsSameSite, $"The next frame launch address is {next}.");
        using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
        using (var stale = await http.GetAsync(frame, cancel.Token))
            Check(stale.StatusCode == HttpStatusCode.Forbidden, $"The earlier address was answered {(int)stale.StatusCode}.");

        Check(await window.RequestCloseAsync(cancellationToken: cancel.Token), "The guest refused the close.");
        Check(await host.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{host.StandardError}");
    }

    private static IEnumerable<(int Width, int Height)> Resizes(RunicTestHostedWindow window) =>
        window.PresentationRequests.Where(static request => request.Kind == "resize")
            .Select(static request => (request.Message.GetProperty("width").GetInt32(), request.Message.GetProperty("height").GetInt32()));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
