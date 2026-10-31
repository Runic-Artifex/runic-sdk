using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;

namespace Runic.Desktop.ServerBenchmarks;

// One Runic Desktop surface on Kestrel or on the native server (X1), and a page-side WebSocket that speaks the
// surface's packet profile, as runic-desktop-views.js does. Every request runs the surface's dispatch,
// DesktopBridgeTransport and a handler that replies with a fixed message, so the producer is not measured; what differs
// between the two servers is HTTP/WebSocket framing, the buffers between socket and session, and their threads.
public class SurfaceTransportBenchmarks
{
    private const byte CheckToken = 0xF5;
    private const byte CallFunction = 0xF9;
    private const byte SendRaw = 0xF8;
    private const int HeaderSize = 8;
    private const int Burst = 64;

    private DesktopHost _host = null!;
    private DesktopSurface _surface = null!;
    private DesktopBridgeTransport _transport = null!;
    private IDisposable _serving = null!;
    private FixedReply _handler = null!;
    private ClientWebSocket _socket = null!;
    private byte[][] _calls = null!;
    private byte[] _push = null!;
    private byte[] _receive = null!;

    // RUNIC_BENCHMARK_SERVERS and RUNIC_BENCHMARK_SIZES (comma-separated) select cases. In-process, every case runs in
    // one process after the ones before it; one server or size alone separates a row from the cases before it.
    [ParamsSource(nameof(Servers))]
    public string Server { get; set; } = "Kestrel";

    public static IEnumerable<string> Servers => Selected("RUNIC_BENCHMARK_SERVERS", "Kestrel,Native");

    public static IEnumerable<int> Sizes =>
        Selected("RUNIC_BENCHMARK_SIZES", "256,16384,262144").Select(size => int.Parse(size, System.Globalization.CultureInfo.InvariantCulture));

    // The size of each request, reply and push, in bytes of UTF-8 JSON.
    [ParamsSource(nameof(Sizes))]
    public int MessageBytes { get; set; }

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        AppContext.SetSwitch("Runic.Desktop.NativeServer", Server == "Native");
        _host = await DesktopHost.StartAsync();
        _surface = await _host.CreateSurfaceAsync();
        _transport = new DesktopBridgeTransport(_surface);
        _handler = new FixedReply(Json(MessageBytes, "reply"));
        _serving = _transport.Serve(_handler);

        using var http = new HttpClient();
        var bootstrap = await http.GetStringAsync(new Uri(_surface.Url, "webui.js"));
        var token = uint.Parse(Between(bootstrap, "const TOKEN = ", ";"), System.Globalization.CultureInfo.InvariantCulture);
        var credential = Between(bootstrap, "const SESSION_CREDENTIAL = \"", "\"");
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Origin", $"{_surface.Url.Scheme}://{_surface.Url.Authority}");
        _socket.Options.AddSubProtocol($"runic-desktop.{credential}");
        var endpoint = new UriBuilder(_surface.Url) { Scheme = "ws", Path = $"{_surface.Url.AbsolutePath}_webui_ws_connect" }.Uri;
        await _socket.ConnectAsync(endpoint, CancellationToken.None);
        _receive = new byte[MessageBytes + 4096];
        await _socket.SendAsync(Packet(token, 0, CheckToken, [0]), WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveAsync(CheckToken);

        var request = Json(MessageBytes, "request");
        var name = Encoding.ASCII.GetBytes("__runicViews");
        var length = Encoding.ASCII.GetBytes(request.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _calls = [.. Enumerable.Range(1, Burst).Select(id => Packet(token, (ushort)id, CallFunction, [.. name, 0, .. length, 0, .. request, 0]))];
        _push = Json(MessageBytes, "push");
        // One request tells the handler the connection's key, which pushes address.
        await RequestRoundTrip();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _socket.Dispose();
        _serving.Dispose();
        _transport.Dispose();
        _surface.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // Latency: one request and its reply.
    [Benchmark]
    public async Task RequestRoundTrip()
    {
        await _socket.SendAsync(_calls[0], WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveAsync(CallFunction);
    }

    // Throughput: 64 requests in flight, then their 64 replies.
    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task RequestBurst()
    {
        var replies = ReceiveManyAsync(CallFunction, Burst);
        foreach (var call in _calls)
        {
            await _socket.SendAsync(call, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        await replies;
    }

    // Push throughput: 64 pushes from the host to the page.
    [Benchmark(OperationsPerInvoke = Burst)]
    public async Task PushBurst()
    {
        var received = ReceiveManyAsync(SendRaw, Burst);
        for (var index = 0; index < Burst; index++)
        {
            await _transport.SendAsync(_handler.ConnectionKey!, _push);
        }
        await received;
    }

    private async Task ReceiveManyAsync(byte command, int count)
    {
        for (var index = 0; index < count; index++)
        {
            await ReceiveAsync(command);
        }
    }

    private async Task ReceiveAsync(byte command)
    {
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(_receive.AsMemory(length), CancellationToken.None);
                length += result.Count;
            }
            while (!result.EndOfMessage);
            if (length >= HeaderSize && _receive[7] == command) return;
        }
    }

    private static string[] Selected(string variable, string all) =>
        (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : all)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // One JSON object of exactly `size` bytes.
    private static byte[] Json(int size, string kind)
    {
        var prefix = $"{{\"kind\":\"{kind}\",\"text\":\"";
        const string suffix = "\"}";
        return Encoding.UTF8.GetBytes(prefix + new string('x', Math.Max(0, size - prefix.Length - suffix.Length)) + suffix);
    }

    private static byte[] Packet(uint token, ushort id, byte command, byte[] payload)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = 0xDD;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    private static string Between(string script, string prefix, string suffix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return script[start..script.IndexOf(suffix, start, StringComparison.Ordinal)];
    }

    private sealed class FixedReply(byte[] reply) : IBridgeRequestHandler
    {
        public string? ConnectionKey { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            ConnectionKey ??= request.ConnectionKey;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(reply);
        }

        public void ConnectionClosed(string connectionKey)
        {
        }
    }
}
