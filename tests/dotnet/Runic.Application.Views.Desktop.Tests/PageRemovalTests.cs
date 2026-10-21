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
