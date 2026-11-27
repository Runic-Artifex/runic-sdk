using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Tests;

// The .NET host of the Runic host protocol for the conformance driver (eng/conformance/PROTOCOL.md, "Host protocol";
// the host.host.* ops of HostConformanceAdapter). The driver plays the guest: it reads the offer, writes the launch
// lines and owns the channel. This adapter starts IRunicHostedApplications on a headless Desktop host (HostFixture),
// with this suite's executable as the relay guest (HostedGuestProgram.RunPuppet), which connects to the driver's relay
// port. It plays the host's user and application (ScenarioPolicy, HostUser, FakeHostDialogs, HostedRegionTestPage) and
// reports what the host shows; it checks nothing of the protocol itself.
internal sealed class ConformanceHost : IAsyncDisposable
{
    private readonly JsonObject _host;
    private readonly HostFixture _fixture;
    private readonly Task<RunicHostedApplication> _start;
    // The host's windows in the order they opened, which is the order of the factory's native windows.
    private readonly List<RunicHostedWindow> _opened = [];
    private readonly FakeHostDialogs _dialogs;
    private readonly HostUser _user = new();
    private readonly IDisposable _dialogsInstalled;
    // The host windows' clipboard and launcher (window-services/1).
    private readonly Runic.Application.Testing.RunicTestPlatform _platform;
    // region/1: the host's own windows whose pages declare regions, by the scenario's names, and the native windows that
    // the host's dialogs were created for, in order.
    private readonly Dictionary<string, HostPage> _pages = new(StringComparer.Ordinal);
    private readonly List<DesktopWindow> _dialogOwners = [];
    // The host application's departures (RunicHostedRegion.AsDepartureGuard) and setRegionAppearance results, in order.
    private readonly Queue<Task<bool>> _departures = new();
    private readonly Queue<Task<bool>> _appearanceResults = new();

    private sealed record HostPage(HostedRegionTestPage Page, DesktopSurface Surface, DesktopWindow? Window, HostedWindowHost? Host);

    private ConformanceHost(JsonObject host, HostFixture fixture, Runic.Application.Testing.RunicTestPlatform platform, FakeHostDialogs dialogs,
        IDisposable dialogsInstalled, Func<ConformanceHost, Task<RunicHostedApplication>> start)
    {
        _host = host;
        _dialogs = dialogs;
        _fixture = fixture;
        _platform = platform;
        _dialogsInstalled = dialogsInstalled;
        _start = start(this);
    }

    private bool BrowserHost => _host["browser"]?.GetValue<bool>() == true;

