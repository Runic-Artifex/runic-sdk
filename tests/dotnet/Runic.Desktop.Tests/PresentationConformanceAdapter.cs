using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The conformance adapter of Runic.Desktop for the presentation contract's portable cases (`desktop.presentation/1`,
// specs/desktop/conformance; eng/conformance/PROTOCOL.md, "Desktop presentation"): `bun run conformance
// dotnet-desktop-presentation` starts this executable with --conformance-adapter. Each abstract operation of
// operations.json runs against a real DesktopHost on loopback: a request is a real HTTP request to the surface, a
// session a real WebSocket of the surface's page transport (webui-compat/52f9e75: its upgrade with the session credential
// as subprotocol, then the token check), an invocation a real call packet. The adapter reports what it observed as the
// contract's events, states and outcomes: an error's category, code, message and retryability are those of the
// DesktopDiagnostic the host reported (DesktopHostOptions.DiagnosticSink), never the adapter's own. A terminal outcome the
// host reports nothing for has none.
internal static class PresentationConformanceAdapter
{
    public const string Argument = "--conformance-adapter";

    public static async Task<int> RunAsync()
    {
        var protocol = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        // The adapter mode runs instead of TUnit (Program.cs dispatches on --conformance-adapter), so no TUnit log is lost.
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging
        Console.SetOut(Console.Error);
#pragma warning restore TUnit0055
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        PresentationRun? run = null;
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            var request = JsonNode.Parse(line)!.AsObject();
            var seq = request["seq"]!.GetValue<long>();
            var op = request["op"]!.GetValue<string>();
            var args = request["args"] as JsonObject ?? [];
            JsonObject response;
            try
            {
                JsonNode? result = op switch
                {
                    "describe" => Describe(),
                    "shutdown" => new JsonObject(),
                    "desktop.decode" => Decode(args),
                    "desktop.start" => await StartAsync(run, args, started => run = started),
                    "desktop.stop" => await StopAsync(run, () => run = null),
                    "desktop.step" when run is not null => await run.StepAsync(args),
                    "desktop.observe" when run is not null => run.Observe(),
                    _ when op.StartsWith("desktop.", StringComparison.Ordinal) && run is null => throw new InvalidOperationException($"{op} before desktop.start."),
                    _ => throw new NotSupportedException($"The .NET presentation adapter has no op {op}."),
                };
                response = new JsonObject { ["seq"] = seq, ["ok"] = result };
            }
            catch (NotSupportedException error)
            {
                response = new JsonObject { ["seq"] = seq, ["error"] = new JsonObject { ["code"] = "unsupported", ["message"] = error.Message } };
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"{op}: {error}");
                response = new JsonObject { ["seq"] = seq, ["error"] = new JsonObject { ["code"] = "failed", ["message"] = $"{error.GetType().Name}: {error.Message}" } };
            }
            await protocol.WriteLineAsync(response.ToJsonString());
            if (op == "shutdown") break;
        }
        await StopAsync(run);
        return 0;
    }

    // Runic.Desktop's features of the scenarios' `requires`, and the wire profiles of its vectors: its private page
    // transport and its structured payloads (StructuredPayload).
    private static JsonObject Describe() => new()
    {
        ["protocol"] = "rax.conformance-adapter/1",
        ["implementation"] = new JsonObject
        {
            ["id"] = "dotnet-desktop-presentation",
            ["name"] = "Runic.Desktop",
            ["version"] = typeof(DesktopHost).Assembly.GetName().Version?.ToString(),
        },
        ["corpora"] = new JsonArray("desktop.presentation/1"),
        ["roles"] = new JsonArray("host"),
        ["features"] = new JsonArray("correlated-invocation", "head-request", "origin-policy", "reconnect", "request-cancellation", "session-authentication",
            "shared-listener", "streaming-response", "structured-errors", "surface-isolation"),
        ["capabilities"] = new JsonArray(),
        ["profiles"] = new JsonArray("webui-compat/52f9e75", "structured-json/1"),
    };

    private static async Task<JsonObject> StartAsync(PresentationRun? run, JsonObject args, Action<PresentationRun> started)
    {
        if (run is not null) throw new InvalidOperationException("A scenario is running: desktop.stop first.");
        started(new PresentationRun(args["policy"] as JsonObject));
        return await Task.FromResult(new JsonObject());
    }

    private static async Task<JsonObject> StopAsync(PresentationRun? run, Action? stopped = null)
    {
        if (run is not null) await run.DisposeAsync();
        stopped?.Invoke();
        return [];
    }

    // A codec vector, decoded the way Runic.Desktop reads it: a webui-compat packet by its header (WebUiProtocol) and, for
    // a multi packet, its announced length; a structured payload by StructuredPayload.Parse.
    private static JsonObject Decode(JsonObject args)
    {
        var input = args["input"]!.AsObject();
        var text = input["value"]!.GetValue<string>();
        switch (args["profile"]!.GetValue<string>())
        {
            case "webui-compat/52f9e75":
            {
                var packet = Convert.FromBase64String(text);
                if (packet.Length < WebUiProtocol.HeaderSize || packet[0] != WebUiProtocol.Signature)
                    return new JsonObject { ["error"] = new JsonObject { ["code"] = "not-a-packet" } };
                var command = packet[7];
                var value = new JsonObject { ["signature"] = (int)packet[0] };
                if (command == WebUiProtocol.Multi)
                {
                    var data = packet.AsSpan(WebUiProtocol.DataOffset);
                    var end = data.IndexOf((byte)0);
                    value["command"] = "multi";
                    value["payloadLength"] = end > 0 && int.TryParse(Encoding.ASCII.GetString(data[..end]), NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                        ? length : null;
                    return new JsonObject { ["value"] = value };
                }
                value["token"] = WebUiProtocol.ReadToken(packet);
                value["correlationId"] = (int)WebUiProtocol.ReadId(packet);
                value["command"] = command switch
                {
                    WebUiProtocol.CheckToken => "checkToken",
                    WebUiProtocol.CallFunction => "callFunction",
                    WebUiProtocol.JavaScript => "javaScript",
                    WebUiProtocol.Close => "close",
                    _ => $"0x{command:X2}",
                };
                value["payloadBase64"] = Convert.ToBase64String(packet.AsSpan(WebUiProtocol.DataOffset));
                return new JsonObject { ["value"] = value };
            }
            case "structured-json/1":
                try
                {
                    _ = StructuredPayload.Parse(input["schemaId"]!.GetValue<string>(), Encoding.UTF8.GetBytes(text));
                    return new JsonObject { ["value"] = JsonNode.Parse(text) };
                }
                catch (DesktopException error)
                {
                    return new JsonObject
                    {
                        ["error"] = new JsonObject
                        {
                            ["category"] = Camel(error.Category.ToString()),
                            ["code"] = error.Code,
                            ["message"] = error.Message,
                            ["retryable"] = error.Retryable,
                        },
                    };
                }
            default:
                throw new NotSupportedException($"The .NET presentation adapter has no profile {args["profile"]}.");
        }
    }

    internal static string Camel(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);
}

