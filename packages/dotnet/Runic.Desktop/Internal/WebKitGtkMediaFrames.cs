namespace Runic.Desktop.Internal;

// The MediaCapture grant on WebKitGTK (SEC-37). WebKit's permission request does not name the requesting frame, and
// WebKit lets a frame of another origin ask when the page delegates camera or microphone to it (an iframe's
// allow="camera; microphone"); a frame without that delegation is refused before the request reaches the host. An
// isolated-world script in every frame reports the subframes, so the window grants the request only while every
// subframe of the page has the presented origin. The page cannot reach the handler, so it can neither hide a frame
// nor report one. Linked into Runic.Desktop.Gtk4; one instance per window, used on the GTK thread.
internal sealed class WebKitGtkMediaFrames
{
    internal const string MessageHandler = "runicMediaFrames";

    // "0" when a top-level document starts: its earlier subframes are gone. A subframe reports "+<id> <origin>" when its
    // document starts and when it returns from the back-forward cache, and "-<id>" when it is hidden or unloaded.
    // A frame with an opaque origin reports "null".
    internal const string Script = """
        (() => {
          "use strict";
          const handler = globalThis.webkit?.messageHandlers?.runicMediaFrames;
          if (!handler) return;
          const post = handler.postMessage.bind(handler);
          if (globalThis.top === globalThis) { post("0"); return; }
          const id = Array.from(globalThis.crypto.getRandomValues(new Uint32Array(4)), value => value.toString(16)).join("");
          const report = () => post("+" + id + " " + globalThis.location.origin);
          report();
          globalThis.addEventListener("pageshow", event => { if (event.persisted) report(); }, true);
          globalThis.addEventListener("pagehide", () => post("-" + id), true);
        })();
        """;

    private readonly Uri _presented;
    private readonly HashSet<string> _foreign = new(StringComparer.Ordinal);

    internal WebKitGtkMediaFrames(Uri presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        _presented = presented;
    }

    // Whether a subframe of another origin than the presented one is in the page.
    internal bool HasForeignFrames => _foreign.Count != 0;

    internal void Receive(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        switch (message[0])
        {
            case '0' when message.Length == 1:
                _foreign.Clear();
                break;
            case '+':
                var space = message.IndexOf(' ', StringComparison.Ordinal);
                if (space < 2) return;
                var id = message[1..space];
                if (IsPresentedOrigin(message[(space + 1)..]))
                {
                    _foreign.Remove(id);
                }
                else
                {
                    _foreign.Add(id);
                }
                break;
            case '-' when message.Length > 1:
                _foreign.Remove(message[1..]);
                break;
        }
    }

    // A new web process starts without the old one's frames.
    internal void Reset() => _foreign.Clear();

    internal bool IsPresentedOrigin(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var candidate) &&
        (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps) &&
        Uri.Compare(_presented, candidate, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
}
