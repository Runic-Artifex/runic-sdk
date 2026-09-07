using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Runic.Application.CsWebUi;

internal static class NativeServerReadiness
{
    internal static async Task WaitAsync(Uri url, TimeSpan deadline, CancellationToken cancellationToken)
    {
        // A TCP connection alone is insufficient: WebUI installs its request
        // handlers after opening the socket. The private readiness route has
        // no body and does not connect a bridge session. WebUI supports GET,
        // but its shipped HTTP handler does not support HEAD.
        // This fixed HTTP/1.x probe talks only to our IPv4 loopback listener.
        // It needs no proxy, cookies, redirects, TLS or general HTTP client
        // stack in the small NativeAOT host.
        byte[] request = Encoding.ASCII.GetBytes($"GET {url.PathAndQuery} HTTP/1.1\r\nHost: {url.Authority}\r\nConnection: close\r\n\r\n");
        byte[] status = new byte[13];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        string lastResponse = "no response";
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try
                {
                    using var client = new TcpClient(AddressFamily.InterNetwork);
                    await client.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token).ConfigureAwait(false);
                    using var stream = client.GetStream();
                    await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                    // The status prefix is bounded; no asset body is read. A
                    // successful response proves the HTTP handler is installed.
                    await stream.ReadExactlyAsync(status, timeout.Token).ConfigureAwait(false);
                    if (status.AsSpan().SequenceEqual("HTTP/1.1 200 "u8) || status.AsSpan().SequenceEqual("HTTP/1.0 200 "u8)) return;
                    lastResponse = Encoding.ASCII.GetString(status).Trim();
                }
                catch (Exception error) when (error is SocketException or IOException)
                {
                    lastResponse = error.Message;
                }
                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"CS-WebUI server at {url} did not become ready within {deadline.TotalSeconds:g} seconds ({lastResponse}).", error);
        }
    }
}
