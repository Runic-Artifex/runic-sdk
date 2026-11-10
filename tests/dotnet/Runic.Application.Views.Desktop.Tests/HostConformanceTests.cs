using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// The guest runner of the Runic host protocol corpus (specs/desktop/host-conformance): for every scenario of the guest
// role it plays the host against RunicDesktopHost.Run in hosted mode, in this process, with the corpus's test
// application (two Window types, ConformanceMainWindow and ConformanceGuardedWindow, and the menus and commands of
// ConformanceMenus). The guest's standard input and output are pipes of the runner; the host channel is a real loopback
// WebSocket.
internal sealed class HostConformanceTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    public static IEnumerable<string> GuestScenarios() =>
        Directory.EnumerateFiles(Corpus, "*.json", SearchOption.AllDirectories)
            .Select(file => JsonNode.Parse(File.ReadAllText(file))!.AsObject())
            .Where(scenario => scenario["roles"]!.AsArray().Any(role => role!.GetValue<string>() == "guest"))
            .Select(scenario => scenario["id"]!.GetValue<string>())
            .Order(StringComparer.Ordinal);

    // specs/desktop/host-conformance/scenarios, copied next to the suite by its project.
    internal static string Corpus => Path.Combine(AppContext.BaseDirectory, "host-conformance");

    [Test, NotInParallel(Order = 40)]
    [MethodDataSource(nameof(GuestScenarios))]
    [DisplayName("Guest($id)")]
    public async Task Guest(string id)
    {
        var file = Directory.EnumerateFiles(Corpus, "*.json", SearchOption.AllDirectories)
            .Single(path => JsonNode.Parse(File.ReadAllText(path))!["id"]!.GetValue<string>() == id);
        await using var runner = new GuestRunner(JsonNode.Parse(File.ReadAllText(file))!.AsObject());
        await runner.RunAsync();
    }

    // Runs one scenario: the runner is the host.
    private sealed class GuestRunner(JsonObject scenario) : IAsyncDisposable
    {
        private readonly AnonymousPipeServerStream _input = new(PipeDirection.Out);
        private readonly LineWriter _output = new();
        private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonNode?> _bindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<object?>> _actions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<IRunicWindow>> _windows = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CancellationTokenSource> _openCancellations = new(StringComparer.Ordinal);
        // Each window's dialogs and context menus take this token until a cancelDialog step cancels it.
        private readonly Dictionary<string, CancellationTokenSource> _dialogCancellations = new(StringComparer.Ordinal);
        private readonly ConformanceMenus _menus = new();
        private readonly ScenarioFiles _files = new();
        private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
        private readonly ConcurrentQueue<int> _exits = new();
        private readonly TaskCompletionSource<RunicDesktopHost> _host = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ServiceProvider? _provider;
        private Task<int>? _run;
        private Uri? _channel;
        private string? _key;
        private long _nextRequest;
        private bool _inputClosed;

        private JsonObject Limits => scenario["arrange"]?["limits"] as JsonObject ?? [];

        public async Task RunAsync()
        {
            var steps = scenario["steps"]!.AsArray();
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index]!.AsObject();
                try { await StepAsync(step); }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw new InvalidOperationException(
                        $"{scenario["id"]}: step {index} {step.ToJsonString()} failed: {error.Message}\nGuest output:\n{_output.All}", error);
                }
            }
            // Every message of every open connection was matched.
            foreach (var (name, connection) in _connections)
                if (connection.TryPeekMessage(out var left))
                    throw new InvalidOperationException($"{scenario["id"]}: {name} has an unmatched message: {left!.ToJsonString()}");
        }

        private async Task StepAsync(JsonObject step)
        {
            if (step["start"] is { } start) Start(start);
            else if (step["line"] is { } line) await LineAsync(line);
            else if (step["connect"] is { } connect) await ConnectAsync(connect.GetValue<string>(), step);
            else if (step["send"] is JsonObject send) await _connections[Name(step["connection"])].SendAsync(Resolve(send)!.AsObject());
            else if (step["sendRaw"] is JsonObject raw) await _connections[Name(step["connection"])].SendRawAsync(raw);
            else if (step["receive"] is JsonObject receive) await ReceiveAsync(Name(step["connection"]), receive);
            else if (step["receiveAll"] is JsonArray all) await ReceiveAllAsync(Name(step["connection"]), all);
            else if (step["closed"] is JsonObject closed) await ClosedAsync(closed);
            else if (step["disconnect"] is { } disconnect) await _connections[disconnect.GetValue<string>()].DisposeAsync();
            else if (step["closeInput"] is not null) CloseInput();
            else if (step["advance"] is { } advance)
                await Task.Delay(TimeSpan.FromMilliseconds(Limits[advance.GetValue<string>()]!.GetValue<int>()) + TimeSpan.FromMilliseconds(500));
            else if (step["guest"] is JsonObject guest) await GuestAsync(guest, step["as"]?.GetValue<string>());
            else if (step["expect"] is JsonObject expect) await ExpectAsync(expect);
            else throw new InvalidOperationException("Unknown step.");
        }

        private static string Name(JsonNode? node) => node!.GetValue<string>();

        // ----- The guest under test

        private void Start(JsonNode start)
        {
            var application = scenario["arrange"]!["application"]!.AsObject();
            // Each scenario starts with a guard that allows at once.
            ConformanceGuardedWindow.Reset();
            TimeSpan Limit(string name, TimeSpan fallback) =>
                Limits[name] is { } value ? TimeSpan.FromMilliseconds(value.GetValue<int>()) : fallback;
            var guest = RunicGuestOptions.FromStreams(new AnonymousPipeClientStream(PipeDirection.In, _input.ClientSafePipeHandle), _output) with
            {
                ApplicationName = application["name"]!.GetValue<string>(),
                ApplicationVersion = application["version"]!.GetValue<string>(),
                ApplicationId = application["applicationId"]?.GetValue<string>(),
                StartTimeout = Limit("startTimeout", HostProtocol.DefaultStartTimeout),
                UnauthenticatedTimeout = Limit("unauthenticatedTimeout", HostProtocol.UnauthenticatedTimeout),
                HelloTimeout = Limit("helloTimeout", HostProtocol.HelloTimeout),
                RequestTimeout = Limit("requestTimeout", HostProtocol.RequestTimeout),
                MaximumWaitingRequests = Limits["waitingRequests"]?.GetValue<int>() ?? HostProtocol.MaximumWaitingRequests,
                ExitProcess = code => _exits.Enqueue(code),
                CheckPrivileges = static () => null,
            };
            var services = new ServiceCollection();
            // The Windows' IWindowPlatformServices: their context menus, file dialogs and message boxes.
            services.AddRunicPlatformServices();
            services.AddScoped<OrderedCollectionModel>();
            services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
                _ => (transport, model) => new OrderedCollectionBridge(transport, model));
            _provider = services.BuildServiceProvider();
            var provider = _provider;
            _run = Task.Factory.StartNew(() => RunicDesktopHost.Run(provider, new DesktopHostOptions { WaitForConnection = false },
                new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                    Guest = guest,
                },
                async host =>
                {
                    // The test application's menu bar and its commands (README, Test application).
                    host.Desktop.SetMenuBar(ConformanceMenus.Bar(german: false));
                    using var menu = host.UseApplicationMenu(_menus.Commands);
                    _host.TrySetResult(host);
                    try { await Task.Delay(Timeout.Infinite, host.Desktop.ShutdownRequested); }
                    catch (OperationCanceledException) { }
                    return 0;
                }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (start is JsonValue) return; // "none": no offer.
            var line = start["offerLine"]?.GetValue<string>() ?? HostProtocol.OfferPrefix + Resolve(start["offer"])!.ToJsonString();
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _input.Write(bytes);
            _input.Flush();
        }

        private void CloseInput()
        {
            if (_inputClosed) return;
            _inputClosed = true;
            _input.Dispose();
        }

        private async Task LineAsync(JsonNode expected)
        {
            if (expected is JsonValue)
            {
                // "none": the guest exits without writing a launch line.
                await WaitForRunAsync();
                Check(!_output.All.Contains(HostProtocol.ChannelPrefix, StringComparison.Ordinal) &&
                      !_output.All.Contains(HostProtocol.RefusedPrefix, StringComparison.Ordinal),
                    "The guest wrote a launch line.");
                return;
            }
            var line = await _output.NextAsync(text =>
                text.StartsWith(HostProtocol.ChannelPrefix, StringComparison.Ordinal) ||
                text.StartsWith(HostProtocol.RefusedPrefix, StringComparison.Ordinal), TestTimeouts.Scale(StepTimeout));
            if (expected["channel"] is JsonObject channel)
            {
                var content = HostProtocol.ParseLine(line, HostProtocol.ChannelPrefix)
                    ?? throw new InvalidOperationException($"Expected a channel line, got '{line}'.");
                Match(channel, content, "line");
                var read = HostProtocol.ReadChannel(content) ?? throw new InvalidOperationException($"The channel line is not valid: {line}");
                _channel = read.Url;
                _key = read.Key;
            }
            else
            {
                var content = HostProtocol.ParseLine(line, HostProtocol.RefusedPrefix)
                    ?? throw new InvalidOperationException($"Expected a refusal line, got '{line}'.");
                Match(expected["refused"]!, content, "line");
            }
        }

        private async Task WaitForRunAsync()
        {
            try { await _run!.WaitScaledAsync(StepTimeout); }
            catch (RunicHostedApplicationException) { }
        }

        private async Task GuestAsync(JsonObject guest, string? name)
        {
            var host = await _host.Task.WaitScaledAsync(StepTimeout);
            var action = guest["action"]!.GetValue<string>();
            Task<IRunicWindow> Window() => _windows[guest["window"]!.GetValue<string>()];
            Task<object?> result;
            switch (action)
            {
                case "open":
                    var owner = guest["owner"] is { } ownerName ? await _windows[ownerName.GetValue<string>()] : null;
                    var options = owner is null ? null : new RunicWindowOptions
                    {
                        Owner = owner,
                        Modality = guest["modality"]?.GetValue<string>() == "window" ? RunicWindowModality.Window : RunicWindowModality.None,
                    };
                    var cancellation = new CancellationTokenSource();
                    _openCancellations[guest["window"]!.GetValue<string>()] = cancellation;
                    Task<IRunicWindow> opening = guest["type"]!.GetValue<string>() == "Guarded"
                        ? Open<ConformanceGuardedWindow>(host, options, cancellation.Token)
                        : Open<ConformanceMainWindow>(host, options, cancellation.Token);
                    _windows[guest["window"]!.GetValue<string>()] = opening;
                    result = Run(async () => (object?)await opening);
                    break;
                case "close":
                    result = Run(async () => { _ = await (await Window()).CloseAsync(TimeSpan.Zero); return null; });
                    break;
                case "requestClose":
                    result = Run(async () => (object?)await (await Window()).RequestCloseAsync());
                    break;
                case "present": result = Present(async window => await window.Presentation.PresentAsync()); break;
                case "minimize": result = Present(async window => await window.Presentation.MinimizeAsync()); break;
                case "setVisible": result = Present(async window => await window.Presentation.SetVisibleAsync(guest["value"]!.GetValue<bool>())); break;
                case "setFullScreen": result = Present(async window => await window.Presentation.SetFullScreenAsync(guest["value"]!.GetValue<bool>())); break;
                case "setDocumentEdited": result = Present(async window => await window.Presentation.SetDocumentEditedAsync(guest["value"]!.GetValue<bool>())); break;
                case "setColorScheme":
                    var scheme = guest["value"]!.GetValue<string>() switch
                    {
                        "light" => DesktopColorScheme.Light,
                        "dark" => DesktopColorScheme.Dark,
                        _ => DesktopColorScheme.NoPreference,
                    };
                    result = Present(async window => await window.Presentation.SetColorSchemeAsync(scheme));
                    break;
                case "cancelOpen":
                    await _openCancellations[guest["window"]!.GetValue<string>()].CancelAsync();
                    result = Task.FromResult<object?>(null);
                    break;
                case "answerGuard":
                    ConformanceGuardedWindow.Decide(guest["allow"]!.GetValue<bool>());
                    result = Task.FromResult<object?>(null);
                    break;
                case "holdGuard":
                    ConformanceGuardedWindow.Hold();
                    result = Task.FromResult<object?>(null);
                    break;
                case "activity":
                    result = Run(async () => (object?)(await Window()).Presentation);
                    break;
                case "setMenuBar":
                    var menu = guest["menu"]!.GetValue<string>();
                    host.Desktop.SetMenuBar(menu == "none" ? [] : ConformanceMenus.Bar(german: menu == "german"));
                    result = Task.FromResult<object?>(null);
                    break;
                case "setCommandEnabled":
                    _menus.Command(guest["command"]!.GetValue<string>()).SetEnabled(guest["value"]!.GetValue<bool>());
                    result = Task.FromResult<object?>(null);
                    break;
                case "invoked":
                    result = Task.FromResult<object?>(_menus);
                    break;
                case "contextMenu":
                    var point = guest["point"] as JsonObject;
                    result = Dialog(async (services, token) => Normalize(await services.ShowContextMenuAsync(ConformanceMenus.ContextMenu,
                        point is null ? null : new DesktopContextMenuOptions { X = point["x"]!.GetValue<double>(), Y = point["y"]!.GetValue<double>() },
                        token)));
                    break;
                case "openFile":
                    result = Dialog(async (services, token) => await NormalizeAsync(await services.FileDialogs.OpenFileAsync(ConformanceDialogs.Open, token),
                        static lease => [lease.DisplayName]));
                    break;
                case "openFiles":
                    result = Dialog(async (services, token) => await NormalizeAsync(await services.FileDialogs.OpenFilesAsync(ConformanceDialogs.Open, token),
                        static list => list.Select(static lease => lease.DisplayName)));
                    break;
                case "saveFile":
                    result = Dialog(async (services, token) => await NormalizeAsync(await services.FileDialogs.SaveFileAsync(ConformanceDialogs.Save, token),
                        static lease => [lease.DisplayName]));
                    break;
                case "openFolder":
                    result = Dialog(async (services, token) => await NormalizeAsync(await services.FileDialogs.OpenDirectoryAsync(ConformanceDialogs.Folder, token),
                        static lease => [lease.DisplayName]));
                    break;
                case "messageBox":
                    result = Dialog(async (services, token) => Normalize(await services.MessageBoxes.ShowAsync(ConformanceDialogs.MessageBox, token)));
                    break;
                case "cancelDialog":
                    if (_dialogCancellations.Remove(guest["window"]!.GetValue<string>(), out var dialogCancellation))
                    {
                        await dialogCancellation.CancelAsync();
                        dialogCancellation.Dispose();
                    }
                    result = Task.FromResult<object?>(null);
                    break;
                default: throw new InvalidOperationException($"Unknown action {action}.");
            }
            if (name is not null) _actions[name] = result;

            Task<object?> Present(Func<IRunicWindow, Task<PlatformResult<PlatformUnit>>> call) =>
                Run(async () => (object?)await call(await Window()));

            // A dialog or context menu of the window's IWindowPlatformServices, with the window's dialog token as it is now.
            Task<object?> Dialog(Func<IWindowPlatformServices, CancellationToken, Task<JsonObject>> call)
            {
                var windowName = guest["window"]!.GetValue<string>();
                if (!_dialogCancellations.TryGetValue(windowName, out var cancellation))
                    _dialogCancellations[windowName] = cancellation = new CancellationTokenSource();
                var token = cancellation.Token;
                return Run(async () => (object?)await call((await Window()).Services.GetRequiredService<IWindowPlatformServices>(), token));
            }
        }

        // The results of the dialog and context menu actions as the README's expect members: selected, dismissed, chosen,
        // button or outcome.
        private static JsonObject OutcomeOf(string kind, string token) => new() { ["outcome"] = new JsonObject { [kind] = token } };

        private static JsonObject Normalize(PlatformResult<DesktopMenuItem?> result) => result switch
        {
            PlatformResult<DesktopMenuItem?>.Success(var item) => new JsonObject { ["chosen"] = item?.CommandId },
            PlatformResult<DesktopMenuItem?>.Unavailable(var reason) => OutcomeOf("unavailable", HostProtocol.Kebab(reason.ToString())),
            PlatformResult<DesktopMenuItem?>.Failed(var code, _) => OutcomeOf("failed", HostProtocol.Kebab(code.ToString())),
            _ => throw new InvalidOperationException($"Unknown result {result}."),
        };

        private static JsonObject Normalize(PlatformResult<DesktopMessageBoxButton?> result) => result switch
        {
            PlatformResult<DesktopMessageBoxButton?>.Success(var button) => new JsonObject { ["button"] = button?.Id },
            PlatformResult<DesktopMessageBoxButton?>.Unavailable(var reason) => OutcomeOf("unavailable", HostProtocol.Kebab(reason.ToString())),
            PlatformResult<DesktopMessageBoxButton?>.Failed(var code, _) => OutcomeOf("failed", HostProtocol.Kebab(code.ToString())),
            _ => throw new InvalidOperationException($"Unknown result {result}."),
        };

        // A selection is reported by its leases' display names, and released.
        private static async Task<JsonObject> NormalizeAsync<T>(PickerResult<T> result, Func<T, IEnumerable<string>> names) where T : IAsyncDisposable
        {
            switch (result)
            {
                case PickerResult<T>.Selected(var value):
                    try { return new JsonObject { ["selected"] = new JsonArray([.. names(value).Select(static name => (JsonNode)name)]) }; }
                    finally { await value.DisposeAsync(); }
                case PickerResult<T>.Dismissed:
                    return new JsonObject { ["dismissed"] = true };
                case PickerResult<T>.Unavailable(var reason):
                    return OutcomeOf("unavailable", HostProtocol.Kebab(reason.ToString()));
                case PickerResult<T>.Failed(var code, _):
                    return OutcomeOf("failed", HostProtocol.Kebab(code.ToString()));
                default:
                    throw new InvalidOperationException($"Unknown result {result}.");
            }
        }

        private static Task<object?> Run(Func<Task<object?>> work) => Task.Run(work);

        private static async Task<IRunicWindow> Open<TWindow>(RunicDesktopHost host, RunicWindowOptions? options, CancellationToken cancellationToken)
            where TWindow : class, IRunicWindow, new() =>
            await host.OpenWindowAsync<TWindow>(options, null, cancellationToken);

        private async Task ExpectAsync(JsonObject expect)
        {
            if (expect["action"] is { } actionName)
            {
                var action = _actions[actionName.GetValue<string>()];
                if (expect["cancelled"] is not null)
                {
                    try
                    {
                        await action.WaitScaledAsync(StepTimeout);
                        throw new InvalidOperationException("The action was not cancelled.");
                    }
                    catch (OperationCanceledException) { }
                    return;
                }
                if (expect["failed"] is { } failed)
                {
                    try
                    {
                        await action.WaitScaledAsync(StepTimeout);
                        throw new InvalidOperationException("The action succeeded.");
                    }
                    catch (RunicWindowConfigurationException error)
                    {
                        Check(error.Diagnostics.Any(diagnostic => diagnostic.Code == failed.GetValue<string>()),
                            $"The open failed with {string.Join(", ", error.Diagnostics.Select(d => d.Code))}.");
                    }
                    return;
                }
                var result = await action.WaitScaledAsync(StepTimeout);
                if (result is JsonObject normalized)
                {
                    // A dialog or context menu: its result is one of the members the README defines.
                    var expected = expect.DeepClone().AsObject();
                    expected.Remove("action");
                    Check(JsonNode.DeepEquals(Resolve(expected), normalized), $"The result is {normalized.ToJsonString()}, not {expected.ToJsonString()}.");
                    return;
                }
                if (expect["invoked"] is JsonArray invoked)
                {
                    var menus = (ConformanceMenus)result!;
                    var commands = invoked.Select(static command => command!.GetValue<string>()).ToArray();
                    Check(await TestTimeouts.UntilAsync(() => menus.Invoked.SequenceEqual(commands), StepTimeout),
                        $"The application ran [{string.Join(", ", menus.Invoked)}], not [{string.Join(", ", commands)}].");
                    return;
                }
                if (expect["closed"] is { } closed)
                    Check(result is bool value && value == closed.GetValue<bool>(), $"RequestCloseAsync returned {result}.");
                if (expect["outcome"] is JsonObject outcome) CheckOutcome(outcome, (PlatformResult<PlatformUnit>)result!);
                if (expect["activity"] is JsonObject activity)
                {
                    var presentation = (IRunicWindowPresentation)result!;
                    var expected = new RunicWindowActivity(activity["active"]!.GetValue<bool>(),
                        activity["minimized"]!.GetValue<bool>(), activity["visible"]!.GetValue<bool>());
                    Check(await TestTimeouts.UntilAsync(() => Equals(presentation.Activity, expected), StepTimeout),
                        $"The activity is {presentation.Activity}, not {expected}.");
                }
                return;
            }
            if (expect["startFailure"] is { } code)
            {
                try
                {
                    await _run!.WaitScaledAsync(StepTimeout);
                    throw new InvalidOperationException("The hosted start succeeded.");
                }
                catch (RunicHostedApplicationException error)
                {
                    Check(error.Code == code.GetValue<string>(), $"The hosted start failed with {error.Code}: {error.Message}");
                }
                return;
            }
            if (expect["exited"] is { } exited)
            {
                try
                {
                    var exitCode = await _run!.WaitScaledAsync(StepTimeout);
                    if (exited is JsonValue value && value.TryGetValue<int>(out var expectedCode))
                        Check(exitCode == expectedCode, $"Run returned {exitCode}.");
                }
                catch (RunicHostedApplicationException) when (exited is JsonValue value && value.GetValueKind() == JsonValueKind.True) { }
                Check(_exits.IsEmpty, "The guest ended its process instead of returning from Run.");
                return;
            }
            if (expect["running"] is not null)
            {
                Check(!_run!.IsCompleted && _exits.IsEmpty, "The guest is no longer running.");
                return;
            }
            throw new InvalidOperationException("Unknown expectation.");
        }

        private static void CheckOutcome(JsonObject expected, PlatformResult<PlatformUnit> actual)
        {
            var matches = (expected.First().Key, actual) switch
            {
                ("success", PlatformResult<PlatformUnit>.Success) => true,
                ("unavailable", PlatformResult<PlatformUnit>.Unavailable unavailable) =>
                    HostProtocol.Kebab(unavailable.Reason.ToString()) == expected["unavailable"]!.GetValue<string>(),
                ("failed", PlatformResult<PlatformUnit>.Failed failed) =>
                    HostProtocol.Kebab(failed.Code.ToString()) == expected["failed"]!.GetValue<string>(),
                _ => false,
            };
            Check(matches, $"The outcome is {actual}, not {expected.ToJsonString()}.");
        }

        // ----- Connections

        private async Task ConnectAsync(string name, JsonObject step)
        {
            var url = _channel ?? throw new InvalidOperationException("No channel line was read.");
            var host = step["host"]?.GetValue<string>().Replace("{port}", url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                ?? url.Authority;
            var path = step["path"]?.GetValue<string>() ?? url.AbsolutePath;
            var headers = step["headers"] as JsonObject;
            var expectedStatus = step["status"]?.GetValue<int>() ?? 101;
            var refused = step["refused"] is not null;
            var client = new TcpClient(url.HostNameType == UriHostNameType.IPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            try
            {
                try { await client.ConnectAsync(url.IdnHost.Trim('[', ']'), url.Port); }
                catch (SocketException) when (refused) { client.Dispose(); return; }
                var stream = client.GetStream();
                var webSocketKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                var request = new StringBuilder()
                    .Append("GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n")
                    .Append("Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: " + webSocketKey + "\r\n");
                foreach (var (header, value) in headers ?? []) request.Append(header + ": " + value!.GetValue<string>() + "\r\n");
                request.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));
                var (status, accept) = await ReadStatusAsync(stream);
                if (refused)
                {
                    Check(status is null, $"The connection was answered with {status}.");
                    client.Dispose();
                    return;
                }
                Check(status == expectedStatus, $"The upgrade was answered with {(status is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "nothing")}, not {expectedStatus}.");
                if (status != 101)
                {
                    client.Dispose();
                    return;
                }
                // RFC 6455: the key with the protocol's GUID, hashed with SHA-1.
#pragma warning disable CA5350 // RFC 6455 defines Sec-WebSocket-Accept with SHA-1.
                var expectedAccept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
                Check(accept == expectedAccept, $"Sec-WebSocket-Accept is {accept}, not {expectedAccept}.");
                var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = TimeSpan.Zero });
                _connections[name] = new Connection(client, socket);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        // The upgrade response's status and Sec-WebSocket-Accept, after its headers; no status when the connection closed first.
        private static async Task<(int? Status, string? Accept)> ReadStatusAsync(NetworkStream stream)
        {
            var header = new List<byte>();
            var one = new byte[1];
            using var timeout = TestTimeouts.Cancellation(StepTimeout);
            while (header.Count < 8192)
            {
                int read;
                try { read = await stream.ReadAsync(one, timeout.Token); }
                catch (IOException) { return (null, null); }
                if (read == 0) return (null, null);
                header.Add(one[0]);
                if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
            }
            var lines = Encoding.ASCII.GetString([.. header]).Split("\r\n");
            var accept = lines.FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Accept:", StringComparison.OrdinalIgnoreCase))?[21..].Trim();
            return (int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture), accept);
        }

        private async Task ReceiveAsync(string name, JsonObject expected)
        {
            var message = await _connections[name].NextAsync(TestTimeouts.Scale(StepTimeout))
                ?? throw new InvalidOperationException($"{name} closed instead of sending {expected.ToJsonString()}.");
            Match(expected, message, "message");
        }

        // The next messages, as many as expected, in any order: each matches one expected message.
        private async Task ReceiveAllAsync(string name, JsonArray expected)
        {
            var messages = new List<JsonObject>();
            for (var i = 0; i < expected.Count; i++)
                messages.Add(await _connections[name].NextAsync(TestTimeouts.Scale(StepTimeout))
                    ?? throw new InvalidOperationException($"{name} closed after {i} of {expected.Count} messages."));
            var left = expected.ToList();
            foreach (var message in messages)
            {
                var matched = left.FindIndex(candidate => TryMatch(candidate, message));
                Check(matched >= 0, $"{message.ToJsonString()} matches none of {new JsonArray([.. left.Select(m => m!.DeepClone())]).ToJsonString()}.");
                left.RemoveAt(matched);
            }
        }

        // Match without binding anything when it fails.
        private bool TryMatch(JsonNode? expected, JsonNode? actual)
        {
            var bindings = new Dictionary<string, JsonNode?>(_bindings, StringComparer.Ordinal);
            try
            {
                Match(expected, actual, "message");
                return true;
            }
            catch (InvalidOperationException)
            {
                _bindings.Clear();
                foreach (var (key, value) in bindings) _bindings[key] = value;
                return false;
            }
        }

        private async Task ClosedAsync(JsonObject closed)
        {
            var connection = _connections[closed["connection"]!.GetValue<string>()];
            var message = await connection.NextAsync(TestTimeouts.Scale(StepTimeout));
            Check(message is null, $"The connection sent {message?.ToJsonString()} instead of closing.");
            if (closed["status"] is { } status)
                Check((int?)connection.CloseStatus == status.GetValue<int>(),
                    $"The connection closed with {(int?)connection.CloseStatus}, not {status}.");
        }

        // ----- Values

        // A message the runner writes: $any writes its default, $id its binding (a new request id the first time), $secret
        // its value.
        private JsonNode? Resolve(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject placeholder when placeholder.ContainsKey("$any"):
                    return placeholder["default"]?.DeepClone();
                case JsonObject placeholder when placeholder.ContainsKey("$id"):
                    var id = placeholder["$id"]!.GetValue<string>();
                    if (_bindings.TryGetValue(id, out var bound)) return bound?.DeepClone();
                    if (scenario["ids"]![id]!["kind"]!.GetValue<string>() == "request")
                        return _bindings[id] = JsonValue.Create(++_nextRequest);
                    if (scenario["ids"]![id]!["kind"]!.GetValue<string>() == "wire") return scenario["ids"]![id]!["value"]!.DeepClone();
                    throw new InvalidOperationException($"{id} is not bound yet.");
                case JsonObject placeholder when placeholder.ContainsKey("$secret"):
                    return Secret(placeholder["$secret"]!.GetValue<string>());
                case JsonObject placeholder when ScenarioFiles.IsPath(placeholder):
                    return _files.Path(placeholder);
                case JsonObject value:
                    var copy = new JsonObject();
                    foreach (var (member, child) in value) copy[member] = Resolve(child);
                    return copy;
                case JsonArray array:
                    return new JsonArray([.. array.Select(Resolve)]);
                default:
                    return node?.DeepClone();
            }
        }

        private string Secret(string name) => name switch
        {
            "nonce" => HostProtocol.Base64Url(_nonce),
            "key" => _key!,
            "other-key" => HostProtocol.NewSecret(),
            "host-proof" => HostProtocol.Proof(_nonce, HostProtocol.HostProofLabel, _key!),
            "host-proof-other-nonce" => HostProtocol.Proof(RandomNumberGenerator.GetBytes(32), HostProtocol.HostProofLabel, _key!),
            "guest-proof" => HostProtocol.Proof(_nonce, HostProtocol.GuestProofLabel, _key!),
            "guest-proof-other-nonce" => HostProtocol.Proof(RandomNumberGenerator.GetBytes(32), HostProtocol.GuestProofLabel, _key!),
            _ => throw new InvalidOperationException($"The runner cannot write the secret {name}."),
        };

        private static readonly Regex LaunchUrl = new(@"^http://(127\.0\.0\.1|\[::1\]):\d+(/[A-Za-z0-9._~-]+)*/_runic_launch/[A-Za-z0-9_-]{43}$");

        // A message the guest sent against the expected one, by JSON value.
        private void Match(JsonNode? expected, JsonNode? actual, string path)
        {
            switch (expected)
            {
                case JsonObject placeholder when placeholder.ContainsKey("$any"):
                    var types = placeholder["$any"] is JsonArray list ? list.Select(item => item!.GetValue<string>()) : [placeholder["$any"]!.GetValue<string>()];
                    Check(types.Any(type => IsType(actual, type)), $"{path}: {actual?.ToJsonString() ?? "null"} is not {string.Join(" or ", types)}.");
                    return;
                case JsonObject placeholder when placeholder.ContainsKey("$id"):
                    var id = placeholder["$id"]!.GetValue<string>();
                    if (_bindings.TryGetValue(id, out var bound))
                        Check(JsonNode.DeepEquals(bound, actual), $"{path}: {actual?.ToJsonString()} is not {id} ({bound?.ToJsonString()}).");
                    else _bindings[id] = actual?.DeepClone();
                    return;
                case JsonObject placeholder when placeholder.ContainsKey("$secret"):
                    var secret = placeholder["$secret"]!.GetValue<string>();
                    var text = actual?.GetValueKind() == JsonValueKind.String ? actual.GetValue<string>() : null;
                    Check(secret == "launch-url" ? text is not null && LaunchUrl.IsMatch(text) : text == Secret(secret),
                        $"{path}: {actual?.ToJsonString()} is not the {secret}.");
                    return;
                case JsonObject placeholder when ScenarioFiles.IsPath(placeholder):
                    var local = _files.Path(placeholder);
                    Check(actual?.GetValueKind() == JsonValueKind.String && actual.GetValue<string>() == local, $"{path}: {actual?.ToJsonString()} is not {local}.");
                    return;
                case JsonObject value:
                    Check(actual is JsonObject, $"{path}: {actual?.ToJsonString()} is not an object.");
                    var members = actual!.AsObject();
                    Check(members.Select(member => member.Key).Order().SequenceEqual(value.Select(member => member.Key).Order()),
                        $"{path}: members {string.Join(",", members.Select(m => m.Key))} are not {string.Join(",", value.Select(m => m.Key))}.");
                    foreach (var (member, child) in value) Match(child, members[member], $"{path}/{member}");
                    return;
                case JsonArray array:
                    Check(actual is JsonArray other && other.Count == array.Count, $"{path}: {actual?.ToJsonString()} is not {array.ToJsonString()}.");
                    for (var i = 0; i < array.Count; i++) Match(array[i], actual![i], $"{path}/{i}");
                    return;
                default:
                    Check(JsonNode.DeepEquals(expected, actual), $"{path}: {actual?.ToJsonString() ?? "null"} is not {expected?.ToJsonString() ?? "null"}.");
                    return;
            }
        }

        private static bool IsType(JsonNode? node, string type) => (type, node?.GetValueKind()) switch
        {
            ("null", null or JsonValueKind.Null) => true,
            ("string", JsonValueKind.String) or ("object", JsonValueKind.Object) or ("array", JsonValueKind.Array) => true,
            ("boolean", JsonValueKind.True or JsonValueKind.False) => true,
            ("number", JsonValueKind.Number) => true,
            ("integer", JsonValueKind.Number) => node!.GetValue<double>() == Math.Floor(node.GetValue<double>()),
            _ => false,
        };

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        public async ValueTask DisposeAsync()
        {
            ConformanceGuardedWindow.Reset();
            CloseInput();
            foreach (var connection in _connections.Values) await connection.DisposeAsync();
            if (_run is not null)
            {
                try { await _run.WaitScaledAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception error) when (error is RunicHostedApplicationException or TimeoutException) { }
            }
            if (_provider is not null) await _provider.DisposeAsync();
            foreach (var cancellation in _openCancellations.Values) cancellation.Dispose();
            foreach (var cancellation in _dialogCancellations.Values) cancellation.Dispose();
            _files.Dispose();
            _output.Dispose();
        }
    }

    // A WebSocket of the runner and the messages the guest sent on it; null marks the close.
    private sealed class Connection : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly WebSocket _socket;
        private readonly System.Threading.Channels.Channel<JsonObject?> _messages = System.Threading.Channels.Channel.CreateUnbounded<JsonObject?>();

        public Connection(TcpClient client, WebSocket socket)
        {
            _client = client;
            _socket = socket;
            _ = ReadAsync();
        }

        public WebSocketCloseStatus? CloseStatus { get; private set; }

        private async Task ReadAsync()
        {
            try
            {
                while (true)
                {
                    var message = await HostChannel.ReceiveAsync(_socket, CancellationToken.None);
                    if (message is null) break;
                    await _messages.Writer.WriteAsync(message);
                }
            }
            catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException or HostProtocolViolationException) { }
            CloseStatus = _socket.CloseStatus;
            _messages.Writer.TryWrite(null);
            _messages.Writer.TryComplete();
        }

        public bool TryPeekMessage(out JsonObject? message) => _messages.Reader.TryPeek(out message) && message is not null;

        public async Task<JsonObject?> NextAsync(TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try { return await _messages.Reader.ReadAsync(cancel.Token); }
            catch (System.Threading.Channels.ChannelClosedException) { return null; }
        }

        public Task SendAsync(JsonObject message) => HostChannel.SendAsync(_socket, message, CancellationToken.None);

        // A message that breaks the profile; the guest may close the connection before it took all of it.
        public async Task SendRawAsync(JsonObject raw)
        {
            var (bytes, type) = raw switch
            {
                _ when raw["binary"] is { } binary => (new byte[binary.GetValue<int>()], WebSocketMessageType.Binary),
                _ when raw["text"] is { } text => (Encoding.UTF8.GetBytes(text.GetValue<string>()), WebSocketMessageType.Text),
                _ => (Encoding.ASCII.GetBytes("{}" + new string(' ', raw["textBytes"]!.GetValue<int>() - 2)), WebSocketMessageType.Text),
            };
            try { await _socket.SendAsync(bytes, type, endOfMessage: true, CancellationToken.None); }
            catch (Exception error) when (error is WebSocketException or IOException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
                catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { }
            }
            _socket.Dispose();
            _client.Dispose();
        }
    }

    // The guest's standard output, line by line.
    private sealed class LineWriter : TextWriter
    {
        private readonly StringBuilder _all = new();
        private readonly StringBuilder _line = new();
        private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public override Encoding Encoding => Encoding.UTF8;

        public string All
        {
            get { lock (_all) return _all.ToString(); }
        }

        public override void Write(char value)
        {
            lock (_all)
            {
                _all.Append(value);
                if (value == '\n')
                {
                    _lines.Writer.TryWrite(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else _line.Append(value);
            }
        }

        public async Task<string> NextAsync(Func<string, bool> wanted, TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            while (true)
            {
                var line = await _lines.Reader.ReadAsync(cancel.Token);
                if (wanted(line)) return line;
            }
        }
    }
}

sealed class ConformanceMainWindow : RunicWindow<OrderedCollectionModel>;

sealed class ConformanceGuardedWindow : RunicWindow<OrderedCollectionModel>
{
    private static readonly object s_gate = new();
    // What the guard answers (the corpus's answerGuard action), and the decision it waits for after holdGuard; the
    // conformance cases run one at a time.
    private static bool s_answer = true;
    private static TaskCompletionSource<bool>? s_held;

    public ConformanceGuardedWindow() => GuardClose = true;

    public static void Reset() => Decide(true);

    public static void Hold()
    {
        lock (s_gate) s_held ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Decide(bool allow)
    {
        TaskCompletionSource<bool>? held;
        lock (s_gate)
        {
            s_answer = allow;
            (held, s_held) = (s_held, null);
        }
        held?.TrySetResult(allow);
    }

    protected override ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        lock (s_gate) return s_held is { } held ? new ValueTask<bool>(held.Task) : ValueTask.FromResult(s_answer);
    }
}

// The corpus's test application's menus (specs/desktop/host-conformance/README.md, Test application): its menu bar in
// English or German, the application's commands of its items, which record each command that runs, and its context menu.
internal sealed class ConformanceMenus
{
    private readonly List<string> _invoked = [];

    public ConformanceMenus()
    {
        foreach (var id in (string[])["file.open", "file.save", "file.recent", "view.grid"])
            _commands[id] = new ConformanceCommand(() => { lock (_invoked) _invoked.Add(id); });
    }

    private readonly Dictionary<string, ConformanceCommand> _commands = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ICommand> Commands => _commands.ToDictionary(static pair => pair.Key, static pair => (ICommand)pair.Value, StringComparer.Ordinal);

    public ConformanceCommand Command(string id) => _commands[id];

    // The commands that ran, in order.
    public IReadOnlyList<string> Invoked
    {
        get { lock (_invoked) return [.. _invoked]; }
    }

    public static IReadOnlyList<DesktopMenu> Bar(bool german) =>
    [
        new DesktopMenu(german ? "Datei" : "File",
        [
            new DesktopMenuItem(german ? "Öffnen" : "Open", "file.open") { Shortcut = new DesktopShortcut("O") },
            new DesktopMenuItem(german ? "Sichern" : "Save", "file.save") { Shortcut = new DesktopShortcut("S") },
            new DesktopMenuSeparator(),
            new DesktopMenu(german ? "Zuletzt geöffnet" : "Recent", [new DesktopMenuItem("notes.txt", "file.recent")]),
        ]) { Role = DesktopMenuRole.File },
        new DesktopMenu(german ? "Ansicht" : "View", [new DesktopMenuItem(german ? "Raster" : "Grid", "view.grid") { IsChecked = false }]),
    ];

    public static ImmutableArray<DesktopMenuEntry> ContextMenu => HostedContextMenu.Entries;
}

internal sealed class ConformanceCommand(Action execute) : ICommand
{
    private volatile bool _enabled = true;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _enabled;

    public void Execute(object? parameter) => execute();

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

// The test application's dialogs (README, Test application).
internal static class ConformanceDialogs
{
    public static OpenFileOptions Open => new() { Title = "Open", Filters = [new FileDialogFilter("Text", ["txt"])] };

    public static SaveFileOptions Save => new("notes.txt") { Title = "Save" };

    public static OpenDirectoryOptions Folder => new() { Title = "Choose a folder" };

    public static DesktopMessageBox MessageBox => new("Save changes?")
    {
        Title = "Conformance",
        Kind = DesktopMessageBoxKind.Warning,
        Buttons =
        [
            new DesktopMessageBoxButton("save", "Save") { Role = DesktopMessageBoxButtonRole.Default },
            new DesktopMessageBoxButton("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive },
            new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel },
        ],
    };
}

// The files of a scenario's {"$file": name} and {"$directory": name} (README, Values in messages): absolute paths in a
// directory of the runner's own, created on first use and removed with it.
internal sealed class ScenarioFiles : IDisposable
{
    private string? _root;

    public static bool IsPath(JsonObject placeholder) => placeholder.Count == 1 && (placeholder.ContainsKey("$file") || placeholder.ContainsKey("$directory"));

    public string Path(JsonObject placeholder)
    {
        _root ??= Directory.CreateTempSubdirectory("runic-host-conformance-").FullName;
        var directory = placeholder["$directory"] is not null;
        var name = (placeholder["$file"] ?? placeholder["$directory"])!.GetValue<string>();
        var path = System.IO.Path.Combine(_root, name);
        if (directory) Directory.CreateDirectory(path);
        else if (!File.Exists(path)) File.WriteAllText(path, "Runic host conformance\n");
        return path;
    }

    public void Dispose()
    {
        if (_root is null) return;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
