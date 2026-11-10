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
        private RunicHostedApplication? _application;
        private long _nextRequest = 1000;
        private byte[]? _nonce;
        private string? _key;

        private JsonObject Limits => scenario["arrange"]?["limits"] as JsonObject ?? [];

        private TimeSpan Limit(string name, TimeSpan fallback) =>
            Limits[name] is { } value ? TimeSpan.FromMilliseconds(value.GetValue<int>()) : fallback;

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
                case "advance": await Task.Delay(Limit(value!.GetValue<string>(), StepTimeout)); break;
                case "expect": await ExpectAsync(value!.AsObject()); break;
                case "user": await UserAsync(value!.AsObject()); break;
                default: throw new InvalidOperationException($"The host runner does not implement the step '{action}'.");
            }
        }

        private async Task StartAsync()
        {
            _control.Start();
            _fixture = await HostFixture.StartAsync();
            _fixture.Applications.WindowOpened += (_, e) => { lock (_opened) _opened.Add(e.Window); };
            _fixture.Factory.ContextMenu = _user.ContextMenuAsync;
            _dialogs.Answer = _user.DialogAsync;
            _dialogsInstalled = _dialogs.Install();
            var host = scenario["arrange"]!["host"]!;
            var start = HostedGuestProgram.StartInfo(HostedGuestProgram.PuppetArgument) with
            {
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
                // The scenario's menu rates, dialogs per guest and dialog rate, or the protocol's defaults.
                Policy = new ScenarioPolicy(new RunicHostedLimits
                {
                    MenuRequestsPerSecond = Limits["menuRequests"]?.GetValue<int>() ?? RunicHostedLimits.Default.MenuRequestsPerSecond,
                    MaximumDialogs = Limits["dialogs"]?.GetValue<int>() ?? RunicHostedLimits.Default.MaximumDialogs,
                    GuestMenuRequestsPerSecond = Limits["guestMenuRequests"]?.GetValue<int>() ?? RunicHostedLimits.Default.GuestMenuRequestsPerSecond,
                    DialogsPerTenSeconds = Limits["dialogRate"]?.GetValue<int>() ?? RunicHostedLimits.Default.DialogsPerTenSeconds,
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

        private HostedWindowHost Native(string id)
        {
            int index;
            lock (_opened) index = _opened.FindLastIndex(window => window.Id == id);
            if (index < 0) throw new InvalidOperationException($"The host opened no window {id}.");
            return _fixture!.Factory.Hosts[index];
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
                        Check(native.ChoosePrevious(titles),
                            $"The window's previous menu bar has no enabled item {string.Join(" > ", titles)}: {HostedWindowHost.Describe(native.PreviousMenuBar)}.");
                        break;
                    }
                    Check(native.Choose(titles), $"The window's menu bar has no enabled item {string.Join(" > ", titles)}: {HostedWindowHost.Describe(native.MenuBar)}.");
                    break;
                case "deactivateWindow" or "activateWindow":
                    Native(user["window"]!.GetValue<string>()).SetActive(action == "activateWindow");
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
            if (expect["closed"] is not null)
            {
                await Window(expect["window"]!.GetValue<string>()).Closed.WaitScaledAsync(StepTimeout);
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
            if (_fixture is not null) await _fixture.DisposeAsync();
            _dialogsInstalled?.Dispose();
            _files.Dispose();
        }
    }

    // The host's policy of a scenario: it allows everything, within the scenario's limits.
    private sealed class ScenarioPolicy(RunicHostedLimits limits) : IRunicHostedWindowPolicy
    {
        public RunicHostedLimits Limits => limits;
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
