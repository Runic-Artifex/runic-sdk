using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// sdk#864, P1a (D062.1, D062.6): a Runic Desktop Window's side panel slot. The panel's page is a second connection of the
// Window's session, so the Window's one ViewModel tree serves both pages; the connection's kind comes from the slot its
// WebSocket was admitted to. The panel is not granted navigation-path/1, so it neither follows nor drives the Window's
// route path; every other feature is the page's, regions (hosted-regions/1) included.
internal sealed class SidePanelConnectionChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Test, NotInParallel(Order = 19)]
    public async Task ThePanelIsASecondConnectionOfTheWindowWithoutTheWindowsPath()
    {
        var services = new ServiceCollection();
        services.AddScoped<ConnectionsProbeViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ConnectionsProbeViewModel, IDisposable>>(
            _ => static (_, _, _) => new NoopAttachment());
        services.AddRunicWindowService();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<ConnectionsProbeWindow>();
        var surface = RunicDesktopHost.GetSurface(window)!;
        var connections = window.ViewModel.Connections;
        using var timeout = TestTimeouts.Cancellation(Timeout);
        var cancellation = timeout.Token;

        // The Window's page: the window's features, navigation-path/1 included.
        // Both views share the window's profile, so the panel's page is the same client as the page (its cookie).
        var cookies = new System.Net.CookieContainer();
        using var page = await DesktopWireClient.ConnectAsync(surface, cancellation, cookies);
        var pageFeatures = Features(await page.RequestAsync(Hello("navigation-path/1", "hosted-regions/1"), cancellation));
        Require(pageFeatures.Contains("navigation-path/1") && pageFeatures.Contains("hosted-regions/1"),
            $"The page's hello did not grant its features: {string.Join(", ", pageFeatures)}");

        // The panel's page, with the panel credential: every feature but navigation-path/1.
        var credential = surface.OpenSidePanelSlot();
        using var panel = await DesktopWireClient.ConnectAsync(surface.Url, cancellation, cookies, credential);
        var panelHello = await panel.RequestAsync(Hello("navigation-path/1", "hosted-regions/1"), cancellation);
        var panelFeatures = Features(panelHello);
        Require(!panelFeatures.Contains("navigation-path/1") && !panelHello.Contains("\"navigationPath\"", StringComparison.Ordinal),
            $"The panel was granted the window's path: {panelHello}");
        Require(panelFeatures.Contains("hosted-regions/1") && panelFeatures.SetEquals(pageFeatures.Where(static feature => feature != "navigation-path/1")),
            $"The panel's features differ from the page's by more than navigation-path/1: {string.Join(", ", panelFeatures)}");
        // Its Back does not navigate the window.
        var back = await panel.RequestAsync("""{"kind":"navigation","action":"back"}""", cancellation);
        Require(back.Contains("\"ok\":false", StringComparison.Ordinal) && back.Contains("navigation-path/1", StringComparison.Ordinal),
            $"The panel's Back was answered {back}");

        // One ViewModel tree, two connections, each with its kind.
        await UntilAsync(() => connections.Current.Count == 2, cancellation);
        Require(connections.Current.Select(static connection => connection.Kind).SequenceEqual([RunicConnectionKind.Page, RunicConnectionKind.SidePanel]),
            $"The connections' kinds were {string.Join(", ", connections.Current.Select(static connection => connection.Kind))}.");
        var session = ((RunicConnections)connections).Session!;
        Require(session.Dispatcher.FindConnection(connections.Current[1].ConnectionKey)?.Kind == RunicConnectionKind.SidePanel,
            "The Window's session did not know the panel's kind.");

        // A second panel is refused while one is connected.
        var refused = false;
        try { using var second = await DesktopWireClient.ConnectAsync(surface.Url, cancellation, cookies, credential); }
        catch (WebSocketException) { refused = true; }
        Require(refused, "A second panel connection was admitted.");

        // Closing the panel disconnects it alone.
        Require(surface.CloseSidePanelSlot(), "The panel slot was not open.");
        Require(await panel.ClosedAsync(cancellation), "The panel's connection stayed open.");
        await UntilAsync(() => connections.Current.Count == 1, cancellation);
        Require(connections.Current[0].Kind == RunicConnectionKind.Page && surface.ConnectionCount == 1,
            "Closing the panel disconnected the page.");
    }

    private static string Hello(params string[] features) =>
        $$"""{"kind":"hello","protocol":4,"session":"{{Guid.NewGuid():N}}","client":{"name":"side-panel-check","version":"0"},"features":[{{string.Join(",", features.Select(static feature => $"\"{feature}\""))}}]}""";

    private static HashSet<string> Features(string hello)
    {
        using var document = JsonDocument.Parse(hello);
        Require(document.RootElement.GetProperty("ok").GetBoolean(), $"The hello failed: {hello}");
        return [.. document.RootElement.GetProperty("features").EnumerateArray().Select(static feature => feature.GetString()!)];
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(10, cancellationToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
