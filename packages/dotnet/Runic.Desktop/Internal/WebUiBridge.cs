namespace Runic.Desktop.Internal;

internal static class WebUiBridge
{
    // The reserved raw-data function of Views messages (WebUiSession.PushViewsMessageAsync). The script handles it
    // itself; a page cannot replace it. Keep it in sync with VIEWS_PUSH in WebUiBridge.js.
    internal const string ViewsPushFunction = "__runicViewsPush";

    // The reserved raw-data function of DesktopWindow.AnnounceAsync while the page has the keyboard focus
    // (WebUiSession.AnnounceAsync): the politeness, NUL and the message; an empty message adds the page's live regions.
    // Keep it in sync with ANNOUNCE in WebUiBridge.js.
    internal const string AnnounceFunction = "__runicAnnounce";

    // The reserved raw-data function of a region's focus (WebUiSession.SendRegionAsync): the direction in which the
    // keyboard focus enters the frame's page. Keep it in sync with REGION in WebUiBridge.js.
    internal const string RegionFunction = "__runicRegion";

    // Implements the wire and public browser contract of webui-dev/webui's
    // MIT-licensed TypeScript bridge at the pinned upstream revision. One difference: raw data for a name that is not
    // a page function is dropped instead of being evaluated as a call expression, so no host-to-page traffic but
    // CMD_JS/CMD_JS_QUICK (DesktopSurface.ExecuteJavaScriptAsync and the internal RunJavaScriptAsync) needs eval.
    // The readable source is WebUiBridge.js. The assembly holds it minified, as UTF-8 data that
    // eng/generate-page-scripts.mjs writes to PageScripts.g.cs: in a NativeAOT executable that is 16 KB instead of a
    // 49 KB UTF-16 literal (runic-sdk#1109).
    internal static string Script => ScriptSource.Value;

    private static readonly Lazy<string> ScriptSource = new(static () => System.Text.Encoding.UTF8.GetString(PageScripts.WebUiBridge));
}
