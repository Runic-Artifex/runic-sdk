using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;

// The host runner of the Runic host protocol corpus (specs/desktop/host-conformance): for every scenario of the host role
// it plays the guest against IRunicHostedApplications. The host starts this suite's executable as a puppet
// (HostedGuestProgram.RunPuppet), which relays its standard input and output to the runner; the runner reads the offer,
// writes the launch lines, listens on its own channel and answers the host's connection. The host's windows are headless
// (HostedWindowHost); the runner's HostUser plays the host's user, who chooses menu items and answers context menus and
// dialogs (FakeHostDialogs).
internal sealed class HostRoleConformanceTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    public static IEnumerable<string> HostScenarios() =>
        Directory.EnumerateFiles(HostConformanceTests.Corpus, "*.json", SearchOption.AllDirectories)
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!.AsObject())
            .Where(scenario => scenario["roles"]!.AsArray().Any(role => role!.GetValue<string>() == "host"))
            .Select(scenario => scenario["id"]!.GetValue<string>())
            .Order(StringComparer.Ordinal);

    [Test, NotInParallel(Order = 45)]
    [MethodDataSource(nameof(HostScenarios))]
    [DisplayName("Host($id)")]
    public async Task Host(string id)
    {
        var file = Directory.EnumerateFiles(HostConformanceTests.Corpus, "*.json", SearchOption.AllDirectories)
            .Single(path => JsonNode.Parse(File.ReadAllText(path))!["id"]!.GetValue<string>() == id);
        await using var runner = new HostRunner(JsonNode.Parse(File.ReadAllText(file))!.AsObject());
        await runner.RunAsync();
    }

    // Runs one scenario: the runner is the guest.
    private sealed class HostRunner(JsonObject scenario) : IAsyncDisposable
    {
        private readonly TcpListener _control = new(IPAddress.Loopback, 0);
        private readonly Dictionary<string, WebSocket> _connections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonNode?> _bindings = new(StringComparer.Ordinal);
        private HostFixture? _fixture;
        private Task<RunicHostedApplication>? _start;
        private TcpClient? _puppet;
        private StreamReader? _input;
        private StreamWriter? _commands;
        private TcpListener? _listener;
        private readonly List<TcpClient> _clients = [];
        // The host's windows in the order they opened, which is the order of the factory's native windows.
        private readonly List<RunicHostedWindow> _opened = [];
        private readonly FakeHostDialogs _dialogs = new();
        private readonly HostUser _user = new();
        private readonly ScenarioFiles _files = new();
        private IDisposable? _dialogsInstalled;
        // The host windows' clipboard and launcher (window-services/1).
        private readonly Runic.Application.Testing.RunicTestPlatform _platform = new();
        private RunicHostedApplication? _application;
        // region/1: the host's own windows whose pages declare regions, by the scenario's names, and the native windows
        // that the host's dialogs were created for, in order.
        private readonly Dictionary<string, HostPage> _pages = new(StringComparer.Ordinal);
        private readonly List<DesktopWindow> _dialogOwners = [];
        // The host application's departures (RunicHostedRegion.AsDepartureGuard), in order.
        private readonly Queue<Task<bool>> _departures = new();
        private long _nextRequest = 1000;
        private byte[]? _nonce;
        private string? _key;

        private JsonObject Limits => scenario["arrange"]?["limits"] as JsonObject ?? [];

        private TimeSpan Limit(string name, TimeSpan fallback) =>
            Limits[name] is { } value ? TimeSpan.FromMilliseconds(value.GetValue<int>()) : fallback;

        // The wait of an advance step. Runic's host fixes how long an activation after the guest's request counts as the
        // guest's (RunicHostedWindow.GuestActivationWindow), so the runner waits at least that long, and a little more.
        private TimeSpan Advance(string name)
        {
            var wait = Limit(name, StepTimeout);
            return name == "guestActivation"
                ? TimeSpan.FromTicks(Math.Max(wait.Ticks, RunicHostedWindow.GuestActivationWindow.Ticks)) + TimeSpan.FromMilliseconds(100)
                : wait;
        }

        public async Task RunAsync()
        {
            var steps = scenario["steps"]!.AsArray();
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index]!.AsObject();
                try { await StepAsync(step); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw new InvalidOperationException($"{scenario["id"]}: step {index} {step.ToJsonString()} failed: {error.Message}", error);
                }
            }
        }

        private async Task StepAsync(JsonObject step)
        {
            var (action, value) = step.First(member => member.Key is not ("connection" or "note"));
            var connection = step["connection"]?.GetValue<string>();
            switch (action)
            {
                case "start": await StartAsync(); break;
                case "offer": await OfferAsync(value!.AsObject()); break;
                case "line": await LineAsync(value!.AsObject()); break;
                case "accept": await AcceptAsync(value!.GetValue<string>()); break;
                case "receive": await ReceiveAsync(connection!, value!.AsObject()); break;
                case "send": await SendAsync(connection!, value!.AsObject()); break;
                case "closed": await ClosedAsync(value!.AsObject()); break;
                case "disconnect": await HostChannel.CloseAsync(_connections[value!.GetValue<string>()], WebSocketCloseStatus.NormalClosure, ""); break;
                case "exit": await ExitAsync(value!.GetValue<int>()); break;
                case "advance": await Task.Delay(Advance(value!.GetValue<string>())); break;
                case "expect": await ExpectAsync(value!.AsObject()); break;
                case "user": await UserAsync(value!.AsObject()); break;
                default: throw new InvalidOperationException($"The host runner does not implement the step '{action}'.");
            }
        }

        private async Task StartAsync()
        {
            _control.Start();
            _fixture = await HostFixture.StartAsync(serveOnly: BrowserHost);
            _fixture.Applications.WindowOpened += (_, e) => { lock (_opened) _opened.Add(e.Window); };
            _fixture.Factory.ContextMenu = _user.ContextMenuAsync;
            _fixture.Factory.Places = scenario["arrange"]!["host"]!["windowPlacement"]?.GetValue<bool>() == true;
            _dialogs.Answer = _user.DialogAsync;
            _dialogsInstalled = _dialogs.Install(_platform);
            var installed = RunicHostedApplication.CreateDialogs;
            RunicHostedApplication.CreateDialogs = window =>
            {
                lock (_dialogOwners) _dialogOwners.Add(window);
                return installed(window);
            };
            var host = scenario["arrange"]!["host"]!;
            var start = HostedGuestProgram.StartInfo(HostedGuestProgram.PuppetArgument) with
            {
                UserStarted = host["userStarted"]?.GetValue<bool>() == true,
                Environment = new Dictionary<string, string?>
                {
                    [HostedGuestProgram.PuppetPortVariable] = ((IPEndPoint)_control.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            };
            _start = _fixture.Applications.StartAsync(start, new RunicHostedApplicationOptions
            {
                // The scenarios' limits are small; the runner's own waits are scaled for slow machines.
                StartTimeout = Limit("startTimeout", HostProtocol.DefaultStartTimeout),
                StopTimeout = Limit("stopTimeout", HostProtocol.DefaultStopTimeout),
                HostName = host["name"]!.GetValue<string>(),
                HostVersion = host["version"]!.GetValue<string>(),
                // The scenario's rates and dialogs per guest, or the host's defaults; a host policy that fails for window
                // services when the scenario says so.
                Policy = new ScenarioPolicy(new RunicHostedLimits
                {
                    MenuRequestsPerSecond = Limits["menuRequests"]?.GetValue<int>() ?? RunicHostedLimits.Default.MenuRequestsPerSecond,
                    MaximumDialogs = Limits["dialogs"]?.GetValue<int>() ?? RunicHostedLimits.Default.MaximumDialogs,
                    GuestMenuRequestsPerSecond = Limits["guestMenuRequests"]?.GetValue<int>() ?? RunicHostedLimits.Default.GuestMenuRequestsPerSecond,
                    DialogsPerTenSeconds = Limits["dialogRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.DialogsPerTenSeconds,
                    ServiceRequestsPerSecond = Limits["serviceRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.ServiceRequestsPerSecond,
                    LaunchesPerTenSeconds = Limits["launchRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.LaunchesPerTenSeconds,
                    PresentsPerSecond = Limits["presentRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.PresentsPerSecond,
                    PresentBurst = Limits["presentRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.PresentBurst,
                    AnnouncementsPerSecond = Limits["announcementRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.AnnouncementsPerSecond,
                    AnnouncementBurst = Limits["announcementRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.AnnouncementBurst,
                }, failsServices: host["policy"]?.GetValue<string>() == "fails-services",
                    prompt: host["policy"]?.GetValue<string>() == "prompts-launches" ? _user.PromptAsync : null,
                    embed: host["embed"]?.GetValue<string>(),
                    embedComponent: host["embedComponent"]?.GetValue<bool>() == true,
                    limitsPlacement: host["policy"]?.GetValue<string>() == "limits-placement",
                    embedOptions: new RunicHostedRegionOptions
                    {
                        FocusOnAttach = host["embedOptions"]?["focusOnAttach"]?.GetValue<bool>() ?? false,
                        ReattachOnReload = host["embedOptions"]?["reattachOnReload"]?.GetValue<bool>() ?? true,
                    }),
            }).AsTask();
            using var accept = TestTimeouts.Cancellation(StepTimeout);
            _puppet = await _control.AcceptTcpClientAsync(accept.Token);
            var stream = _puppet.GetStream();
            _input = new StreamReader(stream, new UTF8Encoding(false));
            _commands = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        private async Task OfferAsync(JsonObject expected)
        {
            using var timeout = TestTimeouts.Cancellation(StepTimeout);
            var line = await _input!.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The host wrote no offer.");
            var offer = HostProtocol.ParseLine(line, HostProtocol.OfferPrefix) ?? throw new InvalidOperationException($"Not an offer line: {line}");
            _nonce = HostProtocol.DecodeSecret(HostProtocol.String(offer, "nonce")) ?? throw new InvalidOperationException("The offer's nonce is not 32 bytes.");
            Match(expected, offer, "offer");
        }

        private async Task LineAsync(JsonObject line)
        {
            if (line["text"] is { } text)
            {
                await CommandAsync(text.GetValue<string>());
                return;
            }
            if (line["refused"] is JsonObject refused)
            {
                await CommandAsync(HostProtocol.RefusedPrefix + Resolve(refused)!.ToJsonString());
                return;
            }
            // The channel line names the runner's own listener and a new key.
            var channel = line["channel"]!.AsObject();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _key = HostProtocol.NewSecret();
            await CommandAsync(HostProtocol.ChannelPrefix + new JsonObject
            {
                ["url"] = $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{HostProtocol.ChannelPath}",
                ["key"] = _key,
                ["protocol"] = channel["protocol"]!.GetValue<int>(),
            }.ToJsonString());
        }

        // A plain WebSocket upgrade on the runner's listener, which keeps listening for the host's next connection.
        private async Task AcceptAsync(string name)
        {
            using var timeout = TestTimeouts.Cancellation(StepTimeout);
            var client = await _listener!.AcceptTcpClientAsync(timeout.Token);
            _clients.Add(client);
            var stream = client.GetStream();
            var request = new StringBuilder();
            var buffer = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(buffer, timeout.Token) == 0) throw new InvalidOperationException("The host closed the connection before its upgrade.");
                request.Append((char)buffer[0]);
            }
            var key = request.ToString().Split("\r\n").Select(static header => header.Split(':', 2))
                .Single(static header => header.Length == 2 && header[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))[1].Trim();
#pragma warning disable CA5350 // RFC 6455 defines the upgrade's accept value with SHA-1; it protects nothing.
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            _connections[name] = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        }

        private async Task ReceiveAsync(string name, JsonObject expected)
        {
            using var timeout = TestTimeouts.Cancellation(StepTimeout);
            var message = await HostChannel.ReceiveAsync(_connections[name], timeout.Token)
                ?? throw new InvalidOperationException("The host closed the connection.");
            Match(expected, message, "message");
        }

        private Task SendAsync(string name, JsonObject message) =>
            HostChannel.SendAsync(_connections[name], (JsonObject)Resolve(message)!, CancellationToken.None);

        // The host closes the connection without another message.
        private async Task ClosedAsync(JsonObject closed)
        {
            var socket = _connections[closed["connection"]!.GetValue<string>()];
            using var timeout = TestTimeouts.Cancellation(StepTimeout);
            JsonObject? message;
            try { message = await HostChannel.ReceiveAsync(socket, timeout.Token); }
            catch (Exception error) when (error is WebSocketException or IOException) { message = null; }
            Check(message is null, $"The host sent {message?.ToJsonString()} instead of closing the connection.");
        }

        // ----- The host's user
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
            return _fixture!.Factory.Hosts.Where(host => !hostWindows.Contains(host)).ElementAt(index);
        }

        private DesktopWindow NativeWindow(string id) => _pages.TryGetValue(id, out var page)
            ? page.Window ?? throw new InvalidOperationException($"The browser host's page {id} has no native window.")
            : Window(id).Presentation;

        // A host window whose page the scenario plays (HostedRegionTestPage), opened at its first region. In a browser
        // (serve-only) host the page runs in the user's browser, and no native window shows it.
        private sealed record HostPage(HostedRegionTestPage Page, DesktopSurface Surface, DesktopWindow? Window, HostedWindowHost? Host);

        private bool BrowserHost => scenario["arrange"]!["host"]!["browser"]?.GetValue<bool>() == true;

        private async Task<HostPage> PageAsync(string name)
        {
            if (_pages.TryGetValue(name, out var existing)) return existing;
            var surface = await _fixture!.Desktop.CreateSurfaceAsync();
            if (BrowserHost)
            {
                var served = new HostPage(new HostedRegionTestPage(_fixture.Host.RegionDirectory, null, DesktopHostedRegionPage.Origin(surface.Url),
                    new HostedMenuWindow(), browser: true), surface, null, null);
                _pages[name] = served;
                return served;
            }
            var window = await surface.OpenWindowAsync(RunicDesktopHost.DefaultWindowOptions);
            var host = _fixture.Factory.Hosts[^1];
            // A Window that never opens stands for the host's Window of the page (RunicHostedRegion.MoveToAsync names it).
            var page = new HostPage(new HostedRegionTestPage(_fixture.Host.RegionDirectory, window, DesktopHostedRegionPage.Origin(surface.Url), new HostedMenuWindow()),
                surface, window, host);
            window.Closed += (_, _) => page.Page.Close();
            _pages[name] = page;
            return page;
        }

        private static HostedRegionFocusSource FocusSource(string source) => source switch
        {
            "tab" => HostedRegionFocusSource.Tab,
            "shift-tab" => HostedRegionFocusSource.ShiftTab,
            "pointer" => HostedRegionFocusSource.Pointer,
            _ => HostedRegionFocusSource.Script,
        };

        // The user gives the focus to another window or back to this one. The step ends once the window reported the
        // change: a person switches windows slower than the platform reports it, which the host's rules for the
        // user's activation rely on (host-protocol.md, Window services).
        private async Task SwitchAsync(string id, bool active)
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
                await reported.Task.WaitScaledAsync(StepTimeout);
            }
            finally
            {
                window.ActivityChanged -= OnActivity;
            }
        }

        private async Task UserAsync(JsonObject user)
        {
            var action = user["action"]!.GetValue<string>();
            switch (action)
            {
                case "chooseMenuItem":
                    var titles = user["titles"]!.AsArray().Select(static title => title!.GetValue<string>()).ToArray();
                    var native = Native(user["window"]!.GetValue<string>());
                    if (user["bar"] is not null)
                    {
                        // The bar the window showed before its current one, which the user still clicks during a change.
                        Check(HostMenuChoice.Run(() => native.ChoosePrevious(titles)),
                            $"The window's previous menu bar has no enabled item {string.Join(" > ", titles)}: {HostedWindowHost.Describe(native.PreviousMenuBar)}.");
                        break;
                    }
                    // A click in the native bar is a choice in the host's own menu; a shortcut is a key the page left alone.
                    var chosen = user["by"]?.GetValue<string>() == "shortcut" ? native.Choose(titles) : HostMenuChoice.Run(() => native.Choose(titles));
                    Check(chosen, $"The window's menu bar has no enabled item {string.Join(" > ", titles)}: {HostedWindowHost.Describe(native.MenuBar)}.");
                    break;
                case "deactivateWindow" or "activateWindow":
                    await SwitchAsync(user["window"]!.GetValue<string>(), action == "activateWindow");
                    break;
                case "deactivateHost":
                    // The user switches to another application: the host's active window becomes inactive, and until the
                    // user switches back, the host's requests to activate a window are deferred, as on macOS.
                    _fixture!.Factory.DeactivateHost();
                    foreach (var id in OpenIds())
                    {
                        if (Native(id).IsActive) await SwitchAsync(id, false);
                    }
                    break;
                case "activateHost":
                    // The user switches back to the host application, such as with Cmd-Tab: the desktop activates the
                    // window the host asked for last while it was inactive, else the one active before.
                    if (_fixture!.Factory.ActivateHost() is { } key)
                    {
                        var keyId = OpenIds().LastOrDefault(id => ReferenceEquals(Native(id), key))
                            ?? throw new InvalidOperationException("The host application has no open window of the guest to activate.");
                        await SwitchAsync(keyId, true);
                    }
                    break;
                case "copyText":
                    var copied = user["text"]!.GetValue<string>();
                    _platform.Clipboard.SetText(user["repeat"] is { } repeat ? string.Concat(Enumerable.Repeat(copied, repeat.GetValue<int>())) : copied);
                    break;
                case "changeMonitors":
                    _fixture!.Factory.FakeMonitors.Set([new DesktopMonitor("changed", "Changed")
                    {
                        Bounds = new(0, 0, 1024, 768),
                        WorkArea = new(0, 0, 1024, 768),
                        IsPrimary = true,
                    }]);
                    break;
                case "setLocale":
                    await _fixture!.Locale.SetLocaleAsync(user["locale"]?.GetValue<string>());
                    break;
                case "setAppearance":
                    var appearance = user["appearance"]!.AsObject();
                    _fixture!.Settings.Set(new DesktopAppearance(appearance["colorScheme"]!.GetValue<string>() == "dark" ? DesktopColorScheme.Dark : DesktopColorScheme.Light,
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
                    Native(user["window"]!.GetValue<string>()).Drop([.. user["paths"]!.AsArray().Select(path => Resolve(path)!.GetValue<string>())]);
                    break;
                case "awaitDialogClosed":
                    await _user.AwaitClosedAsync(user["dialog"]?.GetValue<string>(), StepTimeout);
                    break;
                case "closeWindow":
                    var closing = Native(user["window"]!.GetValue<string>());
                    _ = Task.Run(closing.UserClose);
                    break;
                case "awaitContextMenu":
                    await _user.HoldAsync(menu: true, null, StepTimeout);
                    break;
                case "awaitDialog":
                    await _user.HoldAsync(menu: false, user["dialog"]?.GetValue<string>(), StepTimeout);
                    break;
                case "chooseContextItem":
                    var shown = await _user.TakeAsync(menu: true, null, StepTimeout);
                    if (user["dismiss"] is not null)
                    {
                        shown.Answer.TrySetResult(null);
                        break;
                    }
                    var title = user["title"]!.GetValue<string>();
                    var item = HostUser.Find((ImmutableArray<DesktopMenuEntry>)shown.What, title)
                        ?? throw new InvalidOperationException($"The context menu has no item {title}: {HostedWindowHost.Describe((ImmutableArray<DesktopMenuEntry>)shown.What)}.");
                    shown.Answer.TrySetResult(item);
                    break;
                case "answerDialog":
                    var dialog = await _user.TakeAsync(menu: false, user["dialog"]?.GetValue<string>(), StepTimeout);
                    if (user["paths"] is JsonArray paths)
                        dialog.Answer.TrySetResult((IReadOnlyList<string>)[.. paths.Select(path => Resolve(path)!.GetValue<string>())]);
                    else if (user["button"] is { } button) dialog.Answer.TrySetResult(button.GetValue<string>());
                    else dialog.Answer.TrySetResult(null);
                    break;
                // region/1: the host page's reports (hosted-regions/1), which a host window's page sends its backend.
                case "declareRegion":
                    (await PageAsync(user["window"]!.GetValue<string>())).Page.Declare(user["name"]!.GetValue<string>(),
                        user["sizing"]?.GetValue<string>() == "content-height" ? HostedRegionSizing.ContentHeight : HostedRegionSizing.Fill);
                    break;
                case "removeRegion":
                    _pages[user["window"]!.GetValue<string>()].Page.Remove(user["name"]!.GetValue<string>(), user["reason"]?.GetValue<string>() ?? "removed");
                    break;
                case "resizeRegion":
                    _pages[user["window"]!.GetValue<string>()].Page.Report(user["name"]!.GetValue<string>(), region =>
                    {
                        region.Width = user["width"]!.GetValue<double>();
                        region.Height = user["height"]!.GetValue<double>();
                        region.Scale = user["scale"]!.GetValue<double>();
                        if (user["visible"] is { } visible) region.Visible = visible.GetValue<bool>();
                    });
                    break;
                case "focusRegion":
                    _pages[user["window"]!.GetValue<string>()].Page.FocusRegion(user["name"]!.GetValue<string>(), FocusSource(user["source"]!.GetValue<string>()));
                    break;
                // The host application's own decisions about a region window.
                case "departRegion":
                    var departing = Window(user["window"]!.GetValue<string>()).Region!.AsDepartureGuard();
                    _departures.Enqueue(departing.CanDepartAsync(null!, CancellationToken.None).AsTask());
                    break;
                case "detachRegion":
                    await Window(user["window"]!.GetValue<string>()).Region!.DetachAsync().WaitScaledAsync(StepTimeout);
                    break;
                case "moveRegion":
                    var target = (await PageAsync(user["to"]!.GetValue<string>())).Page.Window!;
                    await Window(user["window"]!.GetValue<string>()).Region!.MoveToAsync(target, user["name"]!.GetValue<string>()).WaitScaledAsync(StepTimeout);
                    break;
                case "focusHost":
                    _pages[user["window"]!.GetValue<string>()].Page.FocusHost(user["focused"]?.GetValue<bool>() ?? true);
                    break;
                default: throw new InvalidOperationException($"The host runner does not implement the user action '{action}'.");
            }
        }

        // The native menu bar of a window as entries of the wire format without their ids (README, Steps).
        private static JsonArray Entries(IEnumerable<DesktopMenuEntry> entries) => new([.. entries.Select(static entry => (JsonNode)(entry switch
        {
            DesktopMenu menu => new JsonObject
            {
                ["kind"] = "submenu",
                ["title"] = menu.Title,
                ["role"] = menu.Role switch
                {
                    DesktopMenuRole.Application => "application",
                    DesktopMenuRole.File => "file",
                    DesktopMenuRole.Help => "help",
                    _ => null,
                },
                ["enabled"] = menu.IsEnabled,
                ["items"] = Entries(menu.Items),
            },
            DesktopMenuItem item => new JsonObject
            {
                ["kind"] = "item",
                ["title"] = item.Title,
                ["shortcut"] = item.Shortcut?.ToString(),
                ["enabled"] = item.IsEnabled,
                ["checked"] = item.IsChecked,
            },
            _ => new JsonObject { ["kind"] = "separator" },
        }))]);

        private async Task ExpectAsync(JsonObject expect)
        {
            if (expect["menuBar"] is JsonArray menuBar)
            {
                var native = Native(expect["window"]!.GetValue<string>());
                try
                {
                    _ = await native.WaitForMenuBarAsync(bar =>
                    {
                        try
                        {
                            Match(menuBar, Entries(bar), "menuBar");
                            return true;
                        }
                        catch (InvalidOperationException) { return false; }
                    }, StepTimeout);
                }
                catch (TimeoutException)
                {
                    throw new InvalidOperationException($"The window's menu bar is {Entries(native.MenuBar).ToJsonString()}, not {menuBar.ToJsonString()}.");
                }
                return;
            }
            if (expect["region"] is JsonObject region)
            {
                var hosted = Window(expect["window"]!.GetValue<string>()).Region ?? throw new InvalidOperationException("The window shows in a window of its own.");
                JsonObject Actual() => new()
                {
                    ["state"] = hosted.State.ToString().ToLowerInvariant(),
                    ["title"] = hosted.Title,
                    ["focused"] = hosted.IsFocused,
                    ["documentEdited"] = hosted.IsDocumentEdited,
                };
                bool Matches()
                {
                    var actual = Actual();
                    return region.All(member => JsonNode.DeepEquals(member.Value, actual[member.Key]));
                }
                Check(await TestTimeouts.UntilAsync(Matches, StepTimeout), $"The region is {Actual().ToJsonString()}, not {region.ToJsonString()}.");
                return;
            }
            if (expect["page"] is JsonObject shown)
            {
                var page = _pages[expect["window"]!.GetValue<string>()].Page;
                var name = shown["name"]!.GetValue<string>();
                JsonObject Actual() => page.Find(name) is { } declared
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
                    }
                    : new JsonObject { ["name"] = name };
                bool Matches()
                {
                    var actual = Actual();
                    return shown.All(member => member.Value is JsonObject any && any["$any"] is not null
                        ? actual[member.Key] is not null
                        : JsonNode.DeepEquals(member.Value, actual[member.Key]));
                }
                Check(await TestTimeouts.UntilAsync(Matches, StepTimeout), $"The page's region is {Actual().ToJsonString()}, not {shown.ToJsonString()}.");
                return;
            }
            if (expect["departure"] is { } departure)
            {
                var allowed = await _departures.Dequeue().WaitScaledAsync(StepTimeout);
                Check(allowed == departure.GetValue<bool>(), $"The departure was {(allowed ? "allowed" : "refused")}.");
                return;
            }
            if (expect["dialogOwner"] is { } dialogOwner)
            {
                var owner = NativeWindow(dialogOwner.GetValue<string>());
                lock (_dialogOwners) Check(_dialogOwners.Count > 0 && ReferenceEquals(_dialogOwners[^1], owner), "The host's last dialogs were not created for that window.");
                return;
            }
            if (expect["closed"] is not null)
            {
                await Window(expect["window"]!.GetValue<string>()).Closed.WaitScaledAsync(StepTimeout);
                return;
            }
            if (expect["clipboard"] is { } clipboard)
            {
                var text = clipboard.GetValue<string>();
                Check(await TestTimeouts.UntilAsync(() => _platform.Clipboard.Content?.Text == text, StepTimeout),
                    $"The host's clipboard has {_platform.Clipboard.Content?.Text ?? "no text"}, not {text}.");
                return;
            }
            if (expect["launched"] is JsonArray launched)
            {
                var expected = launched.Select(item => Resolve(item)!.GetValue<string>()).ToArray();
                string[] Actual() => [.. _platform.Launches.Select(static launch => launch.Uri?.AbsoluteUri ?? launch.Path ?? "")];
                Check(await TestTimeouts.UntilAsync(() => Actual().SequenceEqual(expected), StepTimeout),
                    $"The host opened [{string.Join(", ", Actual())}], not [{string.Join(", ", expected)}].");
                return;
            }
            if (expect["started"] is not null)
            {
                _application = await _start!.WaitScaledAsync(StepTimeout);
                Check(_application.Guest.ApplicationId == "dev.runic.conformance-guest", $"The host named the guest {_application.Guest}.");
                return;
            }
            if (expect["startFailure"] is not { } code) throw new InvalidOperationException("The host runner checks start failures and starts only.");
            try
            {
                await using var application = await _start!.WaitScaledAsync(StepTimeout);
                throw new InvalidOperationException("The host started the guest.");
            }
            catch (RunicHostedApplicationException error)
            {
                Check(error.Code == code.GetValue<string>(), $"The start failed with {error.Code}: {error.Message}");
            }
        }

        private Task CommandAsync(string line) => _commands!.WriteLineAsync(line);

        // A host that already gave up, such as after a refusal line, may have ended the puppet first.
        private async Task ExitAsync(int code)
        {
            try { await CommandAsync("exit " + code.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }

        private JsonNode? Resolve(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject { Count: 1 } single when single["$secret"] is { } secret:
                    return Secret(secret.GetValue<string>());
                case JsonObject placeholder when ScenarioFiles.IsPath(placeholder):
                    return _files.Path(placeholder);
                case JsonObject { Count: 1 } single when single["$id"] is { } id:
                    // A request the runner sends gets a new id, which the host's reply then carries.
                    if (!_bindings.TryGetValue(id.GetValue<string>(), out var bound))
                        _bindings[id.GetValue<string>()] = bound = JsonValue.Create(++_nextRequest);
                    return bound?.DeepClone();
                case JsonObject value:
                    var copy = new JsonObject();
                    foreach (var (name, member) in value) copy[name] = Resolve(member);
                    return copy;
                case JsonArray list:
                    return new JsonArray([.. list.Select(Resolve)]);
                default:
                    return node?.DeepClone();
            }
        }

        private string Secret(string name) => name switch
        {
            "nonce" => HostProtocol.Base64Url(_nonce!),
            "key" => _key!,
            "host-proof" => HostProtocol.Proof(_nonce!, HostProtocol.HostProofLabel, _key!),
            "guest-proof" => HostProtocol.Proof(_nonce!, HostProtocol.GuestProofLabel, _key!),
            "guest-proof-other-nonce" => HostProtocol.Proof(RandomNumberGenerator.GetBytes(32), HostProtocol.GuestProofLabel, _key!),
            "host-proof-other-nonce" => HostProtocol.Proof(RandomNumberGenerator.GetBytes(32), HostProtocol.HostProofLabel, _key!),
            "other-key" => HostProtocol.NewSecret(),
            _ => throw new InvalidOperationException($"Unknown secret {name}."),
        };

        private void Match(JsonNode? expected, JsonNode? actual, string path)
        {
            switch (expected)
            {
                case JsonObject { Count: 1 } any when any["$any"] is { } types:
                    var names = types is JsonArray list ? list.Select(static type => type!.GetValue<string>()) : [types.GetValue<string>()];
                    Check(names.Any(type => IsType(actual, type)), $"{path}: {actual?.ToJsonString() ?? "null"} is not {types.ToJsonString()}.");
                    return;
                case JsonObject { Count: 1 } id when id["$id"] is { } name:
                    if (_bindings.TryGetValue(name.GetValue<string>(), out var bound))
                        Check(JsonNode.DeepEquals(bound, actual), $"{path}: {actual?.ToJsonString()} is not {name}'s {bound?.ToJsonString()}.");
                    else _bindings[name.GetValue<string>()] = actual?.DeepClone();
                    return;
                case JsonObject placeholder when ScenarioFiles.IsPath(placeholder):
                    var local = _files.Path(placeholder);
                    Check(actual?.GetValueKind() == JsonValueKind.String && actual.GetValue<string>() == local, $"{path}: {actual?.ToJsonString()} is not {local}.");
                    return;
                case JsonObject { Count: 1 } secret when secret["$secret"] is { } secretName:
                    Check(actual?.GetValueKind() == JsonValueKind.String && actual.GetValue<string>() == Secret(secretName.GetValue<string>()),
                        $"{path}: {actual?.ToJsonString()} is not the {secretName}.");
                    return;
                case JsonObject members:
                    Check(actual is JsonObject, $"{path}: {actual?.ToJsonString()} is not an object.");
                    var actualMembers = (JsonObject)actual!;
                    foreach (var (name, member) in members) Match(member, actualMembers[name], $"{path}.{name}");
                    Check(actualMembers.All(member => members.ContainsKey(member.Key)),
                        $"{path}: {actual!.ToJsonString()} has members that {expected.ToJsonString()} does not.");
                    return;
                case JsonArray items:
                    Check(actual is JsonArray { Count: var count } && count == items.Count, $"{path}: {actual?.ToJsonString()} is not {expected.ToJsonString()}.");
                    for (var index = 0; index < items.Count; index++) Match(items[index], ((JsonArray)actual!)[index], $"{path}[{index}]");
                    return;
                default:
                    Check(JsonNode.DeepEquals(expected, actual), $"{path}: {actual?.ToJsonString() ?? "null"} is not {expected?.ToJsonString() ?? "null"}.");
                    return;
            }
        }

        private static bool IsType(JsonNode? node, string type) => (type, node?.GetValueKind()) switch
        {
            ("null", null or JsonValueKind.Null) => true,
            ("string", JsonValueKind.String) => true,
            ("boolean", JsonValueKind.True or JsonValueKind.False) => true,
            ("object", JsonValueKind.Object) => true,
            ("array", JsonValueKind.Array) => true,
            ("number", JsonValueKind.Number) => true,
            ("integer", JsonValueKind.Number) => node!.GetValue<JsonElement>().TryGetInt64(out _),
            _ => false,
        };

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _connections.Values) socket.Dispose();
            foreach (var client in _clients) client.Dispose();
            _listener?.Stop();
            if (_start is { IsCompletedSuccessfully: true } started)
            {
                // The runner's guest never closes: end the host's session and the puppet with it.
                _ = await started.Result.KillAsync().AsTask().WaitScaledAsync(StepTimeout);
            }
            _puppet?.Dispose();
            _control.Stop();
            foreach (var page in _pages.Values) await page.Surface.DisposeAsync();
            if (_fixture is not null) await _fixture.DisposeAsync();
            _dialogsInstalled?.Dispose();
            await _platform.DisposeAsync();
            _files.Dispose();
        }
    }

    // The host's policy of a scenario: the defaults, within the scenario's limits; with failsServices it throws for every
    // window service, as a policy with a bug does.
    // The scenario's policy: the defaults, or one that fails for window services, or one that asks the user before each
    // launch in a prompt of the host's own (prompts-launches), which a user step answers with the button allow or deny.
    // With embed, the guest's windows that can show in a region and have no owner show in the region of that name
    // (region/1).
    private sealed class ScenarioPolicy(RunicHostedLimits limits, bool failsServices,
        Func<CancellationToken, Task<object?>>? prompt = null, string? embed = null, RunicHostedRegionOptions? embedOptions = null,
        bool embedComponent = false, bool limitsPlacement = false) : IRunicHostedWindowPolicy
    {
        public RunicHostedLimits Limits => limits;

        // limits-placement: maximize is refused, and a placement's size clamped to 1,200 by 900.
        public RunicHostedPresentationDecision Present(RunicHostedPresentationRequest request) => !limitsPlacement
            ? RunicHostedPresentationDecision.Allow()
            : request.Kind switch
            {
                RunicHostedPresentationKind.Maximize => RunicHostedPresentationDecision.Refuse("The host keeps its windows unmaximized."),
                RunicHostedPresentationKind.SetPlacement when request.Placement is { } placement => RunicHostedPresentationDecision.Apply(request with
                {
                    Placement = placement with { Width = Math.Min(placement.Width, 1_200), Height = Math.Min(placement.Height, 900) },
                }),
                _ => RunicHostedPresentationDecision.Allow(),
            };

        public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(embed is not null && request.CanEmbed && request.Owner is null
                // With embedComponent, also for a guest that does not allow it, whose open then fails.
                ? embedComponent
                    ? RunicHostedWindowDecision.EmbedComponent(embed, embedOptions ?? RunicHostedRegionOptions.Default)
                    : RunicHostedWindowDecision.Embed(embed, embedOptions ?? RunicHostedRegionOptions.Default)
                : RunicHostedWindowDecision.Allow());

        public ValueTask<RunicHostedServiceDecision> ServiceAsync(RunicHostedServiceRequest request, CancellationToken cancellationToken) =>
            failsServices
                ? throw new InvalidOperationException("The scenario's policy fails.")
                : prompt is not null && request.Kind == RunicHostedServiceKind.Launch
                    ? new(AskAsync(request, prompt, cancellationToken))
                    : ValueTask.FromResult(Default(request));

        private static async Task<RunicHostedServiceDecision> AskAsync(RunicHostedServiceRequest request, Func<CancellationToken, Task<object?>> prompt,
            CancellationToken cancellationToken) =>
            await prompt(cancellationToken) as string == "allow" ? Default(request) : RunicHostedServiceDecision.Refuse();

        private static RunicHostedServiceDecision Default(RunicHostedServiceRequest request) =>
            request.IsAllowedByDefault ? RunicHostedServiceDecision.Allow() : RunicHostedServiceDecision.Refuse();
    }

    // The host's user: each context menu and dialog the host shows waits until a user step answers it, or until the host
    // closes it (its token).
    private sealed class HostUser
    {
        internal sealed record Shown(object What, string Kind, TaskCompletionSource<object?> Answer, CancellationToken Closed);

        private readonly System.Threading.Channels.Channel<Shown> _menus = System.Threading.Channels.Channel.CreateUnbounded<Shown>();
        private readonly System.Threading.Channels.Channel<Shown> _dialogs = System.Threading.Channels.Channel.CreateUnbounded<Shown>();
        // What the host shows that no user step answered yet, in the order it showed them.
        private readonly List<Shown> _shownMenus = [];
        private readonly List<Shown> _shownDialogs = [];

        public async Task<DesktopMenuItem?> ContextMenuAsync(ImmutableArray<DesktopMenuEntry> entries, DesktopContextMenuOptions options, CancellationToken cancellationToken)
        {
            var shown = new Shown(entries, "context-menu", new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            await _menus.Writer.WriteAsync(shown, CancellationToken.None);
            return (DesktopMenuItem?)await shown.Answer.Task.WaitAsync(cancellationToken);
        }

        // A prompt the host's policy shows itself (host-prompt), answered with a button.
        public async Task<object?> PromptAsync(CancellationToken cancellationToken)
        {
            var shown = new Shown("host-prompt", "host-prompt", new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            await _dialogs.Writer.WriteAsync(shown, CancellationToken.None);
            return await shown.Answer.Task.WaitAsync(cancellationToken);
        }

        public async Task<object?> DialogAsync(FakeHostDialog dialog, CancellationToken cancellationToken)
        {
            var kind = dialog.Kind switch
            {
                RunicHostedDialogKind.OpenFile => "open",
                RunicHostedDialogKind.OpenFiles => "open-multiple",
                RunicHostedDialogKind.SaveFile => "save",
                RunicHostedDialogKind.OpenFolder => "folder",
                RunicHostedDialogKind.OpenFolders => "folder-multiple",
                _ => "message-box",
            };
            var shown = new Shown(dialog, kind, new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            await _dialogs.Writer.WriteAsync(shown, CancellationToken.None);
            return await shown.Answer.Task.WaitAsync(cancellationToken);
        }

        // Waits until the host closes the oldest dialog (of the kind, when given) that a step held open.
        public async Task AwaitClosedAsync(string? kind, TimeSpan timeout)
        {
            var shown = _shownDialogs.FirstOrDefault(shown => kind is null || shown.Kind == kind)
                ?? throw new InvalidOperationException($"No {kind ?? "dialog"} is held open.");
            try { await Task.Delay(Timeout.Infinite, shown.Closed).WaitScaledAsync(timeout); }
            catch (OperationCanceledException) when (shown.Closed.IsCancellationRequested) { }
            catch (TimeoutException) { throw new InvalidOperationException($"The host did not close the {shown.Kind}."); }
            _shownDialogs.Remove(shown);
        }

        // Waits until the host shows a context menu or dialog (of the kind, when given) that no step answered yet; it stays
        // open for a later answer.
        public async Task HoldAsync(bool menu, string? kind, TimeSpan timeout) => _ = await FindAsync(menu, kind, timeout);

        // The oldest context menu or dialog (of the kind, when given) that the host shows and no step answered yet.
        public async Task<Shown> TakeAsync(bool menu, string? kind, TimeSpan timeout)
        {
            var shown = await FindAsync(menu, kind, timeout);
            (menu ? _shownMenus : _shownDialogs).Remove(shown);
            return shown;
        }

        private async Task<Shown> FindAsync(bool menu, string? kind, TimeSpan timeout)
        {
            var list = menu ? _shownMenus : _shownDialogs;
            using var deadline = TestTimeouts.Cancellation(timeout);
            while (true)
            {
                // One the host already closed is gone.
                list.RemoveAll(static shown => shown.Closed.IsCancellationRequested);
                if (list.FirstOrDefault(shown => kind is null || shown.Kind == kind) is { } found) return found;
                try { list.Add(await (menu ? _menus : _dialogs).Reader.ReadAsync(deadline.Token)); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException($"The host showed no {(menu ? "context menu" : kind ?? "dialog")}; it shows [{string.Join(", ", list.Select(static shown => shown.Kind))}].");
                }
            }
        }

        public static DesktopMenuItem? Find(IEnumerable<DesktopMenuEntry> entries, string title)
        {
            foreach (var entry in entries)
            {
                if (entry is DesktopMenuItem item && item.Title == title) return item;
                if (entry is DesktopMenu menu && Find(menu.Items, title) is { } found) return found;
            }
            return null;
        }
    }
}
