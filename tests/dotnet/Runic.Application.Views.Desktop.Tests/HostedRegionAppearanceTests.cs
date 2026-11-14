using System.Diagnostics;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;
using static HostedApplicationTests;
using DisplayNameAttribute = TUnit.Core.DisplayNameAttribute;

// region-appearance/1 (host-protocol.md, Appearance in a region): the guest merges a region window's override with the
// host's desktop appearance for that window's pages only, and Runic's host sends the override from
// RunicHostedRegionOptions.Appearance and RunicHostedRegion.SetAppearanceAsync, and its colour scheme to the host page.
internal sealed class HostedRegionAppearanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly DesktopAccentColor Blue = new(0.2, 0.4, 0.8);
    private static readonly DesktopAccentColor Pink = new(0.8, 0.2, 0.4);
    private static readonly DesktopAccentColor Black = new(0, 0, 0);
    private static readonly DesktopAccentColor White = new(1, 1, 1);
    private static readonly DesktopSystemColors DesktopPalette = new(Black, White, new(1, 1, 0), new(0, 1, 0), new(0, 1, 1), Black, Black, White);
    private static readonly DesktopSystemColors OverridePalette = new(White, Black, Blue, Pink, Blue, White, White, Black);

    // The test host's desktop appearance.
    private static readonly DesktopAppearance Desktop = new(DesktopColorScheme.Light, Blue, ReducedMotion: false)
    {
        Contrast = DesktopContrast.NoPreference,
        TextScale = 1.25,
    };

    // ----- The merge and the override's members (Protocol/HostServices.cs, shared by the guest and RunicTestHost)

    [Test]
    public void TheOverrideReplacesTheDesktopsMembersAndDecidesOnlyThoseItSets()
    {
        // No override: the desktop's appearance, nothing decided.
        Check(HostServices.Merge(Desktop, null) == (Desktop, HostDecidedAppearance.None), "No override changed the appearance.");

        var (merged, decided) = HostServices.Merge(Desktop, new DesktopAppearance(DesktopColorScheme.Dark, Pink) { Contrast = DesktopContrast.More, TextScale = 2 });
        Check(merged == new DesktopAppearance(DesktopColorScheme.Dark, Pink, ReducedMotion: false) { Contrast = DesktopContrast.More, TextScale = 2 },
            $"The merge is {merged}.");
        Check(decided == (HostDecidedAppearance.ColorScheme | HostDecidedAppearance.Contrast),
            $"The decided members are {decided}: the text scale and accent colour come from the host alone in any case.");

        // NoPreference and null keep the desktop's.
        (merged, decided) = HostServices.Merge(Desktop, new DesktopAppearance(DesktopColorScheme.NoPreference, ReducedMotion: true));
        Check(merged == Desktop with { ReducedMotion = true } && decided == HostDecidedAppearance.ReducedMotion, $"The merge is {merged}, {decided}.");

        // An unknown desktop: the override's members alone.
        (merged, decided) = HostServices.Merge(null, new DesktopAppearance(DesktopColorScheme.Dark));
        Check(merged == new DesktopAppearance(DesktopColorScheme.Dark) && decided == HostDecidedAppearance.ColorScheme, $"The merge is {merged}.");
    }

    [Test]
    [Arguments(null, false, false, null, "the override's contrast without forced colours on a plain desktop: no palette")]
    [Arguments(DesktopContrast.ForcedColors, false, false, null, "forced colours without a palette on a desktop that does not force them: none")]
    [Arguments(DesktopContrast.ForcedColors, true, false, "override", "forced colours with a palette: the override's")]
    [Arguments(DesktopContrast.ForcedColors, false, true, "desktop", "forced colours without a palette on a forced desktop: the desktop's")]
    [Arguments(DesktopContrast.ForcedColors, true, true, "override", "forced colours with a palette on a forced desktop: the override's")]
    [Arguments(DesktopContrast.More, true, true, null, "more contrast on a forced desktop: none")]
    [Arguments(null, false, true, "desktop", "no contrast override on a forced desktop: the desktop's")]
    [DisplayName("TheSystemColorsFollowTheEffectiveContrast(contrast: $contrast, overridePalette: $overridePalette, forcedDesktop: $forcedDesktop)")]
    public void TheSystemColorsFollowTheEffectiveContrast(DesktopContrast? contrast, bool overridePalette, bool forcedDesktop, string? expected, string rule)
    {
        var desktop = forcedDesktop ? Desktop with { Contrast = DesktopContrast.ForcedColors, SystemColors = DesktopPalette } : Desktop;
        var appearanceOverride = new DesktopAppearance(DesktopColorScheme.Dark) { Contrast = contrast, SystemColors = overridePalette ? OverridePalette : null };
        var (merged, _) = HostServices.Merge(desktop, appearanceOverride);
        var palette = expected switch { "override" => OverridePalette, "desktop" => DesktopPalette, _ => null };
        Check(Equals(merged?.SystemColors, palette), $"{rule}: the palette is {merged?.SystemColors}.");
        Check(merged?.Contrast == (contrast ?? desktop.Contrast), $"{rule}: the contrast is {merged?.Contrast}.");
    }

    [Test]
    public void AnOverrideMemberOutOfRangeReadsAsTheDesktops()
    {
        var read = HostServices.ReadOverride(JsonNode.Parse("""{"colorScheme":"dark","contrast":"brighter","reducedMotion":"yes","textScale":9,"accentColor":"red","systemColors":null}"""));
        Check(read == new DesktopAppearance(DesktopColorScheme.Dark), $"The override read as {read}.");
        Check(HostServices.ReadOverride(JsonNode.Parse("""{"textScale":0.5}"""))?.TextScale == 0.5 &&
            HostServices.ReadOverride(JsonNode.Parse("""{"textScale":4}"""))?.TextScale == 4 &&
            HostServices.ReadOverride(JsonNode.Parse("""{"textScale":0.49}""")) is null &&
            HostServices.ReadOverride(JsonNode.Parse("""{"textScale":4.01}""")) is null, "The text scale is not bounded from 0.5 to 4.");
        Check(HostServices.ReadOverride(JsonNode.Parse("""{"colorScheme":null,"contrast":null}""")) is null, "An override of null members is one.");
        Check(!HostServices.CheckAppearance(JsonValue.Create(3)) && HostServices.CheckAppearance(null), "An appearance of 3 is valid.");
        Check(HostServices.WriteOverride(null) is null && HostServices.WriteOverride(new DesktopAppearance()) is null, "No override is not written null.");
        Check(HostServices.WriteOverride(new DesktopAppearance(DesktopColorScheme.Dark) { Contrast = DesktopContrast.More })!.ToJsonString() ==
            """{"colorScheme":"dark","contrast":"more","reducedMotion":null,"textScale":null,"accentColor":null,"systemColors":null}""",
            "The override is not written with every member.");
    }

    [Test]
    public void RunicHostedAppearanceChecksItsMembers()
    {
        Check(new RunicHostedAppearance { ColorScheme = DesktopColorScheme.NoPreference }.ColorScheme is null, "NoPreference does not read as null.");
        Check(new RunicHostedAppearance { ColorScheme = DesktopColorScheme.NoPreference } == RunicHostedAppearance.None, "A System scheme is an override.");
        Check(RunicHostedAppearance.None.ToDesktopAppearance() is null, "None is an override.");
        Check(new RunicHostedAppearance { TextScale = 0.5 }.TextScale == 0.5 && new RunicHostedAppearance { TextScale = 4 }.TextScale == 4,
            "The bounds of the text scale are refused.");
        Check(Throws<ArgumentOutOfRangeException>(() => _ = new RunicHostedAppearance { TextScale = 0.4 }) &&
            Throws<ArgumentOutOfRangeException>(() => _ = new RunicHostedAppearance { TextScale = 4.5 }) &&
            Throws<ArgumentOutOfRangeException>(() => _ = new RunicHostedAppearance { TextScale = double.NaN }), "A text scale out of range is accepted.");
        // A palette without forced colours is not sent.
        var palette = new RunicHostedAppearance { Contrast = DesktopContrast.More, SystemColors = DesktopPalette }.ToDesktopAppearance();
        Check(palette is { SystemColors: null, Contrast: DesktopContrast.More }, $"The override is {palette}.");
    }

    // ----- The guest, with RunicTestHost as a region host

    // A real guest process (HostedGuestProgram "main") whose Window RunicTestHost shows in a region with an override: the
    // frame's page receives the merged appearance with override first, then each change of the override and of the
    // host's appearance; an override set while the window is detached reaches the next page.
    [Test]
    public async Task ARegionWindowsPagesReceiveTheHostsAppearanceMergedWithItsOverride()
    {
        await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("main")), new RunicTestHostOptions
        {
            StartTimeout = TestTimeouts.Scale(Timeout),
            Features = ["menus/1", "dialogs/1", "window-services/1", "region/1", "region-appearance/1"],
            EmbedWindow = static _ => true,
            Appearance = Desktop,
            RegionAppearance = static _ => new DesktopAppearance(DesktopColorScheme.Dark) { Contrast = DesktopContrast.More },
        });
        if (host.Exited.IsCompleted) Skip.Test("The guest refused hosted mode (an elevated test process).");
        Check(host.Features.Contains("region-appearance/1"), $"The guest's hello listed [{string.Join(", ", host.Features)}].");
        var window = await host.WaitForWindowAsync(nameof(ConformanceMainWindow), TestTimeouts.Scale(Timeout));
        using var cancel = TestTimeouts.Cancellation(Timeout);

        var frame = await window.AttachRegionAsync("http://127.0.0.1:38200", cancellationToken: cancel.Token);
        var (page, _) = await DesktopWireClient.ConnectFrameAsync(frame, cancel.Token);
        using (page)
        {
            await page.HelloAsync(cancel.Token, "appearance/1");
            await ExpectAppearanceAsync(page,
                """{"kind":"appearance","colorScheme":"dark","contrast":"more","reducedMotion":false,"textScale":1.25,"accentColor":"#3366cc","systemColors":null,"override":["colorScheme","contrast"]}""",
                cancel.Token);

            // The new override replaces the old one as a whole: contrast is the desktop's again.
            await host.SetRegionAppearanceAsync(window, new DesktopAppearance(DesktopColorScheme.Dark, ReducedMotion: true), cancel.Token);
            await ExpectAppearanceAsync(page,
                """{"kind":"appearance","colorScheme":"dark","contrast":"no-preference","reducedMotion":true,"textScale":1.25,"accentColor":"#3366cc","systemColors":null,"override":["colorScheme","reducedMotion"]}""",
                cancel.Token);

            // A change of the host's appearance keeps the override.
            await host.SetAppearanceAsync(Desktop with { AccentColor = Pink }, cancel.Token);
            await ExpectAppearanceAsync(page,
                """{"kind":"appearance","colorScheme":"dark","contrast":"no-preference","reducedMotion":true,"textScale":1.25,"accentColor":"#cc3366","systemColors":null,"override":["colorScheme","reducedMotion"]}""",
                cancel.Token);

            await window.DetachRegionAsync(cancellationToken: cancel.Token);
            Check(await page.ClosedAsync(cancel.Token), "The page stayed connected.");
        }

        // Removed while detached: the next page receives the host's appearance alone.
        await host.SetRegionAppearanceAsync(window, null, cancel.Token);
        var next = await window.AttachRegionAsync("http://127.0.0.1:38200", cancellationToken: cancel.Token);
        var (second, _) = await DesktopWireClient.ConnectFrameAsync(next, cancel.Token);
        using (second)
        {
            await second.HelloAsync(cancel.Token, "appearance/1");
            await ExpectAppearanceAsync(second,
                """{"kind":"appearance","colorScheme":"light","contrast":"no-preference","reducedMotion":false,"textScale":1.25,"accentColor":"#cc3366","systemColors":null}""",
                cancel.Token);
        }
        Check(await window.RequestCloseAsync(cancellationToken: cancel.Token), "The guest refused the close.");
        Check(await host.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{host.StandardError}");
    }

    // RunicTestHost sends no override where the feature is not in use, and refuses one for a window shown in a window.
    [Test]
    public async Task RunicTestHostSendsAnOverrideOnlyWithTheFeatureInUse()
    {
        await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("main")), new RunicTestHostOptions
        {
            StartTimeout = TestTimeouts.Scale(Timeout),
            Features = ["menus/1", "dialogs/1", "window-services/1", "region/1"],
            EmbedWindow = static _ => true,
            Appearance = Desktop,
            RegionAppearance = static _ => new DesktopAppearance(DesktopColorScheme.Dark),
        });
        if (host.Exited.IsCompleted) Skip.Test("The guest refused hosted mode (an elevated test process).");
        var window = await host.WaitForWindowAsync(nameof(ConformanceMainWindow), TestTimeouts.Scale(Timeout));
        using var cancel = TestTimeouts.Cancellation(Timeout);
        Check(Throws<InvalidOperationException>(() => _ = host.SetRegionAppearanceAsync(window, new DesktopAppearance(DesktopColorScheme.Dark))),
            "An override was sent without region-appearance/1 in use.");
        var frame = await window.AttachRegionAsync("http://127.0.0.1:38200", cancellationToken: cancel.Token);
        var (page, _) = await DesktopWireClient.ConnectFrameAsync(frame, cancel.Token);
        using (page)
        {
            await page.HelloAsync(cancel.Token, "appearance/1");
            await ExpectAppearanceAsync(page,
                """{"kind":"appearance","colorScheme":"light","contrast":"no-preference","reducedMotion":false,"textScale":1.25,"accentColor":"#3366cc","systemColors":null}""",
                cancel.Token);
        }
        Check(await window.RequestCloseAsync(cancellationToken: cancel.Token), "The guest refused the close.");
        Check(await host.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{host.StandardError}");
    }

    // ----- Runic's host

    private sealed class AppearancePolicy(string region, RunicHostedAppearance? appearance) : IRunicHostedWindowPolicy
    {
        public RunicHostedWindowRequest? Request { get; private set; }

        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return ValueTask.FromResult(RunicHostedWindowDecision.Embed(region, new RunicHostedRegionOptions { Appearance = appearance }));
        }
    }

    // Runic's host and guest: the policy's override reaches the guest's page before its first content and the host page's
    // frame as its colour scheme; SetAppearanceAsync changes both, coalesces, works detached, survives a move, and
    // answers false once the region ended.
    [Test]
    public async Task TheHostSendsTheRegionsOverrideToTheGuestAndItsColourSchemeToThePage()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireHostableAsync();
        await using var surface = await fixture.Desktop.CreateSurfaceAsync();
        var hostWindow = await surface.OpenWindowAsync(RunicDesktopHost.DefaultWindowOptions);
        var page = new HostedRegionTestPage(fixture.Host.RegionDirectory, hostWindow, window: new HostedMenuWindow());
        page.Declare("check");

        var initial = new RunicHostedAppearance { ColorScheme = DesktopColorScheme.Dark, Contrast = DesktopContrast.More };
        var policy = new AppearancePolicy("check", initial);
        var opened = fixture.Next(opened: true);
        await using var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("main"),
            new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        var region = window.Region!;
        Check(policy.Request is { CanOverrideAppearance: true } && region.CanSetAppearance && region.Appearance == initial,
            "The guest's region window cannot take the override.");
        using var cancel = TestTimeouts.Cancellation(Timeout);

        var frame = await WaitForSourceAsync(page, "check", null);
        Check(page.Get("check").ColorScheme == "dark", $"The page's frame shows the scheme {page.Get("check").ColorScheme}.");
        var (guestPage, _) = await DesktopWireClient.ConnectFrameAsync(new Uri(frame), cancel.Token);
        using (guestPage)
        {
            await guestPage.HelloAsync(cancel.Token, "appearance/1");
            await ExpectAppearanceAsync(guestPage,
                """{"kind":"appearance","colorScheme":"dark","contrast":"more","reducedMotion":false,"textScale":1,"accentColor":null,"systemColors":null,"override":["colorScheme","contrast"]}""",
                cancel.Token);

            var light = new RunicHostedAppearance { ColorScheme = DesktopColorScheme.Light };
            Check(await region.SetAppearanceAsync(light, cancel.Token), "SetAppearanceAsync returned false.");
            Check(page.Get("check").SchemeUpdates.SequenceEqual(["light"]), $"The page's scheme updates are [{string.Join(", ", page.Get("check").SchemeUpdates)}].");
            await ExpectAppearanceAsync(guestPage,
                """{"kind":"appearance","colorScheme":"light","contrast":"no-preference","reducedMotion":false,"textScale":1,"accentColor":null,"systemColors":null,"override":["colorScheme"]}""",
                cancel.Token);

            // The same override again changes nothing.
            Check(await region.SetAppearanceAsync(light with { }, cancel.Token), "An unchanged override returned false.");
            Check(page.Get("check").SchemeUpdates.Count == 1, "An unchanged override updated the page.");

            // Overrides the guest has not read yet are coalesced: the latest reaches the page, and the frame's scheme follows.
            var latest = new RunicHostedAppearance { ColorScheme = DesktopColorScheme.Dark, ReducedMotion = true };
            var results = await Task.WhenAll(
                region.SetAppearanceAsync(RunicHostedAppearance.None, cancel.Token),
                region.SetAppearanceAsync(new RunicHostedAppearance { Contrast = DesktopContrast.ForcedColors }, cancel.Token),
                region.SetAppearanceAsync(latest, cancel.Token));
            Check(results.All(static result => result), "A burst of overrides returned false.");
            Check(region.Appearance == latest && page.Get("check").ColorScheme == "dark", $"The page's frame shows {page.Get("check").ColorScheme}.");
            await ExpectAppearanceAsync(guestPage,
                """{"kind":"appearance","colorScheme":"dark","contrast":"no-preference","reducedMotion":true,"textScale":1,"accentColor":null,"systemColors":null,"override":["colorScheme","reducedMotion"]}""",
                cancel.Token, skipOthers: true);
        }

        // A move keeps the override: the other region's frame shows its scheme, and the guest's next page receives it.
        var target = new HostedMenuWindow();
        var other = new HostedRegionTestPage(fixture.Host.RegionDirectory, hostWindow, window: target);
        other.Declare("other");
        await region.MoveToAsync(target, "other", cancel.Token);
        var moved = await WaitForSourceAsync(other, "other", null);
        Check(other.Get("other").ColorScheme == "dark", $"The moved region's frame shows {other.Get("other").ColorScheme}.");
        var (movedPage, _) = await DesktopWireClient.ConnectFrameAsync(new Uri(moved), cancel.Token);
        using (movedPage)
        {
            await movedPage.HelloAsync(cancel.Token, "appearance/1");
            await ExpectAppearanceAsync(movedPage,
                """{"kind":"appearance","colorScheme":"dark","contrast":"no-preference","reducedMotion":true,"textScale":1,"accentColor":null,"systemColors":null,"override":["colorScheme","reducedMotion"]}""",
                cancel.Token);
        }

        // Detached: the override is accepted and recorded, and the page that shows nothing is not updated.
        await region.DetachAsync(cancel.Token);
        var updates = other.Get("other").SchemeUpdates.Count;
        Check(await region.SetAppearanceAsync(null, cancel.Token) && region.Appearance is null, "A detached region refused the override.");
        Check(other.Get("other").SchemeUpdates.Count == updates, "A detached region's page was updated.");

        // Ended: false, without an exception, and nothing recorded.
        _ = await region.RequestCloseAsync(force: true, cancel.Token);
        await window.Closed.WaitScaledAsync(Timeout);
        Check(!await region.SetAppearanceAsync(initial, cancel.Token) && region.Appearance is null, "An ended region accepted an override.");
        Check(!fixture.Logs.Entries.Any(static entry => entry.EventId == 2030), "The host warned for a guest that applies overrides.");
    }

    private static async Task ExpectAppearanceAsync(DesktopWireClient page, string expected, CancellationToken cancellationToken, bool skipOthers = false)
    {
        while (true)
        {
            var push = await page.NextPushAsync(cancellationToken);
            if (JsonNode.Parse(push)?["kind"]?.GetValue<string>() != "appearance") continue;
            if (push == expected) return;
            if (!skipOthers) throw new InvalidOperationException($"The page received {push}, not {expected}.");
        }
    }

    // The frame launch address the page shows, once it is another than previous.
    private static async Task<string> WaitForSourceAsync(HostedRegionTestPage page, string name, string? previous)
    {
        string? Source() => page.Find(name) is { Shows: "content", Src: { } src } && src != previous ? src : null;
        Check(await TestTimeouts.UntilAsync(() => Source() is not null, Timeout), $"The page shows {page.Find(name)?.Shows} {page.Find(name)?.Src}.");
        return Source()!;
    }

    private static ProcessStartInfo Process(RunicGuestStartInfo start)
    {
        var info = new ProcessStartInfo(start.FileName) { WorkingDirectory = start.WorkingDirectory };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }
}
