using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Runic.Application.Tool;
using Runic.Application.Tool.WebKit;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

/// <summary>
/// dev's loopback listeners on both address families (DEV-SEC-80): <c>--browser</c>'s address and the CDP adapter listen
/// on 127.0.0.1 and <c>[::1]</c>, because a client that connects by a name may try <c>[::1]</c> first, where another local
/// user could otherwise listen. The socket-level checks are in the WindowsCommands category, which the native job's Windows
/// x64 leg runs: Windows binds a port on the two addresses by rules of its own.
/// </summary>
internal sealed partial class ToolTests
{
    private static readonly IPAddress[] BothLoopbacks = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    // Whether this machine has an IPv6 loopback address to listen on; without one, nobody can take [::1] either.
    private static bool HasIpv6Loopback()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
            listener.Start();
            return true;
        }
        catch (SocketException error) when (LoopbackListeners.IsMissingAddress(IPAddress.IPv6Loopback, error))
        {
            return false;
        }
    }

    private static string UrlHost(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    private static Task<HttpResponseMessage> GetFromEntryAt(HttpClient client, BrowserEntry entry, IPAddress address, string target,
        string? host = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{UrlHost(address)}:{entry.Port}{target}"));
        request.Headers.Host = host ?? entry.Host;
        request.Headers.Add("Sec-Fetch-Site", "none");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return client.SendAsync(request);
    }

    [Test, Category("WindowsCommands"), DisplayName("dev's loopback listeners take a port only when it is free on 127.0.0.1 and [::1]")]
    public void LoopbackListenersTakeBothAddresses()
    {
        bool ipv6 = HasIpv6Loopback();
        using (LoopbackListeners listeners = LoopbackListeners.TryStart(0)!)
        {
            SequenceEqual(ipv6 ? BothLoopbacks : [IPAddress.Loopback],
                [.. listeners.Listeners.Select(static listener => ((IPEndPoint)listener.LocalEndpoint).Address)]);
            True(listeners.Listeners.All(listener => ((IPEndPoint)listener.LocalEndpoint).Port == listeners.Port), "one port on both addresses");
            // Taken by dev itself: a second start on the port fails.
            Equal(null, LoopbackListeners.TryStart(listeners.Port));
        }
        // Only a missing IPv6 address lets 127.0.0.1 serve alone; a port in use or refused is taken.
        True(LoopbackListeners.IsMissingAddress(IPAddress.IPv6Loopback, new SocketException((int)SocketError.AddressNotAvailable)), "no ::1");
        True(LoopbackListeners.IsMissingAddress(IPAddress.IPv6Loopback, new SocketException((int)SocketError.AddressFamilyNotSupported)), "no IPv6");
        False(LoopbackListeners.IsMissingAddress(IPAddress.IPv6Loopback, new SocketException((int)SocketError.AddressAlreadyInUse)), "in use");
        False(LoopbackListeners.IsMissingAddress(IPAddress.IPv6Loopback, new SocketException((int)SocketError.AccessDenied)), "refused");
        False(LoopbackListeners.IsMissingAddress(IPAddress.Loopback, new SocketException((int)SocketError.AddressNotAvailable)), "IPv4");
    }

    [Test, Category("WindowsCommands"), DisplayName("a listener of another program on [::1] or 127.0.0.1 takes the port from --browser's address and the CDP adapter")]
    public async Task LoopbackSquatterTakesThePort()
    {
        if (!HasIpv6Loopback()) return;
        foreach (IPAddress squatted in BothLoopbacks)
        {
            IPAddress other = squatted.Equals(IPAddress.Loopback) ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            using var squatter = new TcpListener(squatted, 0);
            squatter.Start();
            int port = ((IPEndPoint)squatter.LocalEndpoint).Port;
            // Neither listens half: the port is taken, and the other address is not left bound.
            Equal(null, BrowserEntry.TryStart(port));
            await using (var adapter = new WebKitDebugEndpoint("127.0.0.1", 1, null, static _ => { }))
                False(adapter.Start(port), $"the CDP adapter listened beside a listener on {squatted}");
            True(CsWebUiDebugging.CanBind(other, port), $"{other}:{port} was left bound");
            // dev moves to a port free on both, as when 9224 is taken, and says why.
            await using BrowserEntry moved = BrowserEntry.TryStart(0)!;
            True(moved.Port != port, "the entry took the squatted port");
            SequenceEqual(BothLoopbacks, moved.Addresses);
        }
        Contains(DevApplication.BrowserHint(40001, $"http://{TestLabel}.localhost:40001/?code=abc"),
            "Port 9224 was taken on 127.0.0.1 or [::1], so the address moved to a port free on both");
    }

    [Test, Category("WindowsCommands"), DisplayName("--browser's address answers on 127.0.0.1 and [::1] with the same Host, code and cookie rules, for one admitted browser")]
    public async Task BrowserEntryHasTheSameRulesOnBothAddresses()
    {
        await using BrowserEntry entry = BrowserEntry.TryStart(0)!;
        if (!HasIpv6Loopback())
        {
            SequenceEqual([IPAddress.Loopback], entry.Addresses);
            return;
        }
        SequenceEqual(BothLoopbacks, entry.Addresses);
        entry.Observe("RUNIC_APPLICATION_URL=" + SurfaceUrl);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        string? previous = null;
        foreach (IPAddress address in BothLoopbacks)
        {
            string code = entry.Code;
            // No code, another name or a wrong code: 403, with nothing that names the label.
            using (HttpResponseMessage none = await GetFromEntryAt(client, entry, address, "/"))
                Equal(HttpStatusCode.Forbidden, none.StatusCode);
            foreach (string host in new[] { $"127.0.0.1:{entry.Port}", $"[::1]:{entry.Port}", $"localhost:{entry.Port}", $"other.localhost:{entry.Port}" })
            {
                using HttpResponseMessage other = await GetFromEntryAt(client, entry, address, "/?code=" + code, host);
                Equal(HttpStatusCode.Forbidden, other.StatusCode);
                DoesNotContain(await other.Content.ReadAsStringAsync(), entry.Label);
                False(other.Headers.Contains("Set-Cookie"), $"{host} on {address} was admitted");
            }
            Equal(code, entry.Code);
            using (HttpResponseMessage wrong = await GetFromEntryAt(client, entry, address, "/?code=guess"))
                Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            // The code admits, once; the cookie's next navigation confirms the admission.
            using HttpResponseMessage admitted = await GetFromEntryAt(client, entry, address, "/?code=" + code);
            Equal(HttpStatusCode.OK, admitted.StatusCode);
            string cookie = admitted.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            True(entry.Code != code, "the admission replaced the code");
            using (HttpResponseMessage used = await GetFromEntryAt(client, entry, address, "/?code=" + code))
                Equal(HttpStatusCode.Forbidden, used.StatusCode);
            using (HttpResponseMessage confirmed = await GetFromEntryAt(client, entry, address, "/", cookie: cookie))
                True(confirmed.StatusCode != HttpStatusCode.Forbidden, $"the admitted browser on {address}: {confirmed.StatusCode}");
            // One admitted browser across both addresses: this admission ended the one made on the other address.
            if (previous is not null)
            {
                foreach (IPAddress any in BothLoopbacks)
                {
                    using HttpResponseMessage ended = await GetFromEntryAt(client, entry, any, "/", cookie: previous);
                    Equal(HttpStatusCode.Forbidden, ended.StatusCode);
                }
            }
            previous = cookie;
        }
    }

    [Test, DisplayName("on Linux, --browser's address refuses another user and admits the developer on [::1] too, from /proc/net/tcp6")]
    public async Task BrowserEntryChecksThePeerOnIpv6()
    {
        if (!OperatingSystem.IsLinux() || !HasIpv6Loopback()) return;
        var reported = new List<string>();
        await using (BrowserEntry foreign = BrowserEntry.TryStart(0,
            ForeignOwner(static row => row.State == 1 && row.Remote.Port != 0), line => { lock (reported) reported.Add(line); })!)
        {
            foreign.Observe("RUNIC_APPLICATION_URL=" + SurfaceUrl);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using HttpResponseMessage refused = await GetFromEntryAt(client, foreign, IPAddress.IPv6Loopback, "/?code=" + foreign.Code);
            Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            False(refused.Headers.Contains("Set-Cookie"), "another user's request on [::1] is not admitted, even with the code");
            lock (reported) Contains(reported.Single(), "from another user (uid 4242)");
        }
        reported.Clear();
        await using BrowserEntry entry = BrowserEntry.TryStart(0, LoopbackSockets.Current, line => { lock (reported) reported.Add(line); })!;
        entry.Observe("RUNIC_APPLICATION_URL=" + SurfaceUrl);
        using var own = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage admitted = await GetFromEntryAt(own, entry, IPAddress.IPv6Loopback, "/?code=" + entry.Code);
        Equal(HttpStatusCode.OK, admitted.StatusCode);
        lock (reported) Equal(0, reported.Count);
    }

    [Test, DisplayName("the CDP adapter answers on 127.0.0.1 and [::1] with the same Origin and Host rules")]
    public async Task CdpEndpointHasTheSameRulesOnBothAddresses()
    {
        using var server = new FakeInspectorServer();
        await using var endpoint = new WebKitDebugEndpoint("127.0.0.1", server.Port, LoopbackSockets.Current, static _ => { });
        True(endpoint.Start(0), "the endpoint listens");
        int port = endpoint.Port!.Value;
        if (!HasIpv6Loopback()) return;
        using var http = new HttpClient();
        foreach (IPAddress address in BothLoopbacks)
        {
            string url = $"http://{UrlHost(address)}:{port}/json/version";
            // The WebSocket address follows the Host the debugger used.
            string browser = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync(url))!["webSocketDebuggerUrl"]!.GetValue<string>();
            StartsWith(browser, $"ws://{UrlHost(address)}:{port}/devtools/browser/");
            using (var foreignOrigin = new HttpRequestMessage(HttpMethod.Get, url))
            {
                foreignOrigin.Headers.Add("Origin", "https://example.com");
                Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreignOrigin)).StatusCode);
            }
            using var foreignHost = new HttpRequestMessage(HttpMethod.Get, url);
            foreignHost.Headers.Host = $"example.com:{port}";
            Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreignHost)).StatusCode);
        }
    }

    [Test, DisplayName("a taken CDP port names a holder on [::1] as on 127.0.0.1")]
    public void CdpPortHolderOnEitherAddress()
    {
        // Another user's listener on [::1]:9222 only.
        const string ipv6 = """
              sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
               0: 00000000000000000000000001000000:2406 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000  1001        0 900 1 0000000000000000 100 0 0 10 0
            """;
        var sockets = new LoopbackSockets(path => path.EndsWith("tcp6", StringComparison.Ordinal) ? ipv6 : null, 1000);
        Equal(((uint?)1001, (string?)null), PortHolder.OnLoopback(sockets, 9222));
        Equal(((uint?)null, (string?)null), PortHolder.OnLoopback(sockets, 9333));
        Equal(((uint?)null, (string?)null), PortHolder.OnLoopback(null, 9222));
        Equal((uint?)1001, WebKitDebugging.DefaultPortTaken.Find(sockets, Environment.CurrentDirectory).Owner);
    }
}
