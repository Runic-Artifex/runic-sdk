using TUnit.Core;
using Runic.Desktop.Internal;
using Decision = Runic.Desktop.Internal.WebContentRules.Decision;

namespace Runic.Desktop.Tests;

// SEC-04: what an embedded window does with navigations away from the application, new windows, downloads and
// developer tools. Every built-in host asks these rules.
public sealed class WebContentRulesTests
{
    private static readonly Uri Surface = new("http://127.0.0.1:5000/surface-1/index.html");
    private static readonly Uri[] Additional = [new("http://127.0.0.1:5173"), new("https://app.example")];

    private static WebContentRules Rules(DesktopWebContentPolicy? policy = null) =>
        new(policy ?? DesktopWebContentPolicy.Default with { DevTools = false }, Surface, Additional);

    [Test]
    public void TheDefaultKeepsTheApplicationInTheWindowAndOpensUserLinksExternally()
    {
        var rules = Rules();
        Assert.Equal(DesktopExternalNavigation.OpenInBrowser, DesktopWebContentPolicy.Default.ExternalNavigation);
        Assert.False(DesktopWebContentPolicy.Default.AllowDownloads);
        Assert.Null(DesktopWebContentPolicy.Default.DevTools);
        Assert.False(rules.AllowsDownloads);

        foreach (var url in new[]
        {
            "http://127.0.0.1:5000/surface-1/other.html", "http://127.0.0.1:5000/surface-2/", "http://127.0.0.1:5173/src/main.ts",
            "https://app.example/page", "https://APP.example:443/", "about:blank", "about:srcdoc", "blob:http://127.0.0.1:5000/0b8a",
        })
        {
            Assert.Equal(Decision.Allow, rules.ForNavigation(url, userInitiated: false));
        }
        Assert.Equal(Decision.OpenExternally, rules.ForNavigation("https://example.org/help", userInitiated: true));
        Assert.Equal(Decision.OpenExternally, rules.ForNavigation("mailto:someone@example.org?subject=Hi", userInitiated: true));
        // Another port or host name is another origin, even on loopback.
        Assert.Equal(Decision.OpenExternally, rules.ForNavigation("http://127.0.0.1:5001/", userInitiated: true));
        Assert.Equal(Decision.OpenExternally, rules.ForNavigation("http://localhost:5000/surface-1/", userInitiated: true));
        // A script without a user gesture, or another scheme, never leaves the window.
        Assert.Equal(Decision.Cancel, rules.ForNavigation("https://example.org/help", userInitiated: false));
        foreach (var url in new[]
        {
            "file:///etc/passwd", "ms-settings:display", "javascript:alert(1)",
            "data:text/html,x", "blob:https://example.org/1", "custom-scheme:run", "mailto:a@example.org?attach=/etc/passwd", "not a url", null,
        })
        {
            Assert.Equal(Decision.Cancel, rules.ForNavigation(url, userInitiated: true));
        }
    }

    [Test]
    public void NewWindowsNeverOpenASecondWebView()
    {
        var rules = Rules();
        Assert.Equal(Decision.Cancel, rules.ForNewWindow("http://127.0.0.1:5000/surface-1/popup.html", userInitiated: true));
        Assert.Equal(Decision.Cancel, rules.ForNewWindow("about:blank", userInitiated: true));
        Assert.Equal(Decision.OpenExternally, rules.ForNewWindow("https://example.org/", userInitiated: true));
        Assert.Equal(Decision.Cancel, rules.ForNewWindow("https://example.org/", userInitiated: false));

        var inWindow = Rules(DesktopWebContentPolicy.Default with { ExternalNavigation = DesktopExternalNavigation.AllowInWindow });
        Assert.Equal(Decision.NavigateInWindow, inWindow.ForNewWindow("https://example.org/", userInitiated: false));
        Assert.Equal(Decision.Cancel, inWindow.ForNewWindow("http://127.0.0.1:5000/surface-1/popup.html", userInitiated: true));
        Assert.Equal(Decision.OpenExternally, inWindow.ForNewWindow("mailto:someone@example.org", userInitiated: true));
        Assert.Equal(Decision.Allow, inWindow.ForNavigation("https://example.org/", userInitiated: false));
        Assert.Equal(Decision.Allow, inWindow.ForNavigation("custom-scheme:run", userInitiated: false));
    }

    [Test]
    public void BlockCancelsEverythingOutsideTheApplication()
    {
        var rules = Rules(DesktopWebContentPolicy.Default with { ExternalNavigation = DesktopExternalNavigation.Block, AllowDownloads = true });
        Assert.True(rules.AllowsDownloads);
        Assert.Equal(Decision.Allow, rules.ForNavigation("http://127.0.0.1:5000/surface-1/", userInitiated: true));
        Assert.Equal(Decision.Cancel, rules.ForNavigation("https://example.org/", userInitiated: true));
        Assert.Equal(Decision.Cancel, rules.ForNavigation("mailto:someone@example.org", userInitiated: true));
        Assert.Equal(Decision.Cancel, rules.ForNewWindow("https://example.org/", userInitiated: true));
    }

    [Test]
    public void ExternalTargetsFollowThePlatformLauncherRules()
    {
        Assert.Equal("https://example.org/a?b=c", WebContentRules.ExternalTarget("https://example.org/a?b=c"));
        Assert.Equal("mailto:someone@example.org?subject=Hi", WebContentRules.ExternalTarget("mailto:someone@example.org?subject=Hi"));
        foreach (var url in new[]
        {
            "ms-settings:display", "file:///etc/passwd", "mailto:a@example.org?ATTACHMENT=x", "mailto:a@example.org?%61ttach=x",
            "ftp://example.org/", "relative/path", null,
        })
        {
            Assert.Null(WebContentRules.ExternalTarget(url));
        }
    }

    [Test]
    public void DeveloperToolsDefaultToDevelopmentOnly()
    {
        static Func<string, string?> Environment(string? dotnet, string? aspnet) => name => name switch
        {
            "DOTNET_ENVIRONMENT" => dotnet,
            "ASPNETCORE_ENVIRONMENT" => aspnet,
            _ => null,
        };
        var policy = DesktopWebContentPolicy.Default;
        Assert.True(WebContentRules.Resolve(policy, Environment("Development", null)).DevTools);
        Assert.True(WebContentRules.Resolve(policy, Environment(null, "development")).DevTools);
        Assert.False(WebContentRules.Resolve(policy, Environment("Production", "Development")).DevTools);
        Assert.False(WebContentRules.Resolve(policy, Environment(null, null)).DevTools);
        Assert.True(WebContentRules.Resolve(policy with { DevTools = true }, Environment(null, null)).DevTools);
        Assert.False(WebContentRules.Resolve(policy with { DevTools = false }, Environment("Development", null)).DevTools);
        Assert.True(new WebContentRules(policy with { DevTools = true }, Surface, []).DevToolsEnabled);
        Assert.False(new WebContentRules(policy, Surface, []).DevToolsEnabled);
    }
}
