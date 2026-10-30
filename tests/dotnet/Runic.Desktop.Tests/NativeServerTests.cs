using TUnit.Core;
using Runic.Desktop.Internal;
using Runic.Desktop.Internal.NativeServer;

namespace Runic.Desktop.Tests;

// The managed parts of the experimental native server (X1) that need no native library. The whole suite runs on the
// native server with -p:RunicDesktopServer=Native (.github/workflows/native-server.yml).
public sealed class NativeServerTests
{
    [Test]
    [Arguments("/", "/", "")]
    [Arguments("/a%20b/c.html?x=1&y=%20", "/a b/c.html", "?x=1&y=%20")]
    [Arguments("/a/./b/../c", "/a/c", "")]
    [Arguments("/a/b/..", "/a/", "")]
    [Arguments("/../../etc/passwd", "/etc/passwd", "")]
    [Arguments("/%2e%2e/%2E%2E/secret", "/secret", "")]
    [Arguments("/a%2Fb/c", "/a%2Fb/c", "")]
    [Arguments("/caf%C3%A9", "/café", "")]
    [Arguments("/100%", "/100%", "")]
    public async Task RequestPathsAreDecodedAndNormalizedAsKestrelDoes(string target, string path, string query)
    {
        Assert.True(NativeRequestTarget.TryParse(target, out var parsedPath, out var parsedQuery));
        Assert.Equal(path, parsedPath);
        Assert.Equal(query, parsedQuery);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("*")]
    [Arguments("relative/path")]
    [Arguments("/%C3%28")]
    [Arguments("/%FF")]
    public async Task TargetsThatAreNotAnOriginFormUtf8PathAreAnsweredWithBadRequest(string target)
    {
        Assert.False(NativeRequestTarget.TryParse(target, out _, out _));
        await Task.CompletedTask;
    }

    [Test]
    public async Task KestrelIsTheServerWithoutTheSwitch()
    {
        // The managed suite runs without the switch; the native-server workflow runs it with the switch on.
        var expected = AppContext.TryGetSwitch("Runic.Desktop.NativeServer", out var enabled) && enabled;
        Assert.Equal(expected, PresentationServers.UseNativeServer);
        await using var server = PresentationServers.Create(configureServices: null);
        Assert.IsType(expected ? typeof(NativePresentationServer) : typeof(Runic.Desktop.Internal.Kestrel.KestrelPresentationServer), server);
    }
}
