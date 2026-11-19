using System.Globalization;
using System.Text;
using CsWebUi;
using Runic.Platform;

namespace Runic.Application.Views.CsWebUi;

// cs-webui#32 (T4): the texts of CS-WebUI's own pages (WebUiFileHandlerResult.NotFound, the 500 page) and of WebUI's bridge
// banners, from DesktopPlatformTexts.Current, which Runic.Application.Views.Translations keeps in the application's
// language from the runic-views catalog, with the window's locale as the pages' language. The window's file handler
// answers ScriptPath with WebUiTexts.BannerScript, which runic-views.js loads; WebUI's own 403 page, and its 404 page for
// a file a web root folder does not have, stay English (D061.8).
internal sealed class CsWebUiPageTexts(WebUiWindow window, Func<string?> language)
{
    // Requested by runic-views.js (www/runic-cswebui.js) as the page starts.
    internal const string ScriptPath = "/_runic/cswebui/texts.js";

    private Snapshot? _current;

    private sealed record Snapshot(DesktopPlatformTexts Source, string? Language, WebUiTexts Texts, byte[] Script);

    // The window's texts now; a change of DesktopPlatformTexts.Current or of the window's locale reaches the window's next
    // response.
    internal WebUiTexts Current => Refresh().Texts;

    internal static WebUiTexts From(DesktopPlatformTexts texts, string? language) => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [WebUiTexts.NotFoundTitle] = texts.ResourceUnavailableTitle,
        [WebUiTexts.NotFoundMessage] = texts.ResourceUnavailableMessage,
        [WebUiTexts.ServerErrorTitle] = texts.ServerErrorTitle,
        [WebUiTexts.ServerErrorMessage] = texts.ServerErrorMessage,
        [WebUiTexts.BridgeErrorPrefix] = texts.ConnectionErrorLabel,
        [WebUiTexts.BridgeConnectionFailed] = texts.ConnectionFailedMessage,
        [WebUiTexts.BridgeWebSocketUnsupported] = texts.WebSocketUnsupportedMessage,
        [WebUiTexts.BridgeConnectionLost] = texts.ConnectionLostMessage,
    }, language);

    // A file handler that answers ScriptPath and passes every other path to inner, or to WebUI without one, after giving
    // the window its current texts, so the 404 and 500 pages CS-WebUI answers for inner are in them.
    internal WebUiFileHandler Handler(WebUiFileHandler? inner) => path =>
    {
        var current = Refresh();
        if (string.Equals(path, ScriptPath, StringComparison.Ordinal)) return WebUiFileHandlerResult.FromResponse(current.Script);
        return inner?.Invoke(path) ?? WebUiFileHandlerResult.NotHandled;
    };

    private Snapshot Refresh()
    {
        var source = DesktopPlatformTexts.Current;
        var locale = language();
        var current = Volatile.Read(ref _current);
        if (current is not null && ReferenceEquals(current.Source, source) && string.Equals(current.Language, locale, StringComparison.Ordinal))
            return current;
        var texts = From(source, locale);
        var script = Encoding.UTF8.GetBytes(texts.BannerScript);
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 200 OK\r\nContent-Type: text/javascript; charset=utf-8\r\nContent-Length: {script.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n"));
        var next = new Snapshot(source, locale, texts, [.. header, .. script]);
        window.SetTexts(texts);
        Volatile.Write(ref _current, next);
        return next;
    }
}
