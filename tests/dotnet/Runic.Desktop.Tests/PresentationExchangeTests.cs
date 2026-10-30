using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// The server-neutral request and response helpers that replaced ASP.NET Core's HostString, PathString, cookie collections
// and FileExtensionContentTypeProvider in Runic.Desktop (X2). Both servers present requests through them.
public sealed class PresentationExchangeTests
{
    [Test]
    [Arguments("localhost:5000", "localhost", 5000)]
    [Arguments("127.0.0.1:80", "127.0.0.1", 80)]
    [Arguments("[::1]:8080", "[::1]", 8080)]
    [Arguments("localhost", "localhost", null)]
    [Arguments("[::1]", "[::1]", null)]
    [Arguments("localhost:", "localhost", null)]
    [Arguments("localhost:-1", "localhost", null)]
    [Arguments("::1:80", "::1:80", null)]
    [Arguments("", "", null)]
    public async Task HostHeadersSplitAsHostStringDoes(string header, string host, int? port)
    {
        Assert.Equal((host, port), PresentationExchange.ParseHost(header));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("/surface-a", "/surface-a", true, "")]
    [Arguments("/surface-a/", "/surface-a", true, "/")]
    [Arguments("/SURFACE-A/index.html", "/surface-a", true, "/index.html")]
    [Arguments("/surface-ab", "/surface-a", false, "")]
    [Arguments("/other", "/surface-a", false, "")]
    [Arguments("/_runic_launch/code", "/_runic_launch", true, "/code")]
    public async Task PathSegmentsMatchAsPathStringDoes(string path, string segments, bool matches, string remaining)
    {
        Assert.Equal(matches, PresentationExchange.StartsWithSegments(path, segments, out var rest));
        Assert.Equal(remaining, rest);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("webui_auth=abc", "abc")]
    [Arguments("a=1; webui_auth=abc; b=2", "abc")]
    [Arguments("webui_auth=first; webui_auth=second", "first")]
    [Arguments("webui_auth=\"quoted\"", "quoted")]
    [Arguments("webui_auth=a%20b", "a b")]
    [Arguments("xwebui_auth=abc", null)]
    [Arguments("webui_auth", null)]
    [Arguments("", null)]
    public async Task CookiesAreFoundByExactName(string header, string? value)
    {
        Assert.Equal(value, PresentationCookies.Find(header, "webui_auth"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheClientCookieIsStrictAndHttpOnly()
    {
        Assert.Equal(
            "webui_auth=0123abcd; path=/surface-a/; samesite=strict; httponly",
            PresentationCookies.StrictHttpOnly("webui_auth", "0123abcd", "/surface-a/"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task WebSocketSubprotocolsAreSplitAndTrimmed()
    {
        Assert.Equal(["runic-desktop.abc", "other"], PresentationCookies.SplitList(" runic-desktop.abc , ,other"));
        Assert.Empty(PresentationCookies.SplitList(string.Empty));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("/index.html", "text/html")]
    [Arguments("/assets/app.JS", "text/javascript")]
    [Arguments("/app.mjs", "text/javascript")]
    [Arguments("/module.wasm", "application/wasm")]
    [Arguments("/icon.svg", "image/svg+xml")]
    [Arguments("/site.webmanifest", "application/manifest+json")]
    [Arguments("/font.woff2", "font/woff2")]
    [Arguments("/movie.mp4", "video/mp4")]
    public async Task ContentTypesKeepAspNetCoresTable(string path, string contentType)
    {
        Assert.True(PresentationContentTypes.TryGetContentType(path, out var resolved));
        Assert.Equal(contentType, resolved);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("/README")]
    [Arguments("/folder.d/README")]
    [Arguments("/archive.unknown-extension")]
    public async Task UnknownExtensionsHaveNoContentType(string path)
    {
        Assert.False(PresentationContentTypes.TryGetContentType(path, out _));
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheContentTypeTableIsComplete()
    {
        // ASP.NET Core 10's FileExtensionContentTypeProvider has 382 entries; the table was generated from it.
        Assert.Equal(382, PresentationContentTypes.Count);
        await Task.CompletedTask;
    }
}