    // host.host.start: the host starts the relay guest with the scenario's host, limits and policy, and does not wait for
    // the start to end (the driver plays the guest's side of it).
    public static async Task<ConformanceHost> StartAsync(JsonObject host, JsonObject limits, int relayPort)
    {
        var fixture = await HostFixture.StartAsync(serveOnly: host["browser"]?.GetValue<bool>() == true);
        var platform = new Runic.Application.Testing.RunicTestPlatform();
        var dialogs = new FakeHostDialogs();
        var installed = dialogs.Install(platform);
        TimeSpan Limit(string name, TimeSpan fallback) => limits[name] is { } value ? TimeSpan.FromMilliseconds(value.GetValue<int>()) : fallback;
        int Rate(string name, int fallback) => limits[name]?.GetValue<int>() ?? fallback;
        return new ConformanceHost(host, fixture, platform, dialogs, installed, self =>
        {
            self._dialogs.Answer = self._user.DialogAsync;
            fixture.Applications.WindowOpened += (_, e) => { lock (self._opened) self._opened.Add(e.Window); };
            fixture.Factory.ContextMenu = self._user.ContextMenuAsync;
            fixture.Factory.Places = host["windowPlacement"]?.GetValue<bool>() == true;
            var withDialogs = RunicHostedApplication.CreateDialogs;
            RunicHostedApplication.CreateDialogs = window =>
            {
                lock (self._dialogOwners) self._dialogOwners.Add(window);
                return withDialogs(window);
            };
            var policy = host["policy"]?.GetValue<string>();
            var defaults = RunicHostedLimits.Default;
            var start = HostedGuestProgram.StartInfo(HostedGuestProgram.PuppetArgument) with
            {
                UserStarted = host["userStarted"]?.GetValue<bool>() == true,
                Environment = new Dictionary<string, string?>
                {
                    [HostedGuestProgram.PuppetPortVariable] = relayPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            };
            return fixture.Applications.StartAsync(start, new RunicHostedApplicationOptions
            {
                StartTimeout = Limit("startTimeout", HostProtocol.DefaultStartTimeout),
                StopTimeout = Limit("stopTimeout", HostProtocol.DefaultStopTimeout),
                HostName = host["name"]!.GetValue<string>(),
                HostVersion = host["version"]!.GetValue<string>(),
                Policy = new ScenarioPolicy(new RunicHostedLimits
                {
                    MenuRequestsPerSecond = Rate("menuRequests", defaults.MenuRequestsPerSecond),
                    MaximumDialogs = Rate("dialogs", defaults.MaximumDialogs),
                    GuestMenuRequestsPerSecond = Rate("guestMenuRequests", defaults.GuestMenuRequestsPerSecond),
                    DialogsPerTenSeconds = Rate("dialogRate", defaults.DialogsPerTenSeconds),
                    ServiceRequestsPerSecond = Rate("serviceRate", defaults.ServiceRequestsPerSecond),
                    LaunchesPerTenSeconds = Rate("launchRate", defaults.LaunchesPerTenSeconds),
                    PresentsPerSecond = Rate("presentRate", defaults.PresentsPerSecond),
                    PresentBurst = Rate("presentRate", defaults.PresentBurst),
                    AnnouncementsPerSecond = Rate("announcementRate", defaults.AnnouncementsPerSecond),
                    AnnouncementBurst = Rate("announcementRate", defaults.AnnouncementBurst),
                }, failsServices: policy == "fails-services",
                    prompt: policy == "prompts-launches" ? self._user.PromptAsync : null,
                    embed: host["embed"]?.GetValue<string>(),
                    embedComponent: host["embedComponent"]?.GetValue<bool>() == true,
                    limitsPlacement: policy == "limits-placement",
                    embedOptions: new RunicHostedRegionOptions
                    {
                        FocusOnAttach = host["embedOptions"]?["focusOnAttach"]?.GetValue<bool>() ?? false,
                        ReattachOnReload = host["embedOptions"]?["reattachOnReload"]?.GetValue<bool>() ?? true,
                        Appearance = HostedAppearance(host["embedOptions"]?["appearance"]),
                    }),
            }).AsTask();
        });
    }

    public async Task<JsonNode?> HandleAsync(string op, JsonObject args)
    {
        var wait = TimeSpan.FromMilliseconds(args["waitMs"]?.GetValue<double>() ?? 10_000);
        // The host queues WindowOpened and WindowClosed before its reply and delivers them on the thread pool: the user
        // and the observations act on the windows the host's events reported (#1281).
        if (_fixture.Applications is RunicHostedApplications applications)
            await applications.EventsDelivered.WaitAsync(wait);
        switch (op)
        {
            case "host.host.user":
                try
                {
                    await UserAsync(args["action"]!.AsObject(), wait);
                    return new JsonObject();
                }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or KeyNotFoundException)
                {
                    return new JsonObject { ["failed"] = error.Message };
                }
            case "host.host.observe":
                return new JsonObject { ["value"] = Observe(args) };
            case "host.host.wait":
                return await WaitAsync(args["kind"]!.GetValue<string>(), wait);
            default:
                throw new NotSupportedException($"The .NET host adapter has no op {op}.");
        }
    }

    // ----- Windows

    private RunicHostedWindow Window(string id)
    {
        lock (_opened) return _opened.LastOrDefault(window => window.Id == id) ?? throw new InvalidOperationException($"The host opened no window {id}.");
    }

    private string[] OpenIds()
    {
        lock (_opened) return [.. _opened.Where(static window => window.IsOpen).Select(static window => window.Id).Distinct()];
    }

    // The native window of a guest's window, or of a host window whose page the scenario plays. The factory creates the
    // native windows in the order they open: the guest's windows of their own and the host's windows.
    private HostedWindowHost Native(string id)
    {
        if (_pages.TryGetValue(id, out var page))
            return page.Host ?? throw new InvalidOperationException($"The browser host's page {id} has no native window.");
        int index;
        lock (_opened) index = _opened.Where(static window => window.PresentationKind == RunicHostedWindowPresentationKind.Window).ToList()
            .FindLastIndex(window => window.Id == id);
        if (index < 0) throw new InvalidOperationException($"The host opened no window {id} of its own.");
        var hostWindows = _pages.Values.Select(static page => page.Host).OfType<HostedWindowHost>().ToHashSet();
        return _fixture.Factory.Hosts.Where(host => !hostWindows.Contains(host)).ElementAt(index);
    }

    private DesktopWindow NativeWindow(string id) => _pages.TryGetValue(id, out var page)
        ? page.Window ?? throw new InvalidOperationException($"The browser host's page {id} has no native window.")
        : Window(id).DesktopWindow;