// One scenario's resources and what the adapter observed of them.
internal sealed class PresentationRun(JsonObject? policy) : IAsyncDisposable
{
    private const byte CallFunction = 0xF9;
    private const byte CheckToken = 0xF5;
    private const string PendingCapability = "conformance.pending";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly List<JsonObject> _events = [];
    private readonly Dictionary<string, string> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _outcomes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<DesktopDiagnostic> _diagnostics = new();
    private readonly Dictionary<string, Host> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChunkStream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Request> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Invocation> _invocations = new(StringComparer.Ordinal);
    private readonly HttpClient _client = new();
    private bool _recording;
    private ushort _nextCall;

    private sealed class Host(string? listener)
    {
        public DesktopHost? Started;
        public string? Listener = listener;
        public List<(string Id, string? Credential)> Pending = [];
    }

    private sealed record Surface(string Id, Host Host, DesktopSurface Desktop, int Port, string Credential, uint Token, string? Label);

    private sealed class Request
    {
        public required CancellationTokenSource Cancellation;
        public required string? Stream;
        public Task? Exchange;
    }

    private sealed class Session(string surface, ClientWebSocket socket)
    {
        public string Surface = surface;
        public ClientWebSocket Socket = socket;
    }

    private sealed class Invocation
    {
        public TaskCompletionSource<PresentationResult>? Release;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HandlerEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Session = "";
    }

