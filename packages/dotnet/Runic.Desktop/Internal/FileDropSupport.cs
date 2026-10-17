namespace Runic.Desktop.Internal;

// File drops onto an embedded window. The paths always come from the native drop; the page only adds the routing
// label of the element under the drop. Linked into Runic.Desktop.Gtk4.
internal static class FileDropSupport
{
    // The WebKit message handler the isolated-world script reports a drop's label to.
    internal const string WebKitMessageHandler = "runicFileDrop";

    // How long a native drop waits for the page's label before it is delivered without one.
    internal static readonly TimeSpan LabelWait = TimeSpan.FromMilliseconds(500);

    // For WebKitGTK and WKWebView, in an isolated script world, where the page cannot reach the handler. It lets every
    // file drag drop, so WebKit never navigates to a dropped file, and reports the label of the drop's target: "1" and
    // the label, or "0". A label is only ever attached to a drop the native window reported, with the native paths.
    // Capture-phase listeners on window run before the page's own handlers, which still receive every event. It sets
    // no dropEffect, so the source's own actions apply. WebKitGTK lists a file drag as text/uri-list until the drop,
    // so a dragged link is held on the page too; the native side ignores drops without file paths.
    internal const string WebKitScript = """
        (() => {
          "use strict";
          const handler = globalThis.webkit?.messageHandlers?.runicFileDrop;
          if (!handler || globalThis.top !== globalThis) return;
          const post = handler.postMessage.bind(handler);
          const hasFiles = event => !!event.dataTransfer &&
            Array.from(event.dataTransfer.types).some(type => type === "Files" || type === "text/uri-list");
          const allow = event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
          };
          globalThis.addEventListener("dragenter", allow, true);
          globalThis.addEventListener("dragover", allow, true);
          globalThis.addEventListener("drop", event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            const element = event.target instanceof Element ? event.target.closest("[data-runic-drop-target]") : null;
            const label = element?.getAttribute("data-runic-drop-target");
            post(typeof label === "string" ? "1" + label : "0");
          }, true);
        })();
        """;

    // For WebView2, whose File objects carry their paths only to the host: the top-level document hands the dropped
    // files to chrome.webview.postMessageWithAdditionalObjects. The function and the token are captured before any
    // page script runs. Page script in the same world can still dispatch a drop of File objects it holds, which the
    // user handed it, so the token only keeps other messages from passing as drops.
    internal static string WebView2Script(string token) => $$"""
        (() => {
          "use strict";
          const view = globalThis.chrome?.webview;
          if (globalThis.top !== globalThis || typeof view?.postMessageWithAdditionalObjects !== "function") return;
          const post = view.postMessageWithAdditionalObjects.bind(view);
          const stringify = JSON.stringify;
          const includes = Array.prototype.includes;
          const hasFiles = event => !!event.dataTransfer && includes.call(event.dataTransfer.types, "Files");
          globalThis.addEventListener("dragover", event => { if (hasFiles(event)) event.preventDefault(); }, true);
          globalThis.addEventListener("drop", event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            const element = event.target instanceof Element ? event.target.closest("[data-runic-drop-target]") : null;
            const label = element?.getAttribute("data-runic-drop-target");
            if (event.dataTransfer.files.length === 0) return;
            post(stringify({ runicFileDrop: "{{token}}", target: typeof label === "string" ? label : null }), event.dataTransfer.files);
          }, true);
        })();
        """;

    // Decodes the WebKit script's message.
    internal static string? DecodeLabel(string? message) =>
        message is { Length: > 0 } && message[0] == '1' ? message[1..] : null;

    // The local paths of a text/uri-list: file URIs only, comments and other schemes skipped.
    internal static List<string> ParseUriList(string? uriList)
    {
        List<string> paths = [];
        if (string.IsNullOrEmpty(uriList)) return paths;
        foreach (var line in uriList.Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0 || value[0] == '#') continue;
            if (ToLocalPath(value) is { } path && !paths.Contains(path, StringComparer.Ordinal)) paths.Add(path);
        }
        return paths;
    }

    // A file URI on this machine as an absolute path; null for any other URI.
    internal static string? ToLocalPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        // file://host/path names another machine, except localhost, which is this one.
        if (string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            && !Uri.TryCreate("file://" + parsed.AbsolutePath, UriKind.Absolute, out parsed))
            return null;
        if (parsed.Host.Length != 0) return null;
        var path = parsed.LocalPath;
        return path.Length > 0 && Path.IsPathFullyQualified(path) ? path : null;
    }
}

// Pairs a native drop with the label the page reports for it right after. The native drop always comes first on
// WebKit: the UI process sees it before the web process dispatches the page's drop event. A drop whose label does not
// arrive within the wait, for example because the page's script did not run, is delivered without one.
internal sealed class FileDropPairing(Action<DesktopFileDropEventArgs> deliver, TimeSpan wait) : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Pending> _pending = new();
    private bool _disposed;

    internal void NativeDrop(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var pending = new Pending(paths);
        lock (_gate)
        {
            if (_disposed) return;
            _pending.Enqueue(pending);
            pending.Timer = new Timer(static state =>
            {
                var (pairing, entry) = ((FileDropPairing, Pending))state!;
                pairing.Expire(entry);
            }, (this, pending), wait, Timeout.InfiniteTimeSpan);
        }
    }

    // A label without a pending native drop is ignored: the page cannot make a drop of its own.
    internal void PageReport(string? label)
    {
        Pending pending;
        lock (_gate)
        {
            if (_disposed || !_pending.TryDequeue(out pending!)) return;
            pending.Timer?.Dispose();
            Deliver(pending, label);
        }
    }

    private void Expire(Pending entry)
    {
        lock (_gate)
        {
            if (_disposed || !_pending.Contains(entry)) return;
            // Earlier drops go first, also without a label, so drops keep their order.
            while (_pending.TryDequeue(out var pending))
            {
                pending.Timer?.Dispose();
                Deliver(pending, null);
                if (ReferenceEquals(pending, entry)) break;
            }
        }
    }

    // Under the gate, so drops are delivered in order; the receiver only queues them.
    private void Deliver(Pending pending, string? label)
    {
        try { deliver(new DesktopFileDropEventArgs(pending.Paths, label)); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_pending.TryDequeue(out var pending)) pending.Timer?.Dispose();
        }
    }

    private sealed class Pending(IReadOnlyList<string> paths)
    {
        internal IReadOnlyList<string> Paths { get; } = paths;
        internal Timer? Timer { get; set; }
    }
}

// The WebView2 script's message: {"runicFileDrop": token, "target": label or null}. Anything else is not a drop.
internal sealed record FileDropMessage(string? Target)
{
    internal static FileDropMessage? Parse(string? message, string token)
    {
        if (string.IsNullOrEmpty(message) || message.Length > 64 * 1024) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                || !root.TryGetProperty("runicFileDrop", out var marker)
                || marker.ValueKind != System.Text.Json.JsonValueKind.String
                || !string.Equals(marker.GetString(), token, StringComparison.Ordinal))
                return null;
            var target = root.TryGetProperty("target", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
            return new FileDropMessage(target);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
