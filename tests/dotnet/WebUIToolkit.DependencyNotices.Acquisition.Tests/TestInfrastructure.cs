using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DependencyNotices.Acquisition.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "wut-notice-acquisition-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, ValueTask<HttpResponseMessage>> _respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, ValueTask<HttpResponseMessage>> respond) =>
        _respond = respond;

    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri ?? throw new InvalidOperationException("Request URI is missing."));
        return await _respond(request, cancellationToken).ConfigureAwait(false);
    }
}
