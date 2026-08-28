using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Desktop.Tests;

public sealed class DesktopApiTests
{
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

        Assert.Equal(first.Url.Port, second.Url.Port);
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