    // A host window whose page the scenario plays (HostedRegionTestPage), opened at its first region. In a browser
    // (serve-only) host the page runs in the user's browser, and no native window shows it.
    private async Task<HostPage> PageAsync(string name)
    {
        if (_pages.TryGetValue(name, out var existing)) return existing;
        var surface = await _fixture.Desktop.CreateSurfaceAsync();
        if (BrowserHost)
        {
            var served = new HostPage(new HostedRegionTestPage(_fixture.Host.RegionDirectory, null, DesktopHostedRegionPage.Origin(surface.Url),
                new HostedMenuWindow(), browser: true), surface, null, null);
            _pages[name] = served;
            return served;
        }
        var window = await surface.OpenWindowAsync(RunicDesktopHost.DefaultWindowOptions);
        var host = _fixture.Factory.Hosts[^1];
        var page = new HostPage(new HostedRegionTestPage(_fixture.Host.RegionDirectory, window, DesktopHostedRegionPage.Origin(surface.Url), new HostedMenuWindow()),
            surface, window, host);
        window.Closed += (_, _) => page.Page.Close();
        _pages[name] = page;
        return page;
    }

    private static RunicHostedAppearance? HostedAppearance(JsonNode? node) =>
        HostServices.ReadOverride(node) is { } read
            ? new RunicHostedAppearance
            {
                ColorScheme = read.ColorScheme,
                Contrast = read.Contrast,
                ReducedMotion = read.ReducedMotion,
                TextScale = read.TextScale,
                AccentColor = read.AccentColor,
                SystemColors = read.SystemColors,
            }
            : null;

    private static HostedRegionFocusSource FocusSource(string source) => source switch
    {
        "tab" => HostedRegionFocusSource.Tab,
        "shift-tab" => HostedRegionFocusSource.ShiftTab,
        "pointer" => HostedRegionFocusSource.Pointer,
        _ => HostedRegionFocusSource.Script,
    };

    // The user gives the focus to another window or back to this one. The step ends once the window reported the change.
    private async Task SwitchAsync(string id, bool active, TimeSpan wait)
    {
        var window = NativeWindow(id);
        if (window.Activity?.IsActive == active)
        {
            Native(id).SetActive(active);
            return;
        }
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnActivity(object? sender, DesktopWindowActivityChangedEventArgs e)
        {
            if (e.Activity.IsActive == active) reported.TrySetResult();
        }
        window.ActivityChanged += OnActivity;
        try
        {
            Native(id).SetActive(active);
            await reported.Task.WaitAsync(wait);
        }
        finally
        {
            window.ActivityChanged -= OnActivity;
        }
    }

    // ----- The host's user and application (README, "The host's user"; "Region scenarios")

