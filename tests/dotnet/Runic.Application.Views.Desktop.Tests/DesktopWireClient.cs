using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using Runic.Desktop;

// The browser side of a Desktop surface at the WebUI wire level: it authenticates as the page's bootstrap does and
// calls capabilities by name, the way the generated client's `bridge.call` does.
sealed class DesktopWireClient : IDisposable
{
    private const byte CheckToken = 0xF5;
    private const byte CallFunction = 0xF9;
    private const int HeaderSize = 8;
    private readonly ClientWebSocket _socket;
    private readonly uint _token;
    private ushort _nextId;

    private DesktopWireClient(ClientWebSocket socket, uint token)
    {
        _socket = socket;
        _token = token;
    }

    public static async Task<DesktopWireClient> ConnectAsync(DesktopSurface surface, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var bootstrap = await http.GetStringAsync(new Uri(surface.Url, "webui.js"), cancellationToken);
        var token = uint.Parse(Between(bootstrap, "const TOKEN = ", ";"), System.Globalization.CultureInfo.InvariantCulture);
        var credential = Between(bootstrap, "const SESSION_CREDENTIAL = \"", "\"");
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Origin", $"{surface.Url.Scheme}://{surface.Url.Authority}");
            socket.Options.AddSubProtocol($"runic-desktop.{credential}");
            await socket.ConnectAsync(new UriBuilder(surface.Url) { Scheme = "ws", Path = $"{surface.Url.AbsolutePath}_webui_ws_connect" }.Uri,
                cancellationToken);
            var client = new DesktopWireClient(socket, token);
            await client.SendAsync(0, CheckToken, [0], cancellationToken);
            _ = await client.ReceiveAsync(cancellationToken);
            return client;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // Calls one capability with string arguments and returns its reply text.
    public async Task<string> CallAsync(string capability, CancellationToken cancellationToken, params string[] arguments)
    {
        var values = arguments.Select(Encoding.UTF8.GetBytes).ToArray();
        var payload = new List<byte>(Encoding.UTF8.GetBytes(capability)) { 0 };
        payload.AddRange(Encoding.ASCII.GetBytes(string.Join(';', values.Select(static value => value.Length))));
        payload.Add(0);
        foreach (var value in values) { payload.AddRange(value); payload.Add(0); }
        var id = ++_nextId;
        await SendAsync(id, CallFunction, [.. payload], cancellationToken);
        // State deliveries and other server commands can arrive first.
        while (true)
        {
            var reply = await ReceiveAsync(cancellationToken);
            if (reply[7] == CallFunction && BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5, 2)) == id)
                return Encoding.UTF8.GetString(reply.AsSpan(HeaderSize)).TrimEnd('\0');
        }
    }

    // Waits for the next server command of one kind, such as Navigation (0xFB), and returns its text.
    public async Task<string> WaitForCommandAsync(byte command, CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await ReceiveAsync(cancellationToken);
            if (packet.Length >= HeaderSize && packet[7] == command)
                return Encoding.UTF8.GetString(packet.AsSpan(HeaderSize)).TrimEnd('\0');
        }
    }

    public void Dispose() => _socket.Dispose();

    private Task SendAsync(ushort id, byte command, byte[] payload, CancellationToken cancellationToken)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = 0xDD;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), _token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, HeaderSize);
        return _socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);
    }

    private async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var packet = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult response;
        do
        {
            response = await _socket.ReceiveAsync(buffer, cancellationToken);
            packet.Write(buffer.AsSpan(0, response.Count));
        }
        while (!response.EndOfMessage);
        return packet.WrittenSpan.ToArray();
    }

    private static string Between(string script, string prefix, string suffix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        return script[start..script.IndexOf(suffix, start, StringComparison.Ordinal)];
    }
}
