using System.Reflection;
using Runic.Application.Testing;
using Runic.Platform;

namespace CheckTool.Tests;

// The check in a region, without an installer: RunicTestHost starts the built check with its `hosted` parameter, shows
// its Window in a region (region/1) and plays the installer's departures and message boxes. No browser loads the page:
// a region window's open does not wait for it.
public sealed class HostedCheckTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // The origin of the host page that would frame the check. The check lets no other origin frame its page.
    private const string Embedder = "http://127.0.0.1:47310";

    private static Task<RunicTestHost> StartAsync(Func<RunicTestDialogRequest, int, RunicTestDialogAnswer>? messageBox = null,
        Func<RunicTestHostedWindow, DesktopAppearance?>? regionAppearance = null)
    {
        var start = RunicServeOnlyApplication.CreateStartInfo(typeof(HostedCheckTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "CheckTool").Value!);
        start.ArgumentList.Add("hosted");
        var asked = 0;
        return RunicTestHost.StartGuestAsync(start, new RunicTestHostOptions
        {
            Features = ["menus/1", "dialogs/1", "window-services/1", "region/1", "region-appearance/1"],
            // The installer's policy: every window of the check shows in its step page.
            EmbedWindow = _ => true,
            MessageBox = messageBox is null ? null : (request, _) => Task.FromResult(messageBox(request, asked++)),
            // The installer's design as the region's appearance override (region-appearance/1).
            RegionAppearance = regionAppearance,
        });
    }

    [Test]
    public async Task Shows_its_window_in_a_region_from_the_other_loopback_family()
    {
        await using var host = await StartAsync();
        var window = await host.WaitForWindowAsync(nameof(CheckWindow), Wait);
        await Assert.That(window.IsRegion).IsTrue();

        var frame = await window.AttachRegionAsync(Embedder, width: 640, height: 400);

        // The one-time frame launch address: a host page on 127.0.0.1 frames the check from [::1], so none of the host
        // page's cookies reach it. Without IPv6 the check falls back to the same site, which only an embedded host allows.
        await Assert.That(frame.AbsolutePath).Contains("/_runic_frame/");
        await Assert.That(frame.IsLoopback).IsTrue();
        await Assert.That(window.IsAttached).IsTrue();
        await Assert.That(host.Guest.Name).IsEqualTo("System check");
    }

    [Test]
    public async Task Asks_before_a_departure_while_the_check_runs()
    {
        // The installer's Back or Next asks the check's close guard. The first answer keeps the check, the second stops it.
        await using var host = await StartAsync((_, asked) => RunicTestDialogAnswer.Choose(asked == 0 ? "continue" : "stop"));
        var window = await host.WaitForWindowAsync(nameof(CheckWindow), Wait);
        await window.AttachRegionAsync(Embedder);

        // The checks run for about two seconds after the Window opened.
        await Assert.That(await window.RequestCloseAsync()).IsFalse();
        var question = window.DialogRequests[0];
        await Assert.That(question.DialogKind).IsEqualTo(RunicTestDialogKind.MessageBox);
        await Assert.That(question.Text).IsEqualTo("The system check is still running. Stop it?");
        await Assert.That(question.ButtonIds).IsEquivalentTo(["stop", "continue"]);

        await Assert.That(await window.RequestCloseAsync()).IsTrue();
    }

    [Test]
    public async Task Takes_the_installers_design_as_its_appearance()
    {
        // "High contrast" from the window's open, as the installer's policy gives it, then "Dark" while attached and
        // "System" (no override) while detached. The check takes each; a check of 0.18 would not (the calls throw).
        await using var host = await StartAsync(regionAppearance: _ => new DesktopAppearance(DesktopColorScheme.Light) { Contrast = DesktopContrast.More });
        var window = await host.WaitForWindowAsync(nameof(CheckWindow), Wait);
        await window.AttachRegionAsync(Embedder);

        await host.SetRegionAppearanceAsync(window, new DesktopAppearance(DesktopColorScheme.Dark));
        await window.DetachRegionAsync();
        await host.SetRegionAppearanceAsync(window, null);
        await window.AttachRegionAsync(Embedder);
        await Assert.That(window.IsAttached).IsTrue();
    }
}