    private async Task UserAsync(JsonObject user, TimeSpan wait)
    {
        var action = user["action"]!.GetValue<string>();
        string Name(string member) => user[member]!.GetValue<string>();
        switch (action)
        {
            case "chooseMenuItem":
                var titles = user["titles"]!.AsArray().Select(static title => title!.GetValue<string>()).ToArray();
                var native = Native(Name("window"));
                if (user["bar"] is not null)
                {
                    Check(HostMenuChoice.Run(() => native.ChoosePrevious(titles)),
                        $"The window's previous menu bar has no enabled item {string.Join(" > ", titles)}: {HostedWindowHost.Describe(native.PreviousMenuBar)}.");
                    break;
                }
                // A click in the native bar is a choice in the host's own menu; a shortcut is a key the page left alone.
                var chosen = user["by"]?.GetValue<string>() == "shortcut" ? native.Choose(titles) : HostMenuChoice.Run(() => native.Choose(titles));
                Check(chosen, $"The window's menu bar has no enabled item {string.Join(" > ", titles)}: {HostedWindowHost.Describe(native.MenuBar)}.");
                break;
            case "deactivateWindow" or "activateWindow":
                await SwitchAsync(Name("window"), action == "activateWindow", wait);
                break;
            case "deactivateHost":
                _fixture.Factory.DeactivateHost();
                foreach (var id in OpenIds())
                {
                    if (Native(id).IsActive) await SwitchAsync(id, false, wait);
                }
                break;
            case "activateHost":
                if (_fixture.Factory.ActivateHost() is { } key)
                {
                    var keyId = OpenIds().LastOrDefault(id => ReferenceEquals(Native(id), key))
                        ?? throw new InvalidOperationException("The host application has no open window of the guest to activate.");
                    await SwitchAsync(keyId, true, wait);
                }
                break;
            case "copyText":
                var copied = Name("text");
                _platform.Clipboard.SetText(user["repeat"] is { } repeat ? string.Concat(Enumerable.Repeat(copied, repeat.GetValue<int>())) : copied);
                break;
            case "changeMonitors":
                _fixture.Factory.FakeMonitors.Set([new DesktopMonitor("changed", "Changed")
                {
                    Bounds = new(0, 0, 1024, 768),
                    WorkArea = new(0, 0, 1024, 768),
                    IsPrimary = true,
                }]);
                break;
            case "setLocale":
                await _fixture.Locale.SetLocaleAsync(user["locale"]?.GetValue<string>());
                break;
            case "setAppearance":
                var appearance = user["appearance"]!.AsObject();
                _fixture.Settings.Set(new DesktopAppearance(appearance["colorScheme"]!.GetValue<string>() == "dark" ? DesktopColorScheme.Dark : DesktopColorScheme.Light,
                    ReducedMotion: false)
                {
                    Contrast = appearance["contrast"]!.GetValue<string>() switch
                    {
                        "more" => DesktopContrast.More,
                        "forced-colors" => DesktopContrast.ForcedColors,
                        _ => DesktopContrast.NoPreference,
                    },
                    TextScale = 1,
                });
                break;
            case "dropFiles":
                Native(Name("window")).Drop([.. user["paths"]!.AsArray().Select(static path => path!.GetValue<string>())]);
                break;
            case "awaitDialogClosed":
                await _user.AwaitClosedAsync(user["dialog"]?.GetValue<string>(), wait);
                break;
            case "closeWindow":
                var closing = Native(Name("window"));
                _ = Task.Run(closing.UserClose);
                break;
            case "awaitContextMenu":
                await _user.HoldAsync(menu: true, null, wait);
                break;
            case "awaitDialog":
                await _user.HoldAsync(menu: false, user["dialog"]?.GetValue<string>(), wait);
                break;
            case "chooseContextItem":
                var shown = await _user.TakeAsync(menu: true, null, wait);
                if (user["dismiss"] is not null)
                {
                    shown.Answer.TrySetResult(null);
                    break;
                }
                var title = Name("title");
                var item = HostUser.Find((ImmutableArray<DesktopMenuEntry>)shown.What, title)
                    ?? throw new InvalidOperationException($"The context menu has no item {title}: {HostedWindowHost.Describe((ImmutableArray<DesktopMenuEntry>)shown.What)}.");
                shown.Answer.TrySetResult(item);
                break;
            case "answerDialog":
                var dialog = await _user.TakeAsync(menu: false, user["dialog"]?.GetValue<string>(), wait);
                if (user["paths"] is JsonArray paths) dialog.Answer.TrySetResult((IReadOnlyList<string>)[.. paths.Select(static path => path!.GetValue<string>())]);
                else if (user["button"] is { } button) dialog.Answer.TrySetResult(button.GetValue<string>());
                else dialog.Answer.TrySetResult(null);
                break;
            // region/1: the host page's reports (hosted-regions/1), which a host window's page sends its backend.
            case "declareRegion":
                (await PageAsync(Name("window"))).Page.Declare(Name("name"),
                    user["sizing"]?.GetValue<string>() == "content-height" ? HostedRegionSizing.ContentHeight : HostedRegionSizing.Fill);
                break;
            case "removeRegion":
                _pages[Name("window")].Page.Remove(Name("name"), user["reason"]?.GetValue<string>() ?? "removed");
                break;
            case "resizeRegion":
                _pages[Name("window")].Page.Report(Name("name"), region =>
                {
                    region.Width = user["width"]!.GetValue<double>();
                    region.Height = user["height"]!.GetValue<double>();
                    region.Scale = user["scale"]!.GetValue<double>();
                    if (user["visible"] is { } visible) region.Visible = visible.GetValue<bool>();
                });
                break;
            case "focusRegion":
                _pages[Name("window")].Page.FocusRegion(Name("name"), FocusSource(Name("source")));
                break;
            case "focusHost":
                _pages[Name("window")].Page.FocusHost(user["focused"]?.GetValue<bool>() ?? true);
                break;
            // The host application's own decisions about a region window.
            case "departRegion":
                _departures.Enqueue(Window(Name("window")).Region!.AsDepartureGuard().CanDepartAsync(null!, CancellationToken.None).AsTask());
                break;
            case "detachRegion":
                await Window(Name("window")).Region!.DetachAsync().AsTask().WaitAsync(wait);
                break;
            case "moveRegion":
                var target = (await PageAsync(Name("to"))).Page.Window!;
                await Window(Name("window")).Region!.MoveToAsync(target, Name("name")).AsTask().WaitAsync(wait);
                break;
            // region-appearance/1: the host application changes the region's override; a wait for applied reads its result.
            case "setRegionAppearance":
                _appearanceResults.Enqueue(Window(Name("window")).Region!.SetAppearanceAsync(HostedAppearance(user["appearance"])).AsTask());
                break;
            default:
                throw new NotSupportedException($"The .NET host adapter has no user action {action}.");
        }
    }

