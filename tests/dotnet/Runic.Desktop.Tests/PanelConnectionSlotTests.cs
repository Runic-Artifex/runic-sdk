using TUnit.Core;
using System.Net;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// sdk#864, P1a (D062.1): a window's surface admits a second page connection, the side panel's, with a credential of its
// own. Each slot admits one connection; the slot follows from the credential, never from the page; closing the panel
// revokes its credential; region mode and launch-bound presentations admit no panel.
public sealed class PanelConnectionSlotTests
{
    private const string Page = "<!doctype html><title>Check</title><script src=\"webui.js\"></script>";

    [Test]
    public async Task EachSlotAdmitsOneConnectionWithItsOwnCredential()
    {
        await using var window = await Window.StartAsync();
        var surface = window.Surface;
        var (token, pageCredential) = await BootstrapAsync(surface.Url);
        var panelCredential = surface.OpenSidePanelSlot();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", panelCredential);
        Assert.NotEqual(pageCredential, panelCredential);
        // The served bootstrap carries the page's credential, never the panel's.
        Assert.DoesNotContain(panelCredential, await Http().GetStringAsync(new Uri(surface.Url, "webui.js")));
        Assert.Contains(panelCredential, surface.SidePanelDocumentStartScript!);
        Assert.DoesNotContain(pageCredential, surface.SidePanelDocumentStartScript!);

        using var page = await ConnectedAsync(surface.Url, token, pageCredential);
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        using var panel = await ConnectedAsync(surface.Url, token, panelCredential);
        await WaitUntilAsync(() => surface.ConnectionCount == 2);

        // One connection per slot: a second page and a second panel are refused while theirs is connected.
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(surface.Url, pageCredential));
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(surface.Url, panelCredential));
        // Offering both credentials admits to neither slot; an unknown one is refused as before.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(surface.Url, pageCredential, panelCredential));
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(surface.Url, new string('a', 43)));
        Assert.Equal(2, surface.ConnectionCount);
    }

    [Test]
    public async Task ACredentialNeverTakesTheOtherSlot()
    {
        await using var window = await Window.StartAsync();
        var surface = window.Surface;
        var (token, pageCredential) = await BootstrapAsync(surface.Url);
        var panelCredential = surface.OpenSidePanelSlot();

        // The page's slot is taken and the panel's free: the page credential does not fall into the panel's slot.
        using (var page = await ConnectedAsync(surface.Url, token, pageCredential))
        {
            await WaitUntilAsync(() => surface.ConnectionCount == 1);
            Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(surface.Url, pageCredential));
            await page.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        await WaitUntilAsync(() => surface.ConnectionCount == 0);

        // The panel's slot is taken and the page's free: the panel credential does not fall into the page's slot.
        using var panel = await ConnectedAsync(surface.Url, token, panelCredential);
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        Assert.Equal(HttpStatusCode.Forbidden, await RefusedAsync(surface.Url, panelCredential));
        // The page's slot is still free for the page.
        using var later = await ConnectedAsync(surface.Url, token, pageCredential);
        await WaitUntilAsync(() => surface.ConnectionCount == 2);
    }

    [Test]
    public async Task ClosingRevokesThePanelCredentialAndDisconnectsOnlyThePanel()
    {
        await using var window = await Window.StartAsync();
        var surface = window.Surface;
        var (token, pageCredential) = await BootstrapAsync(surface.Url);
        // No panel yet: a panel-looking credential is just a wrong one.
        Assert.False(surface.CloseSidePanelSlot());
        var first = surface.OpenSidePanelSlot();

        using var page = await ConnectedAsync(surface.Url, token, pageCredential);
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        using (var panel = await ConnectedAsync(surface.Url, token, first))
        {
            await WaitUntilAsync(() => surface.ConnectionCount == 2);
            // A reload of the panel's page is a new connection with the same credential.
            await panel.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        using var reloaded = await ConnectedAsync(surface.Url, token, first);
        await WaitUntilAsync(() => surface.ConnectionCount == 2);

        Assert.True(surface.CloseSidePanelSlot());
        Assert.Null(surface.SidePanelDocumentStartScript);
        await WaitUntilAsync(() => reloaded.State != WebSocketState.Open || surface.ConnectionCount == 1);
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        Assert.Equal(WebSocketState.Open, page.State);
        // Replay: the revoked credential admits nothing, before and after a new open, which mints another.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(surface.Url, first));
        var second = surface.OpenSidePanelSlot();
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(surface.Url, first));
        using var reopened = await ConnectedAsync(surface.Url, token, second);
        await WaitUntilAsync(() => surface.ConnectionCount == 2);

        // Opening again replaces the credential and disconnects the earlier panel's page.
        var third = surface.OpenSidePanelSlot();
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(surface.Url, second));
        using var replaced = await ConnectedAsync(surface.Url, token, third);
        await WaitUntilAsync(() => surface.ConnectionCount == 2);
    }

    [Test]
    public async Task WindowLevelPageOperationsAddressThePageOnly()
    {
        await using var window = await Window.StartAsync();
        var surface = window.Surface;
        var (token, _) = await BootstrapAsync(surface.Url);
        using var panel = await ConnectedAsync(surface.Url, token, surface.OpenSidePanelSlot());
        await WaitUntilAsync(() => surface.ConnectionCount == 1);
        // A side panel's page is not the window's page: the surface's script runs in the page, and there is none.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await surface.ExecuteJavaScriptAsync("1"));
    }

    [Test]
    public async Task RegionModeAdmitsNoPanel()
    {
        await using var window = await Window.StartAsync();
        var surface = window.Surface;
        var credential = surface.OpenSidePanelSlot();
        surface.EnterRegionMode();
        Assert.Throws<InvalidOperationException>(() => surface.OpenSidePanelSlot());

        // The frame alone is admitted in region mode: from the frame's origin, the panel credential is a wrong one.
        var launch = await surface.BeginFrameLaunchAsync("http://127.0.0.1:38200", allowSameSite: true, measuresHeight: false,
            CancellationToken.None);
        string frameToken;
        using (var http = Http())
        using (var redirect = await http.GetAsync(launch.Url))
        {
            var location = redirect.Headers.Location!.OriginalString;
            frameToken = location[(location.IndexOf("#runic-frame=", StringComparison.Ordinal) + "#runic-frame=".Length)..];
        }
        var frame = new Uri(launch.Url, "../");
        Assert.Equal(HttpStatusCode.Unauthorized, await RefusedAsync(frame, credential));
        // The frame token keeps working, also beside the panel credential.
        using var page = await ConnectAsync(frame, credential, frameToken);
        Assert.Equal("runic-desktop." + frameToken, page.SubProtocol);
    }

    [Test]
    public async Task AHostedWindowAdmitsNoPanel()
    {
        await using var window = await Window.StartAsync();
        var surface = window.Surface;
        _ = surface.BeginHostedLaunch();
        Assert.Throws<InvalidOperationException>(() => surface.OpenSidePanelSlot());
    }

    [Test]
    public void TheSlotRevokesEveryEarlierCredential()
    {
        var slot = new PanelConnectionSlot();
        Assert.False(slot.IsOpen);
        Assert.Null(slot.Admit(["runic-desktop.x"], out _));
        var first = slot.Open();
        Assert.Equal("runic-desktop." + first, slot.Admit(["other", "runic-desktop." + first], out var generation));
        Assert.True(slot.Holds(generation));
        Assert.Null(slot.Admit([first], out _));
        var second = slot.Open();
        Assert.False(slot.Holds(generation));
        Assert.Null(slot.Admit(["runic-desktop." + first], out _));
        Assert.NotNull(slot.Admit(["runic-desktop." + second], out generation));
        Assert.True(slot.Close());
        Assert.False(slot.Close());
        Assert.False(slot.Holds(generation));
        Assert.Null(slot.Admit(["runic-desktop." + second], out _));
        Assert.Null(slot.Credential);
    }

    private static HttpClient Http() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

    private static async Task<(uint Token, string Credential)> BootstrapAsync(Uri surface)
    {
        using var http = Http();
        var script = await http.GetStringAsync(new Uri(surface, "webui.js"));
        var token = uint.Parse(Regex.Match(script, @"const TOKEN = (\d+);").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var credential = Regex.Match(script, "const SESSION_CREDENTIAL = \"([A-Za-z0-9_-]+)\";").Groups[1].Value;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", credential);
        return (token, credential);
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri surface, params string[] credentials)
    {
        var socket = new ClientWebSocket();
        foreach (var credential in credentials) socket.Options.AddSubProtocol("runic-desktop." + credential);
        socket.Options.SetRequestHeader("Origin", surface.GetLeftPart(UriPartial.Authority));
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new UriBuilder(new Uri(surface, "_webui_ws_connect")) { Scheme = "ws" }.Uri, timeout.Token);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // A page: its WebSocket with the credential and the Bridge's token check, after which the surface counts it.
    private static async Task<ClientWebSocket> ConnectedAsync(Uri surface, uint token, string credential)
    {
        var socket = await ConnectAsync(surface, credential);
        var packet = new byte[WebUiProtocol.HeaderSize + 1];
        packet[0] = WebUiProtocol.Signature;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), token);
        packet[7] = WebUiProtocol.CheckToken;
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        await socket.SendAsync(packet, WebSocketMessageType.Binary, true, timeout.Token);
        return socket;
    }

    private static async Task<HttpStatusCode> RefusedAsync(Uri surface, params string[] credentials)
    {
        try
        {
            using var socket = await ConnectAsync(surface, credentials);
            throw new InvalidOperationException("The WebSocket was admitted.");
        }
        catch (WebSocketException error) when (error.InnerException is null or HttpRequestException or WebSocketException)
        {
            // ClientWebSocket names the status in its message: "... status code '403' ...".
            var match = Regex.Match(error.Message, @"'(\d{3})'");
            return match.Success ? (HttpStatusCode)int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Window : IAsyncDisposable
    {
        private Window(DesktopHost host, DesktopSurface surface)
        {
            Host = host;
            Surface = surface;
        }

        internal DesktopHost Host { get; }

        internal DesktopSurface Surface { get; }

        internal static async Task<Window> StartAsync()
        {
            var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WaitForConnection = false,
                Security = DesktopSecurityPolicy.Default,
            });
            try
            {
                var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
                return new Window(host, surface);
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Surface.DisposeAsync();
            await Host.DisposeAsync();
        }
    }
}
