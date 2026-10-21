using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// SEC-04 in the macOS WKWebView window: what its navigation and UI delegate answer WebKit. These run on every OS; the
// native delegate on a real WKWebView is covered by tests/native/Runic.Desktop.WebViewSmoke on the macOS CI runner.
public sealed unsafe class MacOsWebContentTests
{
    private const string Page = "http://127.0.0.1:5000/surface-1/";
    private const nint LinkActivated = 0, FormSubmitted = 1, Reload = 3, Other = -1;
    private static readonly WebContentRules Rules = new(DesktopWebContentPolicy.Default with { DevTools = false }, new Uri(Page), []);

    [Test]
    public void WebKitConstantsMatchTheirHeaders()
    {
        // WKNavigationActionPolicy / WKNavigationResponsePolicy: Cancel 0, Allow 1; WKNavigationTypeLinkActivated 0.
        Assert.Equal(0, (int)MacOsWebContent.PolicyCancel);
        Assert.Equal(1, (int)MacOsWebContent.PolicyAllow);
        Assert.Equal(0, (int)MacOsWebContent.NavigationTypeLinkActivated);
        // isa, int flags, int reserved, then invoke: offset 16 on 64-bit.
        Assert.Equal(16, (int)Marshal.OffsetOf<MacOsWebContent.BlockLiteral>(nameof(MacOsWebContent.BlockLiteral.Invoke)));
    }

    [Test]
    public void TopLevelNavigationsFollowTheRules()
    {
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyAllow), Decide(Page + "other.html", true, Other));
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyAllow), Decide(Page, true, Reload));
        // A clicked link leaves for the browser; a script, a form or a reload does not.
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyCancel, OpenExternally: "https://example.org/help"),
            Decide("https://example.org/help", true, LinkActivated));
        foreach (var type in new[] { Other, FormSubmitted, Reload })
            Assert.Equal(MacOsWebContent.Cancel, Decide("https://example.org/help", true, type));
        Assert.Equal(MacOsWebContent.Cancel, Decide("file:///etc/passwd", true, LinkActivated));
        Assert.Equal(MacOsWebContent.Cancel, Decide(null, true, LinkActivated));
    }

    [Test]
    public void FramesAreNotDecidedButDownloadsAreCancelledEverywhere()
    {
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyAllow), Decide("https://example.org/embed", false, Other));
        Assert.Equal(MacOsWebContent.Cancel, Decide(Page + "file.zip", true, LinkActivated, download: true));
        Assert.Equal(MacOsWebContent.Cancel, Decide("https://example.org/embed.zip", false, Other, download: true));
        var allowing = new WebContentRules(DesktopWebContentPolicy.Default with { AllowDownloads = true, DevTools = false }, new Uri(Page), []);
        Assert.Equal(MacOsWebContent.Cancel, MacOsWebContent.ForNavigationAction(allowing, Page + "file.zip", true, LinkActivated, true));
    }

    [Test]
    public void ANavigationWithoutATargetFrameIsANewWindowRequest()
    {
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyCancel, OpenExternally: "https://example.org/"),
            Decide("https://example.org/", null, LinkActivated));
        Assert.Equal(MacOsWebContent.Cancel, Decide("https://example.org/", null, Other));
        // An application document never opens a second window either.
        Assert.Equal(MacOsWebContent.Cancel, Decide(Page + "other.html", null, LinkActivated));

        var inWindow = new WebContentRules(DesktopWebContentPolicy.Default with { ExternalNavigation = DesktopExternalNavigation.AllowInWindow, DevTools = false },
            new Uri(Page), []);
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyCancel, NavigateInWindow: "https://example.org/"),
            MacOsWebContent.ForNavigationAction(inWindow, "https://example.org/", null, Other, false));
        Assert.Equal(new MacOsWebContent.Outcome(MacOsWebContent.PolicyAllow),
            MacOsWebContent.ForNavigationAction(inWindow, "https://example.org/", true, Other, false));

        var blocking = new WebContentRules(DesktopWebContentPolicy.Default with { ExternalNavigation = DesktopExternalNavigation.Block, DevTools = false },
            new Uri(Page), []);
        Assert.Equal(MacOsWebContent.Cancel, MacOsWebContent.ForNewWindow(blocking, "https://example.org/", userInitiated: true));
        Assert.Equal(MacOsWebContent.Cancel, MacOsWebContent.ForNavigationAction(blocking, "https://example.org/", true, LinkActivated, false));
    }

    [Test]
    public void ResponsesWebKitCannotShowAreCancelled()
    {
        Assert.Equal(MacOsWebContent.PolicyAllow, MacOsWebContent.ForNavigationResponse(canShowMimeType: true));
        Assert.Equal(MacOsWebContent.PolicyCancel, MacOsWebContent.ForNavigationResponse(canShowMimeType: false));
    }

    [Test]
    public void ThePolicyReachesTheDecisionHandlerBlock()
    {
        var block = new MacOsWebContent.BlockLiteral
        {
            Flags = 1 << 28, // BLOCK_IS_GLOBAL
            Invoke = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&RecordPolicy,
        };
        s_calls = 0;
        MacOsWebContent.InvokePolicyHandler((nint)(&block), MacOsWebContent.PolicyAllow);
        Assert.Equal(((nint)(&block), MacOsWebContent.PolicyAllow, 1), (s_block, s_policy, s_calls));
        MacOsWebContent.InvokePolicyHandler((nint)(&block), MacOsWebContent.PolicyCancel);
        Assert.Equal((MacOsWebContent.PolicyCancel, 2), (s_policy, s_calls));
        // WebKit always passes a handler; a missing one is not called.
        MacOsWebContent.InvokePolicyHandler(0, MacOsWebContent.PolicyAllow);
        Assert.Equal(2, s_calls);
    }

    private static nint s_block, s_policy;
    private static int s_calls;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RecordPolicy(nint block, nint policy)
    {
        s_block = block;
        s_policy = policy;
        s_calls++;
    }

    private static MacOsWebContent.Outcome Decide(string? url, bool? mainFrame, nint type, bool download = false) =>
        MacOsWebContent.ForNavigationAction(Rules, url, mainFrame, type, download);
}