    // ----- What the host shows, read again on each call (host.host.observe)

    private JsonNode? Observe(JsonObject args)
    {
        var kind = args["kind"]!.GetValue<string>();
        string Name(string member) => args[member]!.GetValue<string>();
        switch (kind)
        {
            case "menuBar":
                return HostMenuWire.Entries(Native(Name("window")).MenuBar);
            case "region":
                return Window(Name("window")).Region is { } region
                    ? new JsonObject
                    {
                        ["state"] = region.State.ToString().ToLowerInvariant(),
                        ["title"] = region.Title,
                        ["focused"] = region.IsFocused,
                        ["documentEdited"] = region.IsDocumentEdited,
                    }
                    : null;
            case "page":
                var name = Name("name");
                return _pages[Name("window")].Page.Find(name) is { } declared
                    ? new JsonObject
                    {
                        ["name"] = name,
                        ["shows"] = declared.Shows,
                        ["src"] = declared.Src,
                        ["module"] = declared.Module,
                        ["token"] = declared.Token,
                        ["title"] = declared.Title,
                        ["allow"] = declared.Allow,
                        ["height"] = declared.AppliedHeight,
                        ["colorScheme"] = declared.ColorScheme,
                    }
                    : new JsonObject { ["name"] = name };
            case "closed":
                return Window(Name("window")).Closed.IsCompleted;
            case "clipboard":
                return _platform.Clipboard.Content?.Text;
            case "launched":
                return new JsonArray([.. _platform.Launches.Select(static launch => (JsonNode)(launch.Uri?.AbsoluteUri ?? launch.Path ?? ""))]);
            case "dialogOwner":
                DesktopWindow? owner;
                lock (_dialogOwners) owner = _dialogOwners.LastOrDefault();
                if (owner is null) return null;
                if (_pages.FirstOrDefault(page => ReferenceEquals(page.Value.Window, owner)).Key is { } pageName) return pageName;
                lock (_opened) return _opened.LastOrDefault(window => ReferenceEquals(window.DesktopWindow, owner))?.Id;
            default:
                throw new NotSupportedException($"The .NET host adapter cannot observe {kind}.");
        }
    }

    // ----- How the host's start, the departures and the appearance changes ended (host.host.wait)

    private async Task<JsonObject> WaitAsync(string kind, TimeSpan wait)
    {
        switch (kind)
        {
            case "started":
                await Task.WhenAny(_start, Task.Delay(wait));
                if (!_start.IsCompleted) return new JsonObject { ["state"] = "pending" };
                if (_start.IsCompletedSuccessfully) return new JsonObject { ["state"] = "started", ["applicationId"] = _start.Result.Guest.ApplicationId };
                var error = _start.Exception?.GetBaseException();
                return new JsonObject
                {
                    ["state"] = "failed",
                    ["code"] = error is RunicHostedApplicationException hosted ? hosted.Code : "unmapped:" + error?.GetType().Name,
                    ["message"] = error?.Message,
                };
            case "applied" or "departure":
                var queue = kind == "applied" ? _appearanceResults : _departures;
                if (!queue.TryPeek(out var next)) throw new InvalidOperationException($"No {kind} is pending.");
                await Task.WhenAny(next, Task.Delay(wait));
                if (!next.IsCompleted) return new JsonObject { ["state"] = "pending" };
                _ = queue.Dequeue();
                return new JsonObject { ["state"] = "done", ["value"] = await next };
            default:
                throw new NotSupportedException($"The .NET host adapter cannot wait for {kind}.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public async ValueTask DisposeAsync()
    {
        // The driver's guest never closes: end the host's session and the relay guest with it. A start still pending
        // ends with the host's own stop.
        if (_start.IsCompletedSuccessfully)
        {
            try { _ = await _start.Result.KillAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) when (error is TimeoutException or RunicHostedApplicationException) { }
        }
        foreach (var page in _pages.Values) await page.Surface.DisposeAsync();
        await _fixture.DisposeAsync();
        _dialogsInstalled.Dispose();
        await _platform.DisposeAsync();
    }
}
