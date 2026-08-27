using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using CsWebUi.Managed;

namespace CsWebUi.Managed.Tests;

public sealed class ManagedWindowTests
{
    private const byte Signature = 0xDD;
    private const byte JavaScript = 0xFE;
    private const byte JavaScriptQuick = 0xFD;
    private const byte Click = 0xFC;
    private const byte Navigation = 0xFB;
    private const byte CallFunction = 0xF9;
    private const byte SendRaw = 0xF8;
    private const byte AddBinding = 0xF7;
    private const byte Multi = 0xF6;
    private const byte CheckToken = 0xF5;
    private const int HeaderSize = 8;

    [Fact]
    public async Task ServesEmbeddedContentAndWebUiCompatibleBridge()
    {
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("<h1>managed</h1>");

        using var client = new HttpClient { BaseAddress = url };
        Assert.Equal("<h1>managed</h1>", await client.GetStringAsync("/"));

        var bridge = await client.GetStringAsync("/webui.js");
        Assert.Contains("Object.defineProperty(globalThis, \"webui\"", bridge, StringComparison.Ordinal);
        Assert.Contains("/_webui_ws_connect", bridge, StringComparison.Ordinal);
        Assert.Contains($"const PORT = {url.Port};", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("__TOKEN__", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("__PORT__", bridge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanRestartAfterClose()
    {
        await using var window = new WebUiWindow();
        await window.StartServerAsync("first");
        await window.CloseAsync();
        var secondUrl = await window.StartServerAsync("second");

        Assert.Equal(secondUrl, window.Url);
        using var client = new HttpClient { BaseAddress = secondUrl };
        Assert.Equal("second", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task CloseCompletesWithAnAuthenticatedBridgeConnection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);

        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__"], timeout.Token);

        await window.CloseAsync(timeout.Token);
        Assert.Null(window.Url);
    }

    [Fact]
    public async Task UsesWebUiHandshakeAndCallFraming()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        window.Bind("multiply", static e => e.GetInt64() * e.GetInt64(1));
        window.BindAsync("inspect", static async (e, cancellationToken) =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal((nuint)3, e.ArgumentCount);
            Assert.True(e.GetBoolean(1));
            Assert.Equal([0x41, 0x00, 0x42], e.GetBytes(2));
            return $"{e.GetString()}:{e.GetBoolean(1)}";
        });

        var url = await window.StartServerAsync("<script src=\"webui.js\"></script>");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__", "multiply", "inspect"], timeout.Token);

        await SendBinaryAsync(socket, CreateCallPacket(token, 65535, "multiply", "6"u8.ToArray(), "7"u8.ToArray()), timeout.Token);
        AssertCallResult(await ReceiveBinaryAsync(socket, timeout.Token), 65535, "42");

        await SendBinaryAsync(
            socket,
            CreateCallPacket(token, 65534, "inspect", "managed"u8.ToArray(), "true"u8.ToArray(), [0x41, 0x00, 0x42]),
            timeout.Token);
        AssertCallResult(await ReceiveBinaryAsync(socket, timeout.Token), 65534, "managed:True");
    }

    [Fact]
    public async Task RejectsAnInvalidProtocolToken()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);

        var invalidToken = token == uint.MaxValue ? token - 1 : token + 1;
        await SendBinaryAsync(socket, CreatePacket(invalidToken, 7, CheckToken, [0]), timeout.Token);
        var response = await ReceiveBinaryAsync(socket, timeout.Token);

        AssertHeader(response, 7, CheckToken);
        Assert.Equal(0, response[HeaderSize]);
    }

    [Fact]
    public async Task AnnouncesBindingsAddedAfterAuthentication()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__"], timeout.Token);

        using var binding = window.Bind("lateBinding", static _ => WebUiResult.None);
        var response = await ReceiveBinaryAsync(socket, timeout.Token);

