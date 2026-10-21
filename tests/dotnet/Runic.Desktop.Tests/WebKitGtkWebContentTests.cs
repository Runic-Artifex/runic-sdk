using TUnit.Core;
using Runic.Desktop.Internal;
using Decision = Runic.Desktop.Internal.WebContentRules.Decision;

namespace Runic.Desktop.Tests;

// SEC-04 in the WebKitGTK hosts: a navigation WebKit answers with a response is decided by the main frame's response,
// because decide-policy does not say which frame navigates. The native smokes (--web-content) drive real windows.
public sealed class WebKitGtkWebContentTests
{
    private static readonly Uri Surface = new("http://127.0.0.1:5000/surface-1/index.html");

    private static WebKitGtkWebContent Content(DesktopWebContentPolicy? policy = null, bool canIdentifyMainFrame = true) =>
        new(new WebContentRules(policy ?? DesktopWebContentPolicy.Default with { DevTools = false }, Surface, [Surface]), canIdentifyMainFrame);

    [Test]
    public void AnExternalHttpNavigationIsDecidedByTheMainFrameResponseWithItsGesture()
    {
        var content = Content();
        Assert.Equal(Decision.Allow, content.ForNavigationAction("https://example.org/clicked", userGesture: true));
        Assert.Equal(Decision.Allow, content.ForNavigationAction("https://example.org/script", userGesture: false));
        Assert.Equal(Decision.OpenExternally, content.ForMainFrameResponse("https://example.org/clicked"));
        Assert.Equal(Decision.Cancel, content.ForMainFrameResponse("https://example.org/script"));
        // The gesture is used once: a later response for the URL, without a navigation asking first, is cancelled.
        Assert.Equal(Decision.Cancel, content.ForMainFrameResponse("https://example.org/clicked"));
        // The application's documents load.
        Assert.Equal(Decision.Allow, content.ForNavigationAction("http://127.0.0.1:5000/surface-1/next.html", userGesture: false));
        Assert.Equal(Decision.Allow, content.ForMainFrameResponse("http://127.0.0.1:5000/surface-1/next.html"));
    }

    [Test]
    public void ARedirectKeepsTheGestureOfTheNavigationThatStartedIt()
    {
        var content = Content();
        Assert.Equal(Decision.Allow, content.ForNavigationAction("https://example.org/start", userGesture: true));
        Assert.Equal(Decision.Allow, content.ForNavigationAction("https://example.org/start", userGesture: false));
        Assert.Equal(Decision.OpenExternally, content.ForMainFrameResponse("https://example.org/start"));
    }

    [Test]
    public void OtherSchemesAreDecidedAtOnceForEveryFrame()
    {
        var content = Content();
        Assert.Equal(Decision.OpenExternally, content.ForNavigationAction("mailto:someone@example.org", userGesture: true));
        Assert.Equal(Decision.Cancel, content.ForNavigationAction("mailto:someone@example.org", userGesture: false));
        Assert.Equal(Decision.Cancel, content.ForNavigationAction("file:///etc/passwd", userGesture: true));
        Assert.Equal(Decision.Cancel, content.ForNavigationAction("custom-scheme:run", userGesture: true));
        // Without the response API every navigation is decided at once, iframes included.
        var withoutResponses = Content(canIdentifyMainFrame: false);
        Assert.Equal(Decision.OpenExternally, withoutResponses.ForNavigationAction("https://example.org/clicked", userGesture: true));
        Assert.Equal(Decision.Cancel, withoutResponses.ForNavigationAction("https://example.org/script", userGesture: false));
    }

    [Test]
    public void AFailedLoadSuppressesTheErrorPageAndOpensOnlyAWaitingUserNavigation()
    {
        var content = Content();
        content.ForNavigationAction("https://unreachable.example/", userGesture: true);
        Assert.Equal(Decision.OpenExternally, content.ForFailedMainFrameLoad("https://unreachable.example/", out var suppress));
        Assert.True(suppress);
        // The load failure after the response decision cancelled it: suppressed, not opened again.
        content.ForNavigationAction("https://example.org/clicked", userGesture: true);
        Assert.Equal(Decision.OpenExternally, content.ForMainFrameResponse("https://example.org/clicked"));
        Assert.Equal(Decision.Cancel, content.ForFailedMainFrameLoad("https://example.org/clicked", out suppress));
        Assert.True(suppress);
        // The application's own failed load keeps WebKit's error page.
        Assert.Equal(Decision.Cancel, content.ForFailedMainFrameLoad("http://127.0.0.1:5000/surface-1/missing", out suppress));
        Assert.False(suppress);
    }

    [Test]
    public void AllowInWindowAndDownloadsFollowThePolicy()
    {
        var allowInWindow = Content(DesktopWebContentPolicy.Default with { ExternalNavigation = DesktopExternalNavigation.AllowInWindow, DevTools = false });
        Assert.Equal(Decision.Allow, allowInWindow.ForNavigationAction("https://example.org/", userGesture: false));
        Assert.Equal(Decision.Allow, allowInWindow.ForMainFrameResponse("https://example.org/"));
        Assert.Equal(Decision.Cancel, allowInWindow.ForFailedMainFrameLoad("https://example.org/", out var suppress));
        Assert.False(suppress);

        Assert.True(Content().CancelsResponse(mimeTypeSupported: false));
        Assert.False(Content().CancelsResponse(mimeTypeSupported: true));
        Assert.False(Content(DesktopWebContentPolicy.Default with { AllowDownloads = true, DevTools = false }).CancelsResponse(mimeTypeSupported: false));
    }
}
