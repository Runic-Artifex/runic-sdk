using System.Text.Json;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Navigation;
using TUnit.Core;
using static DesktopChecks;


// A page leaving a navigation region on Runic Desktop (W250-044). The browser unmounts a page after it received the
// snapshot that removed it, so its Unmount call can arrive after .NET released the page's routes. The host's browser
// is a wire-level client: it reads the shell snapshot, mounts and unmounts pages the way the generated client does.
internal sealed class PageRemovalTests
{
    [Test, NotInParallel(Order = 28)]
    [Skip("P4: the page reaches a View's routes through the protocol-4 dispatcher (open/close), not protocol-3 route capabilities.")]
    public async Task APushAndPopCycleReportsNoErrorDiagnostic()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellation = timeout.Token;
        using var browser = await DesktopWireClient.ConnectAsync(fixture.Surface, cancellation);

        var home = await PageRouteAsync(browser, cancellation);
        await MountAsync(browser, home, "session:home", cancellation);

        var pushed = await fixture.ViewModel.Main.PushAsync(NavigationTarget.Own(new GuardedPage(fixture.Script)));
        Require(pushed is NavigationResult<GuardedPage>.Committed, $"The push did not commit: {pushed}");
        var detail = await PageRouteAsync(browser, cancellation);
        Require(detail != home, "The pushed page reused the first page's route.");
        // The browser mounts the new page and unmounts the one it covers.
        await MountAsync(browser, detail, "session:detail", cancellation);
        Require(await browser.CallAsync($"{home}Unmount", cancellation, "session:home") == "ok", "The covered page did not unmount.");
        Require(fixture.Diagnostics.IsEmpty, $"Pushing a page reported diagnostics: {Describe(fixture)}");

        // Back retires the pushed page's entry: .NET releases the page's routes before the browser unmounts it.
        var popped = await fixture.ViewModel.Main.BackAsync();
        Require(popped is NavigationResult<GuardedPage>.Committed, $"Back did not commit: {popped}");
        Require(await PageRouteAsync(browser, cancellation) == home, "The snapshot after Back did not present the first page.");
        // The late call is answered with the released envelope, which a generated client reports as released (#246).
        var late = await browser.CallAsync($"{detail}Unmount", cancellation, "session:detail");
        Require(late == ReleasedReply, $"The late unmount of the retired page was answered '{late}'.");
        Require(fixture.Diagnostics.IsEmpty, $"Popping a page reported diagnostics: {Describe(fixture)}");

        // A late call re-admits nothing: the retired page's routes stay released, and the page that remains is served.
        Require(await browser.CallAsync($"{detail}Mount", cancellation, "session:detail") != "ok",
            "A late mount re-admitted the retired page.");
        await MountAsync(browser, home, "session:home-again", cancellation);
        Require(fixture.Diagnostics.IsEmpty, $"A late call reported diagnostics: {Describe(fixture)}");