    // A scenario's policy that Runic.Desktop's default host enforces: loopback binding, same-origin pages, a required
    // session credential and one page per surface (DesktopSecurityPolicy.Default). Another one is unsupported.
    private void CheckPolicy()
    {
        if (policy is null) return;
        var defaults = new JsonObject { ["binding"] = "loopback", ["origins"] = new JsonArray("same-origin"), ["credential"] = "required", ["clientAdmission"] = "single" };
        foreach (var (name, value) in policy)
        {
            if (!JsonNode.DeepEquals(defaults[name], value)) throw new NotSupportedException($"Runic.Desktop's host has no policy {name} {value?.ToJsonString()}.");
        }
    }

    // A policy that requires the session credential authenticates a session at its upgrade; without one, a session
    // connects at its upgrade and authenticates with its token check (session.authenticate).
    private bool CredentialAuthenticates => policy?["credential"]?.GetValue<string>() == "required";

    private void Event(string type, string subject, JsonObject? data = null)
    {
        if (!_recording) return;
        var e = new JsonObject { ["type"] = type, ["subject"] = subject };
        if (data is not null) e["data"] = data;
        lock (_events) _events.Add(e);
    }

    private void State(string subject, string state)
    {
        lock (_states) _states[subject] = state;
    }

    private void Outcome(string subject, string terminal, DesktopDiagnostic? diagnostic)
    {
        if (diagnostic is null) return;
        lock (_outcomes)
            _outcomes[subject] = new JsonObject
            {
                ["subject"] = subject,
                ["terminal"] = terminal,
                ["category"] = PresentationConformanceAdapter.Camel(diagnostic.Category.ToString()),
                ["code"] = diagnostic.Code,
                ["message"] = diagnostic.Message,
                ["retryable"] = diagnostic.Retryable,
            };
    }

