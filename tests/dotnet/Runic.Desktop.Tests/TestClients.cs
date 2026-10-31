using System.Net;
using System.Net.WebSockets;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// A development tool can request a page from each new loopback listener: t3code's preview port discovery sends GET /
// (and a TLS hello) to every listener a process under its terminal opens, within 3 seconds. A presentation counts only
// requests that prove they came from its page (the window's client cookie or launch code), so such a probe no longer
// moves its progress or deadline (the ProbeAsync cases); it still shows in the timeout's description and log.
//
// While Isolate's scope is open, each presentation server created admits only the test's own requests, which carry a
// header unique to this process (Http, WebSocket); any other request is answered 404 before the presentation sees it and
// written to the case's output. Cases whose page a real browser requests do not isolate. A case stands in for the page
// with clients that share one CookieContainer: after the first response they carry the window's client cookie, as a
// page's requests do. A client without it stands in for a probe.
internal static class TestClients
{
    private const string Header = "X-Runic-Test-Client";
    private static readonly string s_value = Guid.NewGuid().ToString("N");

    internal static HttpClient Http(Uri? baseAddress = null, CookieContainer? cookies = null)
    {
        var client = cookies is null ? new HttpClient() : new HttpClient(new SocketsHttpHandler { CookieContainer = cookies });
        if (baseAddress is not null) client.BaseAddress = baseAddress;
        client.DefaultRequestHeaders.Add(Header, s_value);
        return client;
    }

    internal static ClientWebSocket WebSocket(CookieContainer? cookies = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(Header, s_value);
        socket.Options.Cookies = cookies;
        return socket;
    }

    // A development tool's probe of a new listener, as t3code's preview discovery sends it: GET / without cookies.
    internal static async Task ProbeAsync(Uri url, CancellationToken cancellationToken)
    {
        using var client = Http();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("node");
        using var response = await client.GetAsync(new Uri(url, "/"), cancellationToken);
    }

    internal static IDisposable Isolate()
    {
        var output = new TestOutput();
        if (PresentationServers.Decorator is not null) throw new InvalidOperationException("The presentation servers are isolated already.");
        PresentationServers.Decorator = server => new IsolatedServer(server, output);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => PresentationServers.Decorator = null;
    }

    private sealed class IsolatedServer(IPresentationServer server, TestOutput output) : IPresentationServer
    {
        public IServiceProvider Services => server.Services;

        public Task<int> StartAsync(IPAddress address, int port, Func<PresentationExchange, Task> dispatch, CancellationToken cancellationToken) =>
            server.StartAsync(address, port, exchange =>
            {
                if (exchange.GetRequestHeader(Header) == s_value) return dispatch(exchange);
                output.WriteLine($"Ignored a request from outside the test: {exchange.Method} {exchange.Path} from " +
                    $"{exchange.RemoteIpAddress}:{exchange.RemotePort}, User-Agent '{exchange.GetRequestHeader("User-Agent")}'.");
                exchange.StatusCode = PresentationStatus.NotFound;
                return Task.CompletedTask;
            }, cancellationToken);

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}
