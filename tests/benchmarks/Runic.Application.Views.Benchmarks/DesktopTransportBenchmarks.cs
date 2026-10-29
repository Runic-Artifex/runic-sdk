using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

namespace Runic.Application.Views.Benchmarks;

// Desktop host performance (0.14, batch T2). One Views request and its reply through a Runic Desktop surface: the page's
// WebSocket frame, the surface's dispatch, DesktopBridgeTransport and the reply frame, over loopback. The page side
// reuses its buffers, so the allocations measured are the host's. The handler replies with a fixed message, so the
// producer is not measured.
public class DesktopTransportBenchmarks
{
    private const byte CheckToken = 0xF5;
    private const byte CallFunction = 0xF9;
    private const int HeaderSize = 8;

    private DesktopHost _host = null!;
    private DesktopSurface _surface = null!;
    private DesktopBridgeTransport _transport = null!;
    private IDisposable _serving = null!;
    private ClientWebSocket _socket = null!;
    private byte[] _call = null!;
    private byte[] _receive = null!;

    // The size of the request and of its reply, in bytes of UTF-8 JSON.
    [Params(256, 16_384, 60_000)]
    public int MessageBytes { get; set; }

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    private async Task SetupAsync()
    {
        _host = await DesktopHost.StartAsync();
        _surface = await _host.CreateSurfaceAsync();
        _transport = new DesktopBridgeTransport(_surface);
        _serving = _transport.Serve(new FixedReply(Json(MessageBytes, "reply")));

        using var http = new HttpClient();
        var bootstrap = await http.GetStringAsync(new Uri(_surface.Url, "webui.js"));
        var token = uint.Parse(Between(bootstrap, "const TOKEN = ", ";"), System.Globalization.CultureInfo.InvariantCulture);
        var credential = Between(bootstrap, "const SESSION_CREDENTIAL = \"", "\"");
        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("Origin", $"{_surface.Url.Scheme}://{_surface.Url.Authority}");
        _socket.Options.AddSubProtocol($"runic-desktop.{credential}");
        await _socket.ConnectAsync(new UriBuilder(_surface.Url) { Scheme = "ws", Path = $"{_surface.Url.AbsolutePath}_webui_ws_connect" }.Uri,
            CancellationToken.None);
        _receive = new byte[MessageBytes + 1024];
        await _socket.SendAsync(Packet(token, 0, CheckToken, [0]), WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveAsync(CheckToken);

        var request = Json(MessageBytes, "request");
        var name = Encoding.ASCII.GetBytes("__runicViews");
        var length = Encoding.ASCII.GetBytes(request.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _call = Packet(token, 1, CallFunction, [.. name, 0, .. length, 0, .. request, 0]);
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

    [Benchmark]
    public async Task RequestRoundTrip()
    {
        await _socket.SendAsync(_call, WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveAsync(CallFunction);
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

    // Reads the request, as the window's handler does, and replies with one fixed message.
    private sealed class FixedReply(byte[] reply) : IBridgeRequestHandler
    {
        public ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            if (request.Message.Span[0] != (byte)'{') throw new InvalidOperationException("The request is not JSON.");
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(reply);
        }

        public void ConnectionClosed(string connectionKey) { }
    }
}