    // The diagnostic the host reported for what the adapter just did.
    private async Task<DesktopDiagnostic?> NextDiagnosticAsync(TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (_diagnostics.TryDequeue(out var diagnostic)) return diagnostic;
            await Task.Delay(10);
        }
        return null;
    }

    public JsonObject Observe()
    {
        lock (_events) lock (_states) lock (_outcomes)
                    return new JsonObject
                    {
                        ["events"] = new JsonArray([.. _events.Select(static e => e.DeepClone())]),
                        ["states"] = new JsonObject([.. _states.Select(static s => KeyValuePair.Create(s.Key, (JsonNode?)s.Value))]),
                        ["outcomes"] = new JsonArray([.. _outcomes.Values.Select(static o => o.DeepClone())]),
                    };
    }

    public async Task<JsonObject> StepAsync(JsonObject args)
    {
        CheckPolicy();
        _recording = args["phase"]?.GetValue<string>() == "step";
        var op = args["op"]!.GetValue<string>();
        var target = args["target"]!.GetValue<string>();
        var arguments = args["arguments"] as JsonObject ?? [];
        string? Text(string name) => arguments[name]?.GetValue<string>();
        switch (op)
        {
            case "host.create":
                _hosts[target] = new Host(Text("listener"));
                break;
            case "host.createStarted":
                _hosts[target] = new Host(Text("listener"));
                await StartHostAsync(target);
                break;
            case "host.start":
                await StartHostAsync(target);
                break;
            case "surface.create" or "surface.createStarted":
            {
                var host = _hosts[Text("host")!];
                if (host.Started is null && op == "surface.create") host.Pending.Add((target, Text("credential")));
                else await CreateSurfaceAsync(host, target, Text("credential"));
                break;
            }
            case "surface.close":
                Event("surface.closing", target);
                await _surfaces[target].Desktop.CloseAsync();
                State(target, "closed");
                Event("surface.closed", target);
                await CheckListenerAsync(_surfaces[target].Host);
                break;
            case "capability.registerFailing":
            {
                var detail = Text("privateDetail") ?? "private detail";
                var name = Text("name")!;
                _ = _surfaces[target].Desktop.RegisterCapability(name, (_, _) =>
                {
                    lock (_invocations) if (_invocations.FirstOrDefault(i => !i.Value.Entered.Task.IsCompleted) is { Key: { } id } open) { Event("invocation.accepted", id); open.Value.Entered.TrySetResult(); }
                    throw new InvalidOperationException(detail);
                });
                _privateDetails.Add(detail);
                break;
            }
            case "response.stream":
                _streams[target] = new ChunkStream(target, this,
                    arguments["chunks"] is JsonArray chunks ? [.. chunks.Select(static c => Encoding.UTF8.GetBytes(c!.GetValue<string>()))] : [],
                    Text("contentType"), arguments["contentLength"]?.GetValue<long>());
                State(target, "unopened");
                break;
            case "stream.acceptNext":
                await _streams[target].AcceptNextAsync(Wait);
                break;
            case "request.start" or "request.head":
                await StartRequestAsync(target, op == "request.head", _surfaces[Text("surface")!], Text("path"), Text("stream"));
                break;
            case "request.probe":
            {
                using var response = await _client.GetAsync(_surfaces[target].Desktop.Url);
                Event(response.IsSuccessStatusCode ? "request.succeeded" : "request.failed", target);
                break;
            }
            case "request.disconnect":
                await DisconnectRequestAsync(target);
                break;
            case "session.createAuthenticated":
                await ConnectAsync(target, _surfaces[Text("surface")!], "valid", "same-origin", authenticate: true);
                break;
            case "session.connect":
                await ConnectAsync(target, _surfaces[Text("surface")!], Text("credential") ?? "valid", Text("origin") ?? "same-origin", authenticate: CredentialAuthenticates);
                break;
            case "session.authenticate":
                await AuthenticateAsync(target);
                break;
            case "session.disconnect":
                await DisconnectSessionAsync(target);
                break;
            case "invocation.start":
                await InvokeAsync(target, Text("session")!, Text("capability")!);
                break;
            case "invocation.startPending":
                await StartPendingAsync(target, Text("session")!);
                break;
            case "invocation.deliverLateSuccess":
                await DeliverLateSuccessAsync(target, Text("session")!);
                break;
            default:
                throw new NotSupportedException($"The .NET presentation adapter has no operation {op}.");
        }
        return [];
    }

    private readonly List<string> _privateDetails = [];

    // ----- Hosts and surfaces

    private async Task StartHostAsync(string id)
    {
        var host = _hosts[id];
        host.Started = await DesktopHost.StartAsync(new DesktopHostOptions { DiagnosticSink = _diagnostics.Enqueue, WaitForConnection = false });
        State(id, "running");
        Event("host.started", id);
        foreach (var (surface, credential) in host.Pending) await CreateSurfaceAsync(host, surface, credential);
        host.Pending.Clear();
        await CheckListenerAsync(host);
    }

    // A host's shared listener listens while it accepts a connection on the port its surfaces share.
    private async Task CheckListenerAsync(Host host)
    {
        if (host.Listener is not { } listener) return;
        var port = _surfaces.Values.FirstOrDefault(s => s.Host == host)?.Port;
        if (port is null) return;
        using var probe = new TcpClient();
        try
        {
            await probe.ConnectAsync("127.0.0.1", port.Value).WaitAsync(Wait);
            State(listener, "listening");
        }
        catch (SocketException) { State(listener, "closed"); }
    }

    // Every surface serves its streams by path, and a short text at its root.
    private async Task CreateSurfaceAsync(Host host, string id, string? label)
    {
        var desktop = await host.Started!.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = id,
            Content = new DesktopContent.Handler((request, _) =>
            {
                ChunkStream? stream;
                lock (_routes) _routes.TryGetValue(request.Path, out stream);
                return ValueTask.FromResult<ContentResponse?>(stream is not null
                    ? ContentResponse.Stream(stream.OpenAsync, stream.ContentType, contentLength: stream.ContentLength)
                    : ContentResponse.Text("conformance"));
            }),
        });
        var bridge = await _client.GetStringAsync(new Uri(desktop.Url, "webui.js"));
        var surface = new Surface(id, host, desktop, desktop.Url.Port, Quoted(bridge, "const SESSION_CREDENTIAL=\""), Unsigned(bridge, "const TOKEN="), label);
        _surfaces[id] = surface;
        State(id, "running");
        Event("surface.started", id);
    }

    private readonly Dictionary<string, ChunkStream> _routes = new(StringComparer.Ordinal);

    // ----- Requests and streams

    private async Task StartRequestAsync(string id, bool head, Surface surface, string? path, string? streamId)
    {
        var route = path ?? "/" + (streamId ?? id);
        if (streamId is not null) lock (_routes) _routes[route] = _streams[streamId];
        var request = new Request { Cancellation = new CancellationTokenSource(), Stream = streamId };
        _requests[id] = request;
        State(id, "started");
        Event("request.started", id);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        request.Exchange = Task.Run(async () =>
        {
            try
            {
                using var message = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, new Uri(surface.Desktop.Url, route.TrimStart('/')));
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.Cancellation.Token);
                var data = new JsonObject { ["status"] = (int)response.StatusCode };
                if (response.Content.Headers.ContentType?.MediaType is { } type) data["contentType"] = type;
                if (response.Content.Headers.ContentLength is { } length) data["contentLength"] = length;
                Event("response.committed", id, data);
                committed.TrySetResult();
                await using var body = await response.Content.ReadAsStreamAsync(request.Cancellation.Token);
                await body.CopyToAsync(Stream.Null, request.Cancellation.Token);
                State(id, "succeeded");
                Event("request.succeeded", id);
            }
            catch (Exception) when (request.Cancellation.IsCancellationRequested)
            {
                committed.TrySetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"request {id}: {error}");
                State(id, "failed");
                committed.TrySetResult();
            }
        });
        // A HEAD request ends at once. A GET runs on: the host commits its response head when its stream gives the first
        // chunk (stream.acceptNext).
        if (head) await request.Exchange.WaitAsync(Wait);
    }

    // The requester goes away. The host cancels the response's stream (the factory's cancellation token) and releases it.
    private async Task DisconnectRequestAsync(string id)
    {
        var request = _requests[id];
        await request.Cancellation.CancelAsync();
        if (request.Stream is { } streamId)
        {
            var stream = _streams[streamId];
            await stream.Cancelled.Task.WaitAsync(Wait);
            State(id, "cancelled");
            Event("request.cancelled", id, new JsonObject { ["reason"] = "requesterDisconnected" });
            await stream.Released.WaitAsync(Wait);
        }
        try { await request.Exchange!.WaitAsync(Wait); }
        catch (TimeoutException) { }
    }

    // A response stream whose chunks the host reads one at a time, each once a stream.acceptNext offered it.
    private sealed class ChunkStream(string id, PresentationRun run, byte[][] chunks, string? contentType, long? contentLength) : Stream
    {
        private readonly SemaphoreSlim _offered = new(0);
        private int _next;
        private TaskCompletionSource _accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleasedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Released => ReleasedSource.Task;
        public string? ContentType => contentType ?? "application/octet-stream";
        public long? ContentLength => contentLength;

        public ValueTask<Stream> OpenAsync(CancellationToken cancellationToken)
        {
            run.State(id, "open");
            return ValueTask.FromResult<Stream>(this);
        }

        public async Task AcceptNextAsync(TimeSpan wait)
        {
            var accepted = _accepted;
            _offered.Release();
            await accepted.Task.WaitAsync(wait);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= chunks.Length) return 0;
            try { await _offered.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            var chunk = chunks[_next];
            chunk.CopyTo(buffer);
            run.Event("stream.chunkAccepted", id, new JsonObject { ["index"] = _next });
            _next++;
            var accepted = _accepted;
            _accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            accepted.TrySetResult();
            return chunk.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (ReleasedSource.TrySetResult())
            {
                run.State(id, "released");
                run.Event("stream.released", id);
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ----- Sessions

    // `credential` is "valid" (the surface's own), "missing", or the label of another surface's credential; `origin` is
    // "same-origin" or an origin.
    private async Task ConnectAsync(string id, Surface surface, string credential, string origin, bool authenticate)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin == "same-origin" ? $"{surface.Desktop.Url.Scheme}://{surface.Desktop.Url.Authority}" : origin);
        var offered = credential switch
        {
            "missing" => null,
            "valid" => surface.Credential,
            var label => _surfaces.Values.FirstOrDefault(s => s.Label == label)?.Credential ?? throw new InvalidOperationException($"No surface has the credential {label}."),
        };
        if (offered is not null) socket.Options.AddSubProtocol($"runic-desktop.{offered}");
        while (_diagnostics.TryDequeue(out _)) { }
        try
        {
            using var timeout = new CancellationTokenSource(Wait);
            await socket.ConnectAsync(new UriBuilder(surface.Desktop.Url) { Scheme = "ws", Path = $"{surface.Desktop.Url.AbsolutePath}_webui_ws_connect" }.Uri, timeout.Token);
        }
        catch (WebSocketException)
        {
            socket.Dispose();
            var diagnostic = await NextDiagnosticAsync(TimeSpan.FromSeconds(2));
            var data = new JsonObject();
            if (diagnostic is not null)
            {
                data["category"] = PresentationConformanceAdapter.Camel(diagnostic.Category.ToString());
                data["code"] = diagnostic.Code;
            }
            State(id, "closed");
            Event("session.rejected", id, data);
            Outcome(id, "failed", diagnostic);
            return;
        }
        _sessions[id] = new Session(surface.Id, socket);
        if (!authenticate)
        {
            State(id, "connected");
            Event("session.connected", id);
            return;
        }
        await AuthenticateAsync(id);
    }

    // The page transport's token check: the surface's TOKEN in a check-token packet, answered with 1.
    private async Task AuthenticateAsync(string id)
    {
        var session = _sessions[id];
        using var timeout = new CancellationTokenSource(Wait);
        await SendAsync(session.Socket, Packet(_surfaces[session.Surface].Token, 0, CheckToken, [0]), timeout.Token);
        var reply = await ReceiveAsync(session.Socket, timeout.Token);
        if (reply is { Length: > 8 } && reply[8] == 1)
        {
            State(id, "authenticated");
            Event("session.authenticated", id);
        }
        else
        {
            State(id, "closed");
            Event("session.rejected", id);
        }
    }

    // The page closes its WebSocket. A pending invocation of the session ends when the host cancels its handler.
    private async Task DisconnectSessionAsync(string id)
    {
        var session = _sessions[id];
        using var timeout = new CancellationTokenSource(Wait);
        try { await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", timeout.Token); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
        State(id, "closed");
        Event("session.closed", id, new JsonObject { ["reason"] = "transportClosed" });
        foreach (var (invocationId, invocation) in _invocations.Where(i => i.Value.Session == id && i.Value.Release is not null).ToList())
        {
            if (await Task.WhenAny(invocation.Release!.Task, Task.Delay(Wait)) != invocation.Release.Task || !invocation.Release.Task.IsCanceled) continue;
            State(invocationId, "failed");
            Event("invocation.failed", invocationId, new JsonObject { ["category"] = "transportClosed" });
            Outcome(invocationId, "failed", await NextDiagnosticAsync(TimeSpan.FromMilliseconds(500)));
        }
    }

    // ----- Invocations

    private async Task InvokeAsync(string id, string sessionId, string capability)
    {
        var session = _sessions[sessionId];
        var invocation = new Invocation { Session = sessionId };
        lock (_invocations) _invocations[id] = invocation;
        while (_diagnostics.TryDequeue(out _)) { }
        using var timeout = new CancellationTokenSource(Wait);
        await SendAsync(session.Socket, Packet(_surfaces[session.Surface].Token, ++_nextCall, CallFunction, [.. Encoding.UTF8.GetBytes(capability + "\0\0")]), timeout.Token);
        var reply = await ReceiveAsync(session.Socket, timeout.Token);
        var diagnostic = await NextDiagnosticAsync(TimeSpan.FromMilliseconds(500));
        var replyText = reply is null ? "" : Encoding.UTF8.GetString(reply);
        if (_privateDetails.Any(detail => replyText.Contains(detail, StringComparison.Ordinal) || (diagnostic?.ToString().Contains(detail, StringComparison.Ordinal) ?? false)))
            Event("publicError.containsPrivateDetail", id);
        if (diagnostic is null)
        {
            State(id, "succeeded");
            Event("invocation.succeeded", id);
            return;
        }
        var data = new JsonObject
        {
            ["category"] = PresentationConformanceAdapter.Camel(diagnostic.Category.ToString()),
            ["code"] = diagnostic.Code,
        };
        State(id, "failed");
        if (invocation.Entered.Task.IsCompleted)
        {
            data["retryable"] = diagnostic.Retryable;
            data["redacted"] = !_privateDetails.Any(detail => diagnostic.ToString().Contains(detail, StringComparison.Ordinal));
            Event("invocation.failed", id, data);
        }
        else Event("invocation.rejected", id, data);
        Outcome(id, "failed", diagnostic);
    }

    // An invocation whose handler waits until the scenario delivers its result; the handler's cancellation ends it.
    private async Task StartPendingAsync(string id, string sessionId)
    {
        var session = _sessions[sessionId];
        var invocation = new Invocation { Session = sessionId, Release = new TaskCompletionSource<PresentationResult>(TaskCreationOptions.RunContinuationsAsynchronously) };
        lock (_invocations) _invocations[id] = invocation;
        _ = _surfaces[session.Surface].Desktop.RegisterCapability(PendingCapability, async (_, cancellationToken) =>
        {
            invocation.Entered.TrySetResult();
            using var cancelled = cancellationToken.Register(() => invocation.Release.TrySetCanceled(cancellationToken));
            try { return await invocation.Release.Task; }
            finally { invocation.HandlerEnded.TrySetResult(); }
        });
        using var timeout = new CancellationTokenSource(Wait);
        await SendAsync(session.Socket, Packet(_surfaces[session.Surface].Token, ++_nextCall, CallFunction, [.. Encoding.UTF8.GetBytes(PendingCapability + "\0\0")]), timeout.Token);
        await invocation.Entered.Task.WaitAsync(Wait);
        State(id, "pending");
    }

    // The pending handler ends with a success after its session closed: nothing reaches a page.
    private async Task DeliverLateSuccessAsync(string id, string sessionId)
    {
        var invocation = _invocations[id];
        var delivered = invocation.Release!.TrySetResult(PresentationResult.FromString("late"));
        if (_sessions[sessionId].Socket.State != WebSocketState.Open && (delivered || invocation.Release.Task.IsCanceled))
            Event("invocation.lateTerminalIgnored", id);
        else if (delivered)
        {
            State(id, "succeeded");
            Event("invocation.succeeded", id);
        }
        await Task.CompletedTask;
    }

    // ----- The page transport's packets (specs/desktop/wire-profile.md)

    private static byte[] Packet(uint token, ushort id, byte command, byte[] payload)
    {
        var packet = new byte[8 + payload.Length];
        packet[0] = 0xDD;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, 8);
        return packet;
    }

    private static Task SendAsync(ClientWebSocket socket, byte[] packet, CancellationToken cancellationToken) =>
        socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var packet = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult response;
        do
        {
            response = await socket.ReceiveAsync(buffer, cancellationToken);
            if (response.MessageType == WebSocketMessageType.Close) return null;
            packet.Write(buffer.AsSpan(0, response.Count));
        }
        while (!response.EndOfMessage);
        return packet.WrittenSpan.ToArray();
    }

    private static uint Unsigned(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return uint.Parse(script[start..script.IndexOf(';', start)], CultureInfo.InvariantCulture);
    }

    private static string Quoted(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return script[start..script.IndexOf('"', start)];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var invocation in _invocations.Values) invocation.Release?.TrySetCanceled();
        foreach (var request in _requests.Values) await request.Cancellation.CancelAsync();
        foreach (var session in _sessions.Values) session.Socket.Dispose();
        foreach (var host in _hosts.Values)
        {
            if (host.Started is not null) await host.Started.DisposeAsync();
        }
        foreach (var request in _requests.Values) request.Cancellation.Dispose();
        _client.Dispose();
    }
}
