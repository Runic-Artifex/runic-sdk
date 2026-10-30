using System.Text.Json;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Navigation;
using TUnit.Core;
using static DesktopChecks;


// A page leaving a navigation region on Runic Desktop (W250-044). The browser closes a page after it received the
// state that removed it, so its close request can arrive after .NET released the page's route. The host's browser is a
// wire-level client: it reads the shell's state, opens and closes pages the way the generated client does.
internal sealed class PageRemovalTests
{
    [Test, NotInParallel(Order = 28)]
    public async Task APushAndPopCycleReportsNoErrorDiagnostic()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
        var cancellation = timeout.Token;
        using var browser = await DesktopWireClient.ConnectAsync(fixture.Surface, cancellation);
        await browser.HelloAsync(cancellation);

        var home = await PageRouteAsync(browser, cancellation);
        await OpenAsync(browser, home, "home", cancellation);

        var pushed = await fixture.ViewModel.Main.PushAsync(NavigationTarget.Own(new GuardedPage(fixture.Script)));
        Require(pushed is NavigationResult<GuardedPage>.Committed, $"The push did not commit: {pushed}");
        var detail = await PageRouteAsync(browser, cancellation);
        Require(detail != home, "The pushed page reused the first page's route.");
        // The browser opens the new page and closes the one it covers.
        await OpenAsync(browser, detail, "detail", cancellation);
        var closed = await CloseAsync(browser, home, "home", cancellation);
        Require(closed == PresentationClosed, $"The covered page did not close: {closed}");
        Require(fixture.Diagnostics.IsEmpty, $"Pushing a page reported diagnostics: {Describe(fixture)}");

        // Back retires the pushed page's entry: .NET releases the page's route before the browser closes it.
        var popped = await fixture.ViewModel.Main.BackAsync();
        Require(popped is NavigationResult<GuardedPage>.Committed, $"Back did not commit: {popped}");
        Require(await PageRouteAsync(browser, cancellation) == home, "The state after Back did not present the first page.");
        // The late request is answered released, which a generated client reports as released (#246).
        var late = await CloseAsync(browser, detail, "detail", cancellation);
        Require(late == ReleasedReply, $"The late close of the retired page was answered '{late}'.");
        Require(fixture.Diagnostics.IsEmpty, $"Popping a page reported diagnostics: {Describe(fixture)}");

        // A late request re-admits nothing: the retired page's route stays released, and the page that remains is served.
        var reopened = await browser.RequestAsync(Open(detail, "detail"), cancellation);
        Require(reopened == ReleasedReply, $"A late open of the retired page was answered '{reopened}'.");
        await OpenAsync(browser, home, "home-again", cancellation);
        Require(fixture.Diagnostics.IsEmpty, $"A late request reported diagnostics: {Describe(fixture)}");
    }

    // Leaving a list page releases its rows with it, far more routes than a fixed memory of single names kept (#490).
    // A row's first request can arrive after that: it is answered released, however large the released tree.
    [Test, NotInParallel(Order = 38)]
    public async Task ALateCallIntoALargeReleasedTreeIsReportedAsReleased()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(60));
        var cancellation = timeout.Token;
        using var browser = await DesktopWireClient.ConnectAsync(fixture.Surface, cancellation);
        await browser.HelloAsync(cancellation);
        var home = await PageRouteAsync(browser, cancellation);

        var rows = Enumerable.Range(0, 300).Select(static _ => new object()).ToArray();
        var pushed = await fixture.ViewModel.Main.PushAsync(NavigationTarget.Own(new GuardedPage(fixture.Script) { Rows = rows }));
        Require(pushed is NavigationResult<GuardedPage>.Committed, $"The push did not commit: {pushed}");
        var list = await PageRouteAsync(browser, cancellation);
        Require(list != home, "The pushed page reused the first page's route.");
        using var listState = JsonDocument.Parse(await browser.RequestAsync(Snapshot(list), cancellation));
        var rowIds = listState.RootElement.GetProperty("state").GetProperty("rows").EnumerateArray().Select(row => row.GetString()!).ToArray();
        Require(rowIds.Length == rows.Length, $"The list presented {rowIds.Length} rows.");
        var first = $"content{rowIds[0]}";
        Require((await browser.RequestAsync(Snapshot(first), cancellation)).StartsWith("{\"ok\":true", StringComparison.Ordinal),
            "The first row was not served.");

        // Back releases the list page and every row before the browser builds the first row.
        var popped = await fixture.ViewModel.Main.BackAsync();
        Require(popped is NavigationResult<GuardedPage>.Committed, $"Back did not commit: {popped}");
        Require(await PageRouteAsync(browser, cancellation) == home, "The state after Back did not present the first page.");
        foreach (var request in new[]
        {
            Snapshot(first), Open(first, "row"),
            $"{{\"kind\":\"set\",\"route\":\"content{rowIds[^1]}\",\"member\":\"note\",\"value\":\"late\",\"edit\":1}}",
            Snapshot(list),
        })
        {
            var late = await browser.RequestAsync(request, cancellation);
            Require(late == ReleasedReply, $"The late request {request} into the released tree was answered '{late}'.");
        }
        Require(fixture.Diagnostics.IsEmpty, $"Late requests into a released tree reported diagnostics: {Describe(fixture)}");
    }

    // Views protocol 4 over the Desktop wire (P3): the window's one request capability answers no-hello before the
    // handshake, released for a route nothing serves, and refuses a session whose connection closed.
    [Test, NotInParallel(Order = 39)]
    public async Task ViewsRequestsReachTheWindowsDispatcher()
    {
        await using var fixture = await GuardFixture.OpenAsync<UnguardedWindow>();
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
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
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
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

    private const string ReleasedReply = "{\"ok\":false,\"error\":{\"kind\":\"released\",\"message\":\"This View was released.\"}}";
    private const string PresentationClosed = "{\"ok\":true,\"presentation\":\"ok\"}";

    private static string Snapshot(string route) => $"{{\"kind\":\"snapshot\",\"route\":\"{route}\"}}";

    private static string Open(string route, string presentation) =>
        $"{{\"kind\":\"open\",\"route\":\"{route}\",\"presentation\":\"{presentation}\"}}";

    // The route of the page the shell's state presents.
    private static async Task<string> PageRouteAsync(DesktopWireClient browser, CancellationToken cancellationToken)
    {
        using var snapshot = JsonDocument.Parse(await browser.RequestAsync(Snapshot("guardedShell"), cancellationToken));
        return "content" + snapshot.RootElement.GetProperty("state").GetProperty("page").GetProperty("id").GetString();
    }

    private static async Task OpenAsync(DesktopWireClient browser, string route, string presentation, CancellationToken cancellationToken)
    {
        var reply = await browser.RequestAsync(Open(route, presentation), cancellationToken);
        Require(reply.StartsWith("{\"ok\":true", StringComparison.Ordinal) && reply.Contains("\"presentation\":\"ok\"", StringComparison.Ordinal),
            $"{route} did not open: {reply}");
    }

    private static Task<string> CloseAsync(DesktopWireClient browser, string route, string presentation, CancellationToken cancellationToken) =>
        browser.RequestAsync($"{{\"kind\":\"close\",\"route\":\"{route}\",\"presentation\":\"{presentation}\"}}", cancellationToken);

    private static string Describe(GuardFixture fixture) => string.Join("; ", fixture.Diagnostics);
}