        AssertHeader(response, 0, AddBinding);
        Assert.Equal("lateBinding", ReadNullTerminatedString(response, HeaderSize));
    }

    [Fact]
    public async Task EncodesTypedResultsAsWebUiResponseStrings()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        window.Bind("integer", static _ => WebUiResult.FromInt64(-42));
        window.Bind("floating", static _ => WebUiResult.FromDouble(1.25));
        window.Bind("boolean", static _ => WebUiResult.FromBoolean(true));
        window.Bind("none", static _ => WebUiResult.None);

        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__", "integer", "floating", "boolean", "none"], timeout.Token);

        var expectations = new[]
        {
            (Id: (ushort)1, Name: "integer", Value: "-42"),
            (Id: (ushort)2, Name: "floating", Value: "1.25"),
            (Id: (ushort)3, Name: "boolean", Value: "1"),
            (Id: (ushort)4, Name: "none", Value: string.Empty),
        };
        foreach (var expectation in expectations)
        {
            await SendBinaryAsync(socket, CreateCallPacket(token, expectation.Id, expectation.Name), timeout.Token);
            AssertCallResult(await ReceiveBinaryAsync(socket, timeout.Token), expectation.Id, expectation.Value);
        }
    }

    [Fact]
    public async Task DispatchesConnectionClickNavigationAndDisconnectionEvents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = Channel.CreateUnbounded<(WebUiEventType Type, string Element, string? Value)>();
        await using var window = new WebUiWindow();
        window.Bind(string.Empty, e =>
        {
            events.Writer.TryWrite((e.EventType, e.Element, e.ArgumentCount > 0 ? e.GetString() : null));
        });

        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await SendBinaryAsync(socket, CreatePacket(token, 0, CheckToken, [0]), timeout.Token);
        var handshake = await ReceiveBinaryAsync(socket, timeout.Token);
        AssertHeader(handshake, 0, CheckToken);
        Assert.Equal("__webui_core_api__,,", ReadNullTerminatedString(handshake, HeaderSize + 1));

        Assert.Equal(WebUiEventType.Connected, (await events.Reader.ReadAsync(timeout.Token)).Type);
        await SendBinaryAsync(socket, CreateTextPacket(token, 0, Click, "button"), timeout.Token);
        var clicked = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal((WebUiEventType.MouseClick, "button", null), clicked);

        await SendBinaryAsync(socket, CreateTextPacket(token, 0, Navigation, "https://example.test/path"), timeout.Token);
        var navigated = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal(WebUiEventType.Navigation, navigated.Type);
        Assert.Equal("https://example.test/path", navigated.Value);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
        Assert.Equal(WebUiEventType.Disconnected, (await events.Reader.ReadAsync(timeout.Token)).Type);
    }

    [Fact]
    public async Task SendsQuickJavaScriptRawDataAndNavigationPackets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__"], timeout.Token);

        await window.RunJavaScriptAsync("window.answer = 42", timeout.Token);
        var script = await ReceiveBinaryAsync(socket, timeout.Token);
        AssertHeader(script, 0, JavaScriptQuick);
        Assert.Equal("window.answer = 42", ReadNullTerminatedString(script, HeaderSize));

        await window.SendRawAsync("receiveBytes", new byte[] { 0x41, 0x00, 0x42 }, timeout.Token);
        var raw = await ReceiveBinaryAsync(socket, timeout.Token);
        AssertHeader(raw, 0, SendRaw);
        Assert.Equal("receiveBytes", ReadNullTerminatedString(raw, HeaderSize));
        Assert.Equal([0x41, 0x00, 0x42, 0x00], raw[(HeaderSize + "receiveBytes".Length + 1)..]);

        await window.NavigateAsync("https://example.test/next", timeout.Token);
        var navigation = await ReceiveBinaryAsync(socket, timeout.Token);
        AssertHeader(navigation, 0, Navigation);
        Assert.Equal("https://example.test/next", ReadNullTerminatedString(navigation, HeaderSize));
    }

    [Fact]
    public async Task CorrelatesJavaScriptResultsAndReportsErrors()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__"], timeout.Token);

        var successful = window.ExecuteJavaScriptAsync("return 40 + 2;", TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var request = await ReceiveBinaryAsync(socket, timeout.Token);
        Assert.Equal(JavaScript, request[7]);
        Assert.Equal("return 40 + 2;", ReadNullTerminatedString(request, HeaderSize));
        var id = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(5, 2));
        await SendBinaryAsync(socket, CreatePacket(token, id, JavaScript, [0, (byte)'4', (byte)'2', 0]), timeout.Token);
        Assert.Equal("42", await successful);

        var failed = window.ExecuteJavaScriptAsync("throw new Error('broken');", TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        request = await ReceiveBinaryAsync(socket, timeout.Token);
        id = BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(5, 2));
        await SendBinaryAsync(socket, CreatePacket(token, id, JavaScript, [1, .. "broken"u8.ToArray(), 0]), timeout.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Contains("broken", exception.Message, StringComparison.Ordinal);

        var timedOut = window.ExecuteJavaScriptAsync("return never;", TimeSpan.FromMilliseconds(50));
        request = await ReceiveBinaryAsync(socket, timeout.Token);
        Assert.Equal("return never;", ReadNullTerminatedString(request, HeaderSize));
        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
    }

    [Fact]
    public async Task CallbackCanSynchronouslyExecuteJavaScriptWithoutDeadlock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        window.Bind("roundtrip", e => e.Window.ExecuteJavaScript("return 6 * 7;", TimeSpan.FromSeconds(5)));
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__", "roundtrip"], timeout.Token);

        await SendBinaryAsync(socket, CreateCallPacket(token, 19, "roundtrip"), timeout.Token);
        var script = await ReceiveBinaryAsync(socket, timeout.Token);
        Assert.Equal(JavaScript, script[7]);
        var scriptId = BinaryPrimitives.ReadUInt16LittleEndian(script.AsSpan(5, 2));
        await SendBinaryAsync(socket, CreatePacket(token, scriptId, JavaScript, [0, (byte)'4', (byte)'2', 0]), timeout.Token);

        AssertCallResult(await ReceiveBinaryAsync(socket, timeout.Token), 19, "42");
    }

    [Fact]
    public async Task ReassemblesWebUiMultiPackets()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var window = new WebUiWindow();
        window.Bind("size", static e => (long)e.GetBytes().Length);
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        using var socket = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(socket, token, ["__webui_core_api__", "size"], timeout.Token);

        var argument = Enumerable.Repeat((byte)0x5A, 150_000).ToArray();
        var call = CreateCallPacket(token, 23, "size", argument);
        await SendBinaryAsync(socket, CreateTextPacket(0, 0, Multi, call.Length.ToString(CultureInfo.InvariantCulture)), timeout.Token);
        for (var offset = 0; offset < call.Length; offset += 65_500)
        {
            var length = Math.Min(65_500, call.Length - offset);
            await SendBinaryAsync(socket, call.AsSpan(offset, length).ToArray(), timeout.Token);
        }

        AssertCallResult(await ReceiveBinaryAsync(socket, timeout.Token), 23, "150000");
    }

    [Fact]
    public async Task AcceptsAReconnectedBridgeAndRaisesNewConnectionEvents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = Channel.CreateUnbounded<(WebUiEventType Type, nuint ConnectionId)>();
        await using var window = new WebUiWindow();
        window.Bind(string.Empty, e =>
        {
            events.Writer.TryWrite((e.EventType, e.ConnectionId));
        });
        var url = await window.StartServerAsync("bridge");
        var token = await GetTokenAsync(url);
        nuint firstConnectionId;

        using (var first = await ConnectAsync(url, timeout.Token))
        {
            await AuthenticateAsync(first, token, ["__webui_core_api__"], timeout.Token);
            var connected = await events.Reader.ReadAsync(timeout.Token);
            Assert.Equal(WebUiEventType.Connected, connected.Type);
            firstConnectionId = connected.ConnectionId;
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", timeout.Token);
            var disconnected = await events.Reader.ReadAsync(timeout.Token);
            Assert.Equal((WebUiEventType.Disconnected, connected.ConnectionId), disconnected);
        }

        using var second = await ConnectAsync(url, timeout.Token);
        await AuthenticateAsync(second, token, ["__webui_core_api__"], timeout.Token);
        var reconnected = await events.Reader.ReadAsync(timeout.Token);
        Assert.Equal(WebUiEventType.Connected, reconnected.Type);
        Assert.NotEqual(firstConnectionId, reconnected.ConnectionId);
    }

    private static async Task<uint> GetTokenAsync(Uri url)
    {
        using var client = new HttpClient { BaseAddress = url };
        return ExtractToken(await client.GetStringAsync("/webui.js"));
    }

    private static uint ExtractToken(string bridge)
    {
        const string prefix = "const TOKEN = ";
        var start = bridge.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;
        var end = bridge.IndexOf(';', start);
        Assert.True(end > start);
        return uint.Parse(bridge[start..end], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri url, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        var webSocketUrl = new UriBuilder(url)
        {
            Scheme = "ws",
            Path = "/_webui_ws_connect",
        }.Uri;
        await socket.ConnectAsync(webSocketUrl, cancellationToken);
        return socket;
    }

    private static async Task AuthenticateAsync(
        ClientWebSocket socket,
        uint token,
        string[] expectedBindings,
        CancellationToken cancellationToken)
    {
        await SendBinaryAsync(socket, CreatePacket(token, 0, CheckToken, [0]), cancellationToken);
        var response = await ReceiveBinaryAsync(socket, cancellationToken);

        AssertHeader(response, 0, CheckToken);
        Assert.Equal(1, response[HeaderSize]);
        var csv = ReadNullTerminatedString(response, HeaderSize + 1);
        var bindings = csv.TrimEnd(',').Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expectedBindings, bindings);
    }

    private static byte[] CreateCallPacket(uint token, ushort id, string element, params byte[][] arguments)
    {
        var elementBytes = Encoding.UTF8.GetBytes(element);
        var lengths = Encoding.ASCII.GetBytes(string.Join(';', arguments.Select(static argument => argument.Length)));
        var payloadLength = elementBytes.Length + 1 + lengths.Length + 1 +
            arguments.Sum(static argument => argument.Length + 1);
        var payload = new byte[payloadLength];
        var offset = 0;
        elementBytes.CopyTo(payload, offset);
        offset += elementBytes.Length + 1;
        lengths.CopyTo(payload, offset);
        offset += lengths.Length + 1;
        foreach (var argument in arguments)
        {
            argument.CopyTo(payload, offset);
            offset += argument.Length + 1;
        }

        return CreatePacket(token, id, CallFunction, payload);
    }

    private static byte[] CreateTextPacket(uint token, ushort id, byte command, string value)
        => CreatePacket(token, id, command, [.. Encoding.UTF8.GetBytes(value), 0]);

    private static byte[] CreatePacket(uint token, ushort id, byte command, byte[] payload)
    {
        var packet = new byte[HeaderSize + payload.Length];
        packet[0] = Signature;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    private static void AssertCallResult(byte[] response, ushort id, string expected)
    {
        AssertHeader(response, id, CallFunction);
        Assert.Equal(expected, ReadNullTerminatedString(response, HeaderSize));
    }

    private static void AssertHeader(byte[] response, ushort id, byte command)
    {
        Assert.True(response.Length >= HeaderSize);
        Assert.Equal(Signature, response[0]);
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], response[1..5]);
        Assert.Equal(id, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(5, 2)));
        Assert.Equal(command, response[7]);
    }

    private static string ReadNullTerminatedString(byte[] packet, int offset)
    {
        var data = packet.AsSpan(offset);
        var length = data.IndexOf((byte)0);
        Assert.True(length >= 0);
        return Encoding.UTF8.GetString(data[..length]);
    }

    private static Task SendBinaryAsync(ClientWebSocket socket, byte[] packet, CancellationToken cancellationToken)
        => socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    private static async Task<byte[]> ReceiveBinaryAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var message = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult response;
        do
        {
            response = await socket.ReceiveAsync(buffer, cancellationToken);
            Assert.Equal(WebSocketMessageType.Binary, response.MessageType);
            message.Write(buffer.AsSpan(0, response.Count));
        }
        while (!response.EndOfMessage);

        return message.WrittenSpan.ToArray();
    }
}
