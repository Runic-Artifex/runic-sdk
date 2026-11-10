using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Runic.Application.Testing;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;
using static HostedApplicationTests;

// The host's appearance and locale and window-services/1 (host-protocol.md, Appearance and locale, Window services)
// between real guest processes (HostedGuestProgram "window-services" and "main") and two hosts: RunicTestHost, as a
// guest application's own tests use it, and IRunicHostedApplications with headless windows, FakeMonitors and a
// RunicTestPlatform as the windows' clipboard and launcher.
internal sealed class HostedWindowServicesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static ProcessStartInfo Process(RunicGuestStartInfo start)
    {
        var info = new ProcessStartInfo(start.FileName) { WorkingDirectory = start.WorkingDirectory };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    // A line of the guest's standard error: as RunicTestHost captures it, or as the host logs it ("<file> (standard error): <line>").
    private static bool HasLine(string output, string line) =>
        output.Split('\n').Select(static text => text.TrimEnd('\r')).Any(text => text == line || text.EndsWith("(standard error): " + line, StringComparison.Ordinal));

    private static async Task WaitForAsync(Func<string> output, string line, string what)
    {
        if (!await TestTimeouts.UntilAsync(() => HasLine(output(), line), Timeout))
            throw new InvalidOperationException($"{what}: the guest did not report '{line}'.\nIts output:\n{output()}");
    }

    private static string TempFile(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runic-hosted-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "dropped");
        return path;
    }

    private static readonly DesktopAppearance DarkMoreContrast =
        new(DesktopColorScheme.Dark, new DesktopAccentColor(0.2, 0.4, 0.8), ReducedMotion: true) { Contrast = DesktopContrast.More, TextScale = 1.25 };

    private static readonly DesktopAppearance ForcedColors = new(DesktopColorScheme.Dark)
    {
        Contrast = DesktopContrast.ForcedColors,
        SystemColors = new(new(0, 0, 0), new(1, 1, 1), new(1, 1, 0), new(0.25, 1, 0), new(0.1, 0.9, 1), new(0, 0, 0), new(0, 0, 0), new(1, 1, 1)),
    };

    // A guest application's own test: RunicTestHost starts it with a locale and an appearance, changes them, drops a file
    // onto its window and answers its clipboard, monitors and launcher requests.
    [Test, NotInParallel(Order = 70)]
    public async Task UnderTheTestHostAGuestFollowsTheHostAndUsesItsWindowServices()
    {
        var file = TempFile("notes.txt");
        await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("window-services")), new RunicTestHostOptions
        {
            StartTimeout = TestTimeouts.Scale(Timeout),
            Locale = "de-CH",
            Languages = ["de-CH", "en"],
            Appearance = DarkMoreContrast,
        });
        if (host.Exited.IsCompleted) Skip.Test("The guest refused hosted mode (an elevated test process).");
        var window = await host.WaitForWindowAsync(nameof(HostedDropWindow), TestTimeouts.Scale(Timeout));
        string Errors() => host.StandardError;
        // hello's locale and appearance: the application and its first Window start in them.
        await WaitForAsync(Errors, "LOCALE de-CH", "hello's locale");
        await WaitForAsync(Errors, "LANGUAGES de-CH,en", "hello's languages");
        await WaitForAsync(Errors, "APPEARANCE Dark More", "hello's appearance");
        Check(window.Locale == "de-CH" && window.AcceptsFileDrops, $"window-open named locale {window.Locale}, fileDrops {window.AcceptsFileDrops}.");

        // The host's locale changes to Arabic: the application follows, and the window's direction with it.
        await host.SetLocaleAsync("ar", ["ar", "en"]);
        await WaitForAsync(Errors, "LOCALE ar", "the locale push");
        Check(await TestTimeouts.UntilAsync(() => window.PresentationRequests.Any(static request => request.Kind == "set-layout-direction" &&
            request.Message.GetProperty("direction").GetString() == "right-to-left"), Timeout), "The window's direction did not follow the locale: " + host.StandardError + string.Join(";", window.PresentationRequests.Select(r => r.Kind)));
        await host.SetAppearanceAsync(ForcedColors);
        await WaitForAsync(Errors, "APPEARANCE Dark ForcedColors", "the appearance push");

        // A drop onto the host's window reaches the root ViewModel, which starts the guest's script.
        host.Clipboard.SetText("from host");
        await window.DropFilesAsync([file], "folder-1");
        await WaitForAsync(Errors, $"DROP {file} folder-1", "the drop");
        await WaitForAsync(Errors, "PHASE 1", "the window services");
        foreach (var line in (string[])["READ from host", "WRITE success", "FORMATS Text, Html", "CLIPBOARD-WATCH unavailable BackendUnavailable",
            "MONITORS test-monitor", "LAUNCH-URI success", "LAUNCH-SETTINGS success", "LAUNCH-GIVEN success", "LAUNCH-OTHER success",
            "DRAG unavailable OwnerUnavailable", "ANNOUNCE success"])
            await WaitForAsync(Errors, line, "the window services");
        Check(host.Clipboard.Content is { Text: "from guest", Html: "<b>from guest</b>" }, "The guest's write did not reach the host's clipboard.");
        var launches = window.LaunchRequests;
        Check(launches.Count == 5 && launches[0].Uri == new Uri("https://runic.dev/docs") && launches[2].Path == file &&
            launches[2].Operation == DesktopFileOperation.Reveal, $"The host recorded {string.Join("; ", launches)}.");
        Check(window.PresentationRequests.Any(static request => request.Kind == "announce" && request.Message.GetProperty("text").GetString() == "Ready"),
            "The announcement did not reach the host's window.");

        // monitors-changed: the guest's monitor watch lists the monitors again.
        await host.SetMonitorsAsync([new DesktopMonitor("left", "Left") { Bounds = new(0, 0, 800, 600), WorkArea = new(0, 0, 800, 600), IsPrimary = true },
            new DesktopMonitor("right", "Right") { Bounds = new(800, 0, 800, 600), WorkArea = new(800, 0, 800, 600) }]);
        await WaitForAsync(Errors, "WATCH left,right", "monitors-changed");

        // The window is no longer active: the clipboard is not read for it. Then the application sets a locale of its own,
        // which the host's next locale does not replace.
        await window.SetActivityAsync(new Runic.Application.Views.RunicWindowActivity(false, false, true));
        await host.SetLocaleAsync("fr");
        await WaitForAsync(Errors, "PHASE 2", "the inactive window");
        await WaitForAsync(Errors, "READ-INACTIVE failed PermissionDenied", "the inactive window's read");
        // The test host does not refuse writes from inactive windows, or replaced files.
        await WaitForAsync(Errors, "WRITE-INACTIVE success", "the inactive window's write");
        await WaitForAsync(Errors, "OWN en", "the application's own locale");
        await host.SetLocaleAsync("de");
        await Task.Delay(TestTimeouts.Scale(TimeSpan.FromMilliseconds(500)));
        Check(!HasLine(Errors(), "LOCALE de"), "The host's locale replaced the application's own.");

        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await host.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{Errors()}");
    }

    // A dark, high-contrast appearance that the host pushes reaches the guest's page (appearance/1), as the page's
    // appearance store receives it, instead of the guest process's own desktop settings; forced colours follow.
    [Test, NotInParallel(Order = 71)]
    public async Task TheHostsHighContrastAppearanceReachesTheGuestsPage()
    {
        await using var host = await RunicTestHost.StartGuestAsync(Process(HostedGuestProgram.Guest("main")), new RunicTestHostOptions
        {
            StartTimeout = TestTimeouts.Scale(Timeout),
            Appearance = DarkMoreContrast,
        });
        if (host.Exited.IsCompleted) Skip.Test("The guest refused hosted mode (an elevated test process).");
        var window = await host.WaitForWindowAsync(nameof(ConformanceMainWindow), TestTimeouts.Scale(Timeout));
        using var cancel = TestTimeouts.Cancellation(Timeout);
        // The host's window loads the launch address, which binds this client; the page then connects as the host's web
        // view's page would.
        var cookies = new CookieContainer();
        using (var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies }))
            _ = await http.GetStringAsync(window.LaunchUrl, cancel.Token);
        var launch = window.LaunchUrl.AbsoluteUri;
        var surface = new Uri(launch[..(launch.IndexOf("_runic_launch/", StringComparison.Ordinal))]);
        using var page = await DesktopWireClient.ConnectAsync(surface, cancel.Token, cookies);
        await page.HelloAsync(cancel.Token, "appearance/1");

        var first = await NextAppearanceAsync(page, cancel.Token);
        Check(first["colorScheme"]?.GetValue<string>() == "dark" && first["contrast"]?.GetValue<string>() == "more" &&
            first["reducedMotion"]?.GetValue<bool>() == true && first["textScale"]?.GetValue<double>() == 1.25 &&
            first["accentColor"]?.GetValue<string>() == "#3366cc", $"The page received {first.ToJsonString()}.");

        await host.SetAppearanceAsync(ForcedColors, cancel.Token);
        var forced = await NextAppearanceAsync(page, cancel.Token);
        Check(forced["contrast"]?.GetValue<string>() == "forced-colors" && forced["systemColors"]?["canvas"]?.GetValue<string>() == "#000000" &&
            forced["systemColors"]?["linkText"]?.GetValue<string>() == "#ffff00", $"The page received {forced.ToJsonString()}.");

        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await host.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{host.StandardError}");
    }

    private static async Task<JsonObject> NextAppearanceAsync(DesktopWireClient page, CancellationToken cancellationToken)
    {
        while (true)
        {
            var push = JsonNode.Parse(await page.NextPushAsync(cancellationToken))!.AsObject();
            if (push["kind"]?.GetValue<string>() == "appearance") return push;
        }
    }

    // Runic's host: the guest follows its locale and appearance, its window services run with the window's clipboard,
    // monitors and launcher under the default policy, and a drop onto its window reaches the guest.
    [Test, NotInParallel(Order = 72)]
    public async Task TheHostServesAGuestsWindowServicesWithItsDefaultPolicy()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
        var file = TempFile("report.txt");
        await using var platform = new RunicTestPlatform();
        platform.Clipboard.SetText("from host");
        var dialogs = new FakeHostDialogs();
        using var installed = dialogs.Install(platform);
        var policy = new RecordingServicePolicy();
        await fixture.Locale.SetLocaleAsync("de-CH");
        fixture.Settings.Set(DarkMoreContrast);
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("window-services"), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        string Errors() => fixture.GuestErrors;
        await WaitForAsync(Errors, "LOCALE de-CH", "hello's locale");
        await WaitForAsync(Errors, "APPEARANCE Dark More", "hello's appearance");

        // The host's locale and appearance change: the guest follows both.
        await fixture.Locale.SetLocaleAsync("ar");
        await WaitForAsync(Errors, "LOCALE ar", "the locale push");
        Check(await TestTimeouts.UntilAsync(() => policy.Presented.Any(static request => request.Kind == RunicHostedPresentationKind.SetLayoutDirection &&
            request.LayoutDirection == DesktopLayoutDirection.RightToLeft), Timeout), "The window's direction did not follow the locale.");
        fixture.Settings.Set(ForcedColors);
        await WaitForAsync(Errors, "APPEARANCE Dark ForcedColors", "the appearance push");

        var native = fixture.Factory.Hosts.Single();
        native.Drop([file], "folder-1");
        await WaitForAsync(Errors, $"DROP {file} folder-1", "the drop");
        await WaitForAsync(Errors, "PHASE 1", "the window services");
        // Reads for the active window; links of http, https and mailto, and the files the user gave the guest; no
        // settings page and no other file; no drags out of the window.
        // The drop was the user's work in the window, which the guest's own window-open had activated.
        foreach (var line in (string[])["READ from host", "WRITE success", "FORMATS Text, Html", "CLIPBOARD-WATCH unavailable BackendUnavailable",
            "MONITORS primary,side", "LAUNCH-URI success", "LAUNCH-SETTINGS failed PermissionDenied", "LAUNCH-GIVEN success",
            "LAUNCH-OTHER failed PermissionDenied", "DRAG unavailable OwnerUnavailable", "LAUNCH-REPLACED failed PermissionDenied"])
            await WaitForAsync(Errors, line, "the window services");
        Check(policy.Services.Where(static request => request.Kind is RunicHostedServiceKind.ClipboardRead or RunicHostedServiceKind.Launch)
            .All(static request => !request.IsGuestActivated), "The user's drop did not end the guest's activation.");
        Check(policy.Presented.Any(static request => request is { Kind: RunicHostedPresentationKind.Announce, Text: "Now", Priority: DesktopAnnouncementPriority.Assertive }),
            "The active window's assertive announcement was not assertive.");
        Check(platform.Clipboard.Content is { Text: "from guest", Html: "<b>from guest</b>" }, "The guest's write did not reach the host's clipboard.");
        var launches = platform.Launches;
        Check(launches.Count == 2 && launches[0].Uri == new Uri("https://runic.dev/docs") && launches[1].Path == file &&
            launches[1].Operation == DesktopFileOperation.Reveal, $"The host launched {string.Join("; ", launches)}.");
        Check(policy.Services.Any(static request => request.Kind == RunicHostedServiceKind.FileDrop && request.IsAllowedByDefault) &&
            policy.Services.Any(static request => request.Kind == RunicHostedServiceKind.Launch && request.Uri?.Scheme == "ms-settings" && !request.IsAllowedByDefault),
            "The policy did not see the drop and the settings link as the defaults decide them.");
        Check(!HasLine(Errors(), "ANNOUNCE unavailable OwnerUnavailable"), "The announcement did not reach the host.");

        // monitors-changed after the host's monitors change.
        fixture.Factory.FakeMonitors.Set([new DesktopMonitor("only", "Only") { Bounds = new(0, 0, 1024, 768), WorkArea = new(0, 0, 1024, 768), IsPrimary = true }]);
        await WaitForAsync(Errors, "WATCH only", "monitors-changed");

        // Another window has the focus: the guest cannot read the clipboard. Its own locale stays.
        native.SetActive(false);
        await fixture.Locale.SetLocaleAsync("fr");
        await WaitForAsync(Errors, "READ-INACTIVE failed PermissionDenied", "the inactive window's read");
        Check(await TestTimeouts.UntilAsync(() => policy.Presented.Any(static request =>
            request is { Kind: RunicHostedPresentationKind.Announce, Text: "Later", Priority: DesktopAnnouncementPriority.Polite }), Timeout),
            "The inactive window's assertive announcement was not made polite.");
        await WaitForAsync(Errors, "WRITE-INACTIVE failed PermissionDenied", "the inactive window's write after the grace");
        await WaitForAsync(Errors, "OWN en", "the application's own locale");
        await fixture.Locale.SetLocaleAsync("de");
        await Task.Delay(TestTimeouts.Scale(TimeSpan.FromMilliseconds(500)));
        Check(!HasLine(Errors(), "LOCALE de"), "The host's locale replaced the application's own.");

        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{Errors()}");
    }

    // A policy that widens the defaults: the guest may open settings pages, and drops reach it without the second path; and
    // narrows them: no clipboard reads, and writes of plain text only.
    [Test, NotInParallel(Order = 73)]
    public async Task APolicyWidensOrNarrowsTheDefaults()
    {
        await using var fixture = await HostFixture.StartAsync();
        await fixture.RequireUnelevatedAsync();
        var file = TempFile("a.txt");
        var hidden = Path.Combine(Path.GetDirectoryName(file)!, "hidden.txt");
        File.WriteAllText(hidden, "hidden");
        await using var platform = new RunicTestPlatform();
        var dialogs = new FakeHostDialogs();
        using var installed = dialogs.Install(platform);
        var policy = new RecordingServicePolicy
        {
            Decide = request => request switch
            {
                { Kind: RunicHostedServiceKind.Launch, Uri.Scheme: "ms-settings" } => RunicHostedServiceDecision.Allow(),
                { Kind: RunicHostedServiceKind.FileDrop } => RunicHostedServiceDecision.Apply(request with { Paths = [.. request.Paths.Where(path => path != hidden)] }),
                { Kind: RunicHostedServiceKind.ClipboardRead } => RunicHostedServiceDecision.Refuse("No clipboard reads for guests."),
                // Plain text only.
                { Kind: RunicHostedServiceKind.ClipboardWrite } => RunicHostedServiceDecision.Apply(request with { ClipboardFormats = DesktopClipboardFormats.Text }),
                _ => null,
            },
        };
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("window-services"), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        string Errors() => fixture.GuestErrors;
        fixture.Factory.Hosts.Single().Drop([file, hidden]);
        await WaitForAsync(Errors, $"DROP {file}", "the narrowed drop");
        await WaitForAsync(Errors, "PHASE 1", "the window services");
        await WaitForAsync(Errors, "READ failed PermissionDenied", "the refused read");
        await WaitForAsync(Errors, "LAUNCH-SETTINGS success", "the widened launch");
        await WaitForAsync(Errors, "WRITE success", "the narrowed write");
        Check(platform.Clipboard.Content is { Text: "from guest", Html: null }, "The narrowed write did not write the text only.");
        Check(platform.Launches.Any(static launch => launch.Uri?.Scheme == "ms-settings"), "The settings page was not opened.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{Errors()}");
    }

    // Records the window services and presentation requests it decides; Decide overrides the default decision.
    private sealed class RecordingServicePolicy : IRunicHostedWindowPolicy
    {
        private readonly List<RunicHostedServiceRequest> _services = [];
        private readonly List<RunicHostedPresentationRequest> _presented = [];

        public Func<RunicHostedServiceRequest, RunicHostedServiceDecision?>? Decide { get; init; }

        public IReadOnlyList<RunicHostedServiceRequest> Services
        {
            get { lock (_services) return [.. _services]; }
        }

        public IReadOnlyList<RunicHostedPresentationRequest> Presented
        {
            get { lock (_presented) return [.. _presented]; }
        }

        public ValueTask<RunicHostedServiceDecision> ServiceAsync(RunicHostedServiceRequest request, CancellationToken cancellationToken)
        {
            lock (_services) _services.Add(request);
            return ValueTask.FromResult(Decide?.Invoke(request)
                ?? (request.IsAllowedByDefault ? RunicHostedServiceDecision.Allow() : RunicHostedServiceDecision.Refuse()));
        }

        public RunicHostedPresentationDecision Present(RunicHostedPresentationRequest request)
        {
            lock (_presented) _presented.Add(request);
            return RunicHostedPresentationDecision.Allow();
        }
    }
}