        // A capability that was never admitted is still reported, names the element, and replies empty: the client
        // reports that reply as an invalid response, not as a released View.
        var unadmittedReply = await browser.CallAsync("contentNeverAdmittedMount", cancellation, "session:x");
        Require(unadmittedReply == "", $"A capability that was never admitted was answered '{unadmittedReply}'.");
        var reported = fixture.Diagnostics.ToArray();
        Require(reported.Length == 1, $"Expected one diagnostic for an unadmitted capability, found: {Describe(fixture)}");
        var diagnostic = reported[0];
        Require(diagnostic is { Category: DesktopErrorCategory.CapabilityDenied, Code: "capability-not-admitted", Severity: DesktopDiagnosticSeverity.Error }
            && diagnostic.Message.Contains("'contentNeverAdmittedMount'", StringComparison.Ordinal),
            $"A capability that was never admitted was not reported with its name: {diagnostic}");
    }

    // Leaving a list page releases its rows with it, far more routes than a fixed memory of single names kept (#490).
    // A row's first Snapshot call can arrive after that: it is late, not unadmitted, however large the released tree.
    [Test, NotInParallel(Order = 38)]
    [Skip("P4: the page reaches a View's routes through the protocol-4 dispatcher (open/close), not protocol-3 route capabilities.")]
    public async Task ALateCallIntoALargeReleasedTreeIsReportedAsReleased()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellation = timeout.Token;
        using var browser = await DesktopWireClient.ConnectAsync(fixture.Surface, cancellation);
        var home = await PageRouteAsync(browser, cancellation);

        // 300 rows with seven routes each, and the page's own three.
        var rows = Enumerable.Range(0, 300).Select(static _ => new object()).ToArray();
        var pushed = await fixture.ViewModel.Main.PushAsync(NavigationTarget.Own(new GuardedPage(fixture.Script) { Rows = rows }));
        Require(pushed is NavigationResult<GuardedPage>.Committed, $"The push did not commit: {pushed}");
        var list = await PageRouteAsync(browser, cancellation);
        Require(list != home, "The pushed page reused the first page's route.");
        var rowIds = JsonSerializer.Deserialize<string[]>(await browser.CallAsync($"{list}Snapshot", cancellation))!;
        Require(rowIds.Length == rows.Length, $"The list presented {rowIds.Length} rows.");
        var first = $"content{rowIds[0]}";
        Require(await browser.CallAsync($"{first}Snapshot", cancellation) == "ok", "The first row was not served.");

        // Back releases the list page and every row, more than 2 000 routes, before the browser builds the first row.
        var popped = await fixture.ViewModel.Main.BackAsync();
        Require(popped is NavigationResult<GuardedPage>.Committed, $"Back did not commit: {popped}");
        Require(await PageRouteAsync(browser, cancellation) == home, "The snapshot after Back did not present the first page.");
        foreach (var route in new[] { $"{first}Snapshot", $"{first}Mount", $"content{rowIds[^1]}SetNote", $"{list}Snapshot" })
        {
            var late = await browser.CallAsync(route, cancellation, "session:row");
            Require(late == ReleasedReply, $"The late call {route} into the released tree was answered '{late}'.");
        }
        Require(fixture.Diagnostics.IsEmpty, $"Late calls into a released tree reported diagnostics: {Describe(fixture)}");
    }

    // Views protocol 4 over the Desktop wire (P3): the window's one request capability answers no-hello before the
    // handshake, released for a route nothing serves, and refuses a session whose connection closed.
    [Test, NotInParallel(Order = 39)]
    public async Task ViewsRequestsReachTheWindowsDispatcher()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellation = timeout.Token;
        const string Hello = "{\"kind\":\"hello\",\"protocol\":4,\"session\":\"session-1\",\"client\":{\"name\":\"wire\",\"version\":\"0\"}}";
        using (var browser = await DesktopWireClient.ConnectAsync(fixture.Surface, cancellation))
        {
            var early = await browser.RequestAsync("{\"kind\":\"snapshot\",\"route\":\"guardedShell\"}", cancellation);
            Require(Reason(early) == "no-hello", $"A request before the hello was answered {early}.");
            var hello = JsonDocument.Parse(await browser.RequestAsync(Hello, cancellation)).RootElement;
            Require(hello.GetProperty("ok").GetBoolean() && hello.GetProperty("protocol").GetInt32() == 4
                && hello.GetProperty("implementation").GetProperty("name").GetString() == "Runic.Application.Views",
                $"The hello was answered {hello}.");
            var released = await browser.RequestAsync("{\"kind\":\"snapshot\",\"route\":\"content0123456789abcdef0123456789abcdef\"}", cancellation);
            Require(released == "{\"ok\":false,\"error\":{\"kind\":\"released\",\"message\":\"This View was released.\"}}",
                $"A route nothing serves was answered {released}.");
        }
        // The Desktop connection closed; its session cannot open a new connection. The dispatcher learns of the close
        // from the surface's connection event, which can trail a new connection: a hello that still got the session
        // closes its connection too, which releases the session as well.
        string again = "";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await WaitUntilDisconnectedAsync(fixture.Surface, cancellation);
            using var reconnected = await DesktopWireClient.ConnectAsync(fixture.Surface, cancellation);
            again = await reconnected.RequestAsync(Hello, cancellation);
            if (!again.StartsWith("{\"ok\":true", StringComparison.Ordinal)) break;
        }
        Require(JsonDocument.Parse(again).RootElement.GetProperty("error").GetProperty("kind").GetString() == "released",
            $"The session of a closed connection was answered {again}.");
    }

    private static async Task WaitUntilDisconnectedAsync(DesktopSurface surface, CancellationToken cancellationToken)
    {
        while (surface.ConnectionCount > 0) await Task.Delay(10, cancellationToken);
        // The surface counts the close before its handlers run.
        await Task.Delay(50, cancellationToken);
    }

    // A capability that was never admitted is reported, names the element, and replies empty: the client reports that
    // reply as an invalid response, not as a released View.
    [Test, NotInParallel(Order = 40)]
    public async Task AnUnadmittedCapabilityIsReportedAndRepliesEmpty()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var browser = await DesktopWireClient.ConnectAsync(fixture.Surface, timeout.Token);
        var unadmittedReply = await browser.CallAsync("contentNeverAdmittedMount", timeout.Token, "session:x");
        Require(unadmittedReply == "", $"A capability that was never admitted was answered '{unadmittedReply}'.");
        var reported = fixture.Diagnostics.ToArray();
        Require(reported.Length == 1, $"Expected one diagnostic for an unadmitted capability, found: {Describe(fixture)}");
        Require(reported[0] is { Category: DesktopErrorCategory.CapabilityDenied, Code: "capability-not-admitted", Severity: DesktopDiagnosticSeverity.Error }
            && reported[0].Message.Contains("'contentNeverAdmittedMount'", StringComparison.Ordinal),
            $"A capability that was never admitted was not reported with its name: {reported[0]}");
    }

    private static string? Reason(string reply) =>
        JsonDocument.Parse(reply).RootElement is { } root && root.TryGetProperty("error", out var error)
            && error.TryGetProperty("reason", out var reason) ? reason.GetString() : null;

    private const string ReleasedReply =
        "{\"ok\":false,\"state\":null,\"error\":{\"kind\":\"released\",\"message\":\"This View was released.\"}}";

    // The route of the page the shell's snapshot presents.
    private static async Task<string> PageRouteAsync(DesktopWireClient browser, CancellationToken cancellationToken)
    {
        using var snapshot = JsonDocument.Parse(await browser.CallAsync("guardedShellSnapshot", cancellationToken));
        return "content" + snapshot.RootElement.GetProperty("state").GetProperty("page").GetString();
    }

    private static async Task MountAsync(DesktopWireClient browser, string route, string token, CancellationToken cancellationToken) =>
        Require(await browser.CallAsync($"{route}Mount", cancellationToken, token) == "ok", $"{route} did not mount.");

    private static string Describe(GuardFixture fixture) => string.Join("; ", fixture.Diagnostics);
}
