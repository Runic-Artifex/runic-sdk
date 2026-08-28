using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Desktop.Tests;

public sealed class DesktopApiTests
{
    private const int PacketHeaderSize = 8;

    [Fact]
    public void StructuredPayloadRejectsDuplicateKeysWithStableRedactedError()
    {
        var error = Assert.Throws<DesktopException>(() => StructuredPayload.Parse(
            "example.message/1",
            "{\"name\":\"first\",\"name\":\"second\"}"u8));

        Assert.Equal(DesktopErrorCategory.InvalidFrame, error.Category);
        Assert.Equal("duplicate-object-key", error.Code);
        Assert.Equal("The structured payload contains a duplicate object key.", error.Message);
        Assert.False(error.Retryable);
    }

    [Fact]
    public void PublicApiContainsNoWebUiCompatibilityIdentity()
    {
        var exportedTypes = typeof(DesktopHost).Assembly.ExportedTypes
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.DoesNotContain(
            exportedTypes,
            static name => name.Contains("WebUi", StringComparison.OrdinalIgnoreCase));

        var baseline = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Runic.Desktop.PublicApi.txt"))
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(baseline, exportedTypes);
    }

    [Fact]
    public async Task CapabilityFailuresProduceOnlyStableRedactedDiagnostics()
    {
        var diagnostics = new ConcurrentQueue<DesktopDiagnostic>();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            DiagnosticSink = diagnostics.Enqueue,
        });
        await using var surface = await host.CreateSurfaceAsync();
        using var registration = surface.RegisterCapability(
            "sample.failure",
            static (_, _) => throw new InvalidOperationException("secret-stack"));
        using var client = new HttpClient();
        var bridge = await client.GetStringAsync(new Uri(surface.Url, "webui.js"));
        var token = ExtractUnsigned(bridge, "const TOKEN = ");
        var credential = ExtractQuoted(bridge, "const SESSION_CREDENTIAL = \"");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"{surface.Url.Scheme}://{surface.Url.Authority}");
        socket.Options.AddSubProtocol($"runic-desktop.{credential}");
        await socket.ConnectAsync(ToWebSocketUrl(surface.Url), timeout.Token);

        await SendPacketAsync(socket, CreatePacket(token, 0, 0xF5, [0]), timeout.Token);
        _ = await ReceivePacketAsync(socket, timeout.Token);
        await SendPacketAsync(
            socket,
            CreatePacket(token, 1, 0xF9, [.. "sample.failure\0\0"u8]),
            timeout.Token);
        _ = await ReceivePacketAsync(socket, timeout.Token);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DesktopErrorCategory.OperationFailed, diagnostic.Category);
        Assert.Equal("capability-failed", diagnostic.Code);
        Assert.Equal("The presentation capability failed.", diagnostic.Message);
        Assert.DoesNotContain("secret-stack", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostSharesListenerAndCreatesARequestScopePerRequest()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            ConfigureServices = services => services.AddScoped<RequestMarker>(),
        });
        await using var first = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = "first",
            ContentHandler = static (request, _) => ValueTask.FromResult<ContentResponse?>(
                ContentResponse.Text($"first:{request.Services.GetRequiredService<RequestMarker>().Id}")),
        });
        await using var second = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            Path = "second",
            ContentHandler = static (request, _) => ValueTask.FromResult<ContentResponse?>(
                ContentResponse.Text($"second:{request.Services.GetRequiredService<RequestMarker>().Id}")),
        });
        await using var isolated = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            UseIsolatedListener = true,
            Content = "isolated",
        });

        Assert.Equal(first.Url.Port, second.Url.Port);
        Assert.NotEqual(first.Url.Port, isolated.Url.Port);
        using var client = new HttpClient();
        var firstRequest = await client.GetStringAsync(first.Url);
        var secondRequest = await client.GetStringAsync(first.Url);
        Assert.StartsWith("first:", firstRequest, StringComparison.Ordinal);
        Assert.NotEqual(firstRequest, secondRequest);
        Assert.StartsWith("second:", await client.GetStringAsync(second.Url), StringComparison.Ordinal);

        var firstUrl = first.Url;
        await first.CloseAsync();
        using var closedResponse = await client.GetAsync(firstUrl);
        Assert.Equal(HttpStatusCode.NotFound, closedResponse.StatusCode);
        Assert.StartsWith("second:", await client.GetStringAsync(second.Url), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicStreamingResponseDoesNotOpenForHead()
    {
        var factoryCalls = 0;
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            ContentHandler = (request, _) => ValueTask.FromResult<ContentResponse?>(
                request.Path == "/stream"
                    ? ContentResponse.Stream(
                        _ =>
                        {
                            Interlocked.Increment(ref factoryCalls);
                            return ValueTask.FromResult<Stream>(new MemoryStream("stream"u8.ToArray()));
                        },
                        contentLength: 6)
                    : null),
        });
        using var client = new HttpClient();

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, new Uri(surface.Url, "stream"));
        using var headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(6, headResponse.Content.Headers.ContentLength);
        Assert.Equal(0, Volatile.Read(ref factoryCalls));

        Assert.Equal("stream", await client.GetStringAsync(new Uri(surface.Url, "stream")));
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
    }

    [Fact]
    public async Task ClosingSurfaceReportsCauseAndReleasesPublicStream()
    {
        ContentRequest? observedRequest = null;
        var stream = new CancellationProbeStream();
        await using var host = await DesktopHost.StartAsync();
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
        {
            ContentHandler = (request, _) =>
            {
                observedRequest = request;
                return ValueTask.FromResult<ContentResponse?>(
                    request.Path == "/stream"
                        ? ContentResponse.Stream(_ => ValueTask.FromResult<Stream>(stream))
                        : null);
            },
        });
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            new Uri(surface.Url, "stream"),
            HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[1];
        Assert.Equal(1, await body.ReadAsync(buffer));

        await surface.CloseAsync();

        Assert.Equal(RequestCancellationReason.SurfaceClosing, observedRequest?.CancellationReason);
        await stream.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class RequestMarker
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    private static uint ExtractUnsigned(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf(';', start);
        return uint.Parse(script[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ExtractQuoted(string script, string prefix)
    {
        var start = script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var end = script.IndexOf('"', start);
        return script[start..end];
    }

    private static Uri ToWebSocketUrl(Uri surfaceUrl) => new UriBuilder(surfaceUrl)
    {
        Scheme = "ws",
        Path = $"{surfaceUrl.AbsolutePath}_webui_ws_connect",
    }.Uri;

    private static byte[] CreatePacket(uint token, ushort id, byte command, byte[] payload)
    {
        var packet = new byte[PacketHeaderSize + payload.Length];
        packet[0] = 0xDD;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), id);
        packet[7] = command;
        payload.CopyTo(packet, PacketHeaderSize);
        return packet;
    }

    private static Task SendPacketAsync(
        ClientWebSocket socket,
        byte[] packet,
        CancellationToken cancellationToken) =>
        socket.SendAsync(packet, WebSocketMessageType.Binary, true, cancellationToken);

    private static async Task<byte[]> ReceivePacketAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var packet = new ArrayBufferWriter<byte>();
        WebSocketReceiveResult response;
        do
        {
            response = await socket.ReceiveAsync(buffer, cancellationToken);
            packet.Write(buffer.AsSpan(0, response.Count));
        }
        while (!response.EndOfMessage);
        return packet.WrittenSpan.ToArray();
    }

    private sealed class CancellationProbeStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _read;

        internal Task Disposed => _disposed.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _read, 1) == 0)
            {
                buffer.Span[0] = (byte)'x';
                return 1;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            await base.DisposeAsync();
        }
    }
}
