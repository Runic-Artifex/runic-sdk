using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Desktop;
using Runic.Desktop;
using Runic.Application.Bridge;

namespace Runic.Application.Desktop.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            await RoundTripAndReconnectAsync().ConfigureAwait(false);
            Console.WriteLine("ok - Desktop transport preserves Application Bridge session and reconnect semantics");
            await ServerOnlyHostCancellationAsync().ConfigureAwait(false);
            Console.WriteLine("ok - Application host maps cancellation and deterministic teardown");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"not ok - Runic Application Desktop integration\n{exception}");
            return 1;
        }
    }

    private static async Task RoundTripAndReconnectAsync()
    {
        await using var host = await DesktopHost.StartAsync().ConfigureAwait(false);
        await using var surface = await host.CreateSurfaceAsync().ConfigureAwait(false);
        var session = new ApplicationBridgeSession(new FakeDispatcher());
        await using var bridge = DesktopApplicationBridge.Attach(surface, session);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cookies = new CookieContainer();

        JsonElement first = await InitializeAsync(surface, 0, cookies, timeout.Token).ConfigureAwait(false);
        Equal("snapshot", first.GetProperty("kind").GetString());
        ulong firstPresentation = bridge.PresentationSessionId ?? throw new InvalidOperationException("No presentation was admitted.");

        JsonElement second = await InitializeAsync(surface, 1, cookies, timeout.Token).ConfigureAwait(false);
        Equal("snapshot", second.GetProperty("kind").GetString());
        Equal(1L, second.GetProperty("connectionEpoch").GetInt64());
        if (bridge.PresentationSessionId == firstPresentation)
            throw new InvalidOperationException("A reconnect must acquire a fresh presentation session.");
    }

    private static async Task<JsonElement> InitializeAsync(
        DesktopSurface surface,
        long epoch,
        CookieContainer cookies,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler);
        string bootstrap = await client.GetStringAsync(new Uri(surface.Url, "runic-desktop.js"), cancellationToken).ConfigureAwait(false);
        uint token = ExtractUnsigned(bootstrap, "token: ");
        string credential = ExtractQuoted(bootstrap, "sessionCredential: \"");
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        socket.Options.SetRequestHeader("Origin", $"{surface.Url.Scheme}://{surface.Url.Authority}");
        socket.Options.AddSubProtocol($"runic-desktop.{credential}");
        await socket.ConnectAsync(ToWebSocketUrl(surface.Url), cancellationToken).ConfigureAwait(false);
        await SendAsync(socket, CreatePacket(token, 7, 0xf5, [0]), cancellationToken).ConfigureAwait(false);
        byte[] authenticated = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        Equal((byte)1, authenticated[8]);
        string capabilities = ReadText(authenticated, 9);
        if (!capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(DesktopApplicationBridgeOptions.Capability))
            throw new InvalidOperationException("The Desktop surface did not advertise the Application Bridge capability.");

        byte[] frame = Encoding.UTF8.GetBytes($$$"""
            {"protocol":"runic.test","version":1,"contractFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","connectionEpoch":{{{epoch}}},"kind":"initialize","commandId":"{{{Guid.NewGuid()}}}","payload":{"_tag":"InitializeApplication"}}
            """);
        await SendAsync(socket, CreateCall(token, 1, DesktopApplicationBridgeOptions.Capability, frame), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            byte[] packet = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (packet.Length >= 9 && packet[7] == 0xf8 && ReadText(packet, 8) == DesktopApplicationBridgeOptions.Receiver)
            {
                int start = 8 + Encoding.UTF8.GetByteCount(DesktopApplicationBridgeOptions.Receiver) + 1;
                int length = packet.Length - start - 1;
                JsonElement response = JsonDocument.Parse(packet.AsMemory(start, length)).RootElement.Clone();
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", cancellationToken).ConfigureAwait(false);
                return response;
            }
        }
    }

    private static async Task ServerOnlyHostCancellationAsync()
    {
        var applicationHost = new DesktopApplicationHost(new DesktopApplicationHostOptions
        {
            OpenWindow = false,
            Surface = new DesktopSurfaceOptions { Content = "server-only" },
        });
        await using (applicationHost.ConfigureAwait(false))
        {
            var manifest = new ApplicationCompositionManifest(
                "runic.test.application",
                "entry",
                "test",
                [],
                []);
            await applicationHost.StartAsync(manifest, ReadOnlyMemory<string>.Empty, CancellationToken.None).ConfigureAwait(false);
            if (applicationHost.Surface is null) throw new InvalidOperationException("The Desktop surface did not start.");
            await using DesktopSurface additional = await applicationHost.Host!.CreateSurfaceAsync(
                new DesktopSurfaceOptions { Content = "additional-window-surface" }).ConfigureAwait(false);
            if (additional.Url.Port != applicationHost.Surface.Url.Port)
                throw new InvalidOperationException("Application-owned surfaces did not share the Desktop listener.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool observed = false;
            try { await applicationHost.WaitForShutdownAsync(cancelled.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { observed = true; }
            if (!observed) throw new InvalidOperationException("Server-only shutdown did not observe cancellation.");
            await applicationHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (applicationHost.Surface is not null) throw new InvalidOperationException("The Desktop surface survived stop.");
        }
    }

    private static byte[] CreateCall(uint token, ushort id, string capability, byte[] frame)
    {
        byte[] name = Encoding.UTF8.GetBytes(capability);
        byte[] lengths = Encoding.ASCII.GetBytes(frame.Length.ToString(CultureInfo.InvariantCulture));
        var payload = new byte[name.Length + 1 + lengths.Length + 1 + frame.Length + 1];
        int offset = 0;
        name.CopyTo(payload, offset);
        offset += name.Length + 1;
        lengths.CopyTo(payload, offset);
        offset += lengths.Length + 1;
        frame.CopyTo(payload, offset);
        return CreatePacket(token, id, 0xf9, payload);
    }

    private static byte[] CreatePacket(uint token, ushort id, byte command, byte[] payload)
    {
        var packet = new byte[8 + payload.Length];
        packet[0] = 0xdd;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, 8);
        return packet;
    }

    private static Task SendAsync(ClientWebSocket socket, byte[] bytes, CancellationToken token) =>
        socket.SendAsync(bytes, WebSocketMessageType.Binary, true, token);

    private static async Task<byte[]> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var output = new ArrayBufferWriter<byte>();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            output.Write(buffer.AsSpan(0, result.Count));
        }
        while (!result.EndOfMessage);
        return output.WrittenSpan.ToArray();
    }

    private static Uri ToWebSocketUrl(Uri surfaceUrl) => new UriBuilder(surfaceUrl)
    {
        Scheme = "ws",
        Path = $"{surfaceUrl.AbsolutePath}_webui_ws_connect",
    }.Uri;

    private static uint ExtractUnsigned(string script, string prefix)
    {
        int start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = script.IndexOf(',', start);
        return uint.Parse(script[start..end], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static string ExtractQuoted(string script, string prefix)
    {
        int start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        int end = script.IndexOf('"', start);
        return script[start..end];
    }

    private static string ReadText(byte[] packet, int offset)
    {
        int length = packet.AsSpan(offset).IndexOf((byte)0);
        return Encoding.UTF8.GetString(packet, offset, length < 0 ? packet.Length - offset : length);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
    }
}

internal sealed class FakeDispatcher : IApplicationBridgeDispatcher
{
    public string ProtocolIdentity => "runic.test";
    public int ProtocolVersion => 1;
    public string ManifestFingerprint => new('a', 64);

    public ValueTask<BridgeDispatchResult> DispatchAsync(
        JsonElement command,
        BridgeCommandContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new BridgeDispatchResult(
            JsonDocument.Parse("""{"_tag":"ApplicationInitialized","snapshot":{"revision":0}}""").RootElement.Clone()));
}
