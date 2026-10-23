using Runic.Platform;

namespace Runic.Desktop.Internal;

// File drops onto an embedded window. The paths always come from the native drop; the page only adds the routing
// label of the element under the drop. Linked into Runic.Desktop.Gtk4.
internal static class FileDropSupport
{
    // The WebKit message handler the isolated-world script reports a drop's label to.
    internal const string WebKitMessageHandler = "runicFileDrop";

    // How long a native drop waits for the page's label before it is delivered without one.
    internal static readonly TimeSpan LabelWait = TimeSpan.FromMilliseconds(500);

    // The part of both scripts that reads data-runic-drop-effect: the nearest marked element under the pointer sets the
    // dropEffect Runic reports during a file drag; "none" refuses the drop there without navigating. Unmarked pages
    // set no dropEffect, so the source's own actions apply.
    private const string EffectFunctions = """
          const effects = ["copy", "move", "link", "none"];
          const marked = event => {
            const element = event.target instanceof Element ? event.target.closest("[data-runic-drop-effect]") : null;
            const value = element?.getAttribute("data-runic-drop-effect");
            return typeof value === "string" && effects.includes(value) ? value : null;
          };
          const allow = event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            const effect = marked(event);
            if (effect !== null) try { event.dataTransfer.dropEffect = effect; } catch { }
          };
          const performed = (event, effect) => {
            const value = effect ?? event.dataTransfer.dropEffect;
            return value === "move" || value === "link" ? value : "copy";
          };
          const label = event => {
            const element = event.target instanceof Element ? event.target.closest("[data-runic-drop-target]") : null;
            const value = element?.getAttribute("data-runic-drop-target");
            return typeof value === "string" ? value : null;
          };
    """;

    // For WebKitGTK and WKWebView, in an isolated script world, where the page cannot reach the handler. It lets every
    // file drag drop, so WebKit never navigates to a dropped file, and reports the drop's target as JSON:
    // {"label", "effect", "origin"}, where origin is null in the top-level document and the frame's origin in a
    // subframe, so the native side can refuse frames of another origin. A label is only ever attached to a drop the
    // native window reported, with the native paths. Capture-phase listeners on window run before the page's own
    // handlers, which still receive every event. WebKitGTK lists a file drag as text/uri-list until the drop, so a
    // dragged link is held on the page too; the native side ignores drops without file paths.
    internal const string WebKitScript = """
        (() => {
          "use strict";
          const handler = globalThis.webkit?.messageHandlers?.runicFileDrop;
          if (!handler) return;
          const post = handler.postMessage.bind(handler);
          const stringify = JSON.stringify;
          const origin = globalThis.top === globalThis ? null : String(globalThis.location?.origin ?? "null");
          const hasFiles = event => !!event.dataTransfer &&
            Array.from(event.dataTransfer.types).some(type => type === "Files" || type === "text/uri-list");
        """ + EffectFunctions + """
          globalThis.addEventListener("dragenter", allow, true);
          globalThis.addEventListener("dragover", allow, true);
          globalThis.addEventListener("drop", event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            const effect = marked(event);
            if (effect === "none") return;
            post(stringify({ label: label(event), effect: performed(event, effect), origin }));
          }, true);
        })();
        """;

    // For WebView2, whose File objects carry their paths only to the host: every document, subframes included, hands
    // the dropped files to chrome.webview.postMessageWithAdditionalObjects; the host hears the top-level document and
    // frames of the presented origin only. The function and the token are captured before any page script runs. Page
    // script in the same world can still dispatch a drop of File objects it holds, which the user handed it, so the
    // token only keeps other messages from passing as drops, and the reported effect is as untrusted as the label.
    internal static string WebView2Script(string token) => $$"""
        (() => {
          "use strict";
          const view = globalThis.chrome?.webview;
          if (typeof view?.postMessageWithAdditionalObjects !== "function") return;
          const post = view.postMessageWithAdditionalObjects.bind(view);
          const stringify = JSON.stringify;
          const includes = Array.prototype.includes;
          const hasFiles = event => !!event.dataTransfer && includes.call(event.dataTransfer.types, "Files");
        """ + EffectFunctions + $$"""
          globalThis.addEventListener("dragenter", allow, true);
          globalThis.addEventListener("dragover", allow, true);
          globalThis.addEventListener("drop", event => {
            if (!hasFiles(event)) return;
            event.preventDefault();
            const effect = marked(event);
            if (effect === "none" || event.dataTransfer.files.length === 0) return;
            post(stringify({ runicFileDrop: "{{token}}", target: label(event), effect: performed(event, effect) }), event.dataTransfer.files);
          }, true);
        })();
        """;

    // Decodes the WebKit script's message; an unreadable one is a top-level report without a label.
    internal static FileDropReport DecodeReport(string? message)
    {
        if (string.IsNullOrEmpty(message) || message.Length > 64 * 1024) return new(null, DesktopDragEffects.Copy, null);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return new(null, DesktopDragEffects.Copy, null);
            return new(ReadString(root, "label"), ParseEffect(ReadString(root, "effect")), ReadString(root, "origin"));
        }
        catch (System.Text.Json.JsonException)
        {
            return new(null, DesktopDragEffects.Copy, null);
        }
    }

    // The label of a top-level report.
    internal static string? DecodeLabel(string? message) => DecodeReport(message).Label;

    // A reported dropEffect: copy unless the page reported move or link.
    internal static DesktopDragEffects ParseEffect(string? effect) => effect switch
    {
        "move" => DesktopDragEffects.Move,
        "link" => DesktopDragEffects.Link,
        _ => DesktopDragEffects.Copy,
    };

    internal static string? ReadString(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

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

    internal void NativeDrop(IReadOnlyList<string> paths) => NativeDrop(paths, [], DesktopDragEffects.Copy);

    // The native side's paths, promised files and the effect it negotiated with the drag source. A native side that
    // knows only the effects the drop offers (GTK 4) passes them as offered: the page's reported effect, when it is one
    // of them, replaces the native effect.
    internal void NativeDrop(IReadOnlyList<string> paths, IReadOnlyList<IReadFileLease> promisedFiles, DesktopDragEffects effect,
        DesktopDragEffects offered = DesktopDragEffects.None)
    {
        if (paths.Count == 0 && promisedFiles.Count == 0) return;
        var pending = new Pending(paths, promisedFiles, effect, offered);
        lock (_gate)
        {
            if (_disposed)
            {
                pending.Discard();
                return;
            }
            _pending.Enqueue(pending);
            pending.Timer = new Timer(static state =>
            {
                var (pairing, entry) = ((FileDropPairing, Pending))state!;
                pairing.Expire(entry);
            }, (this, pending), wait, Timeout.InfiniteTimeSpan);
        }
    }

    // A label without a pending native drop is ignored: the page cannot make a drop of its own. A refused report, from
    // a frame of another origin, discards the drop it belongs to. The reported effect counts only for a drop whose
    // native side passed the effects it offers.
    internal void PageReport(string? label, bool refused = false, DesktopDragEffects reported = DesktopDragEffects.None)
    {
        Pending pending;
        lock (_gate)
        {
            if (_disposed || !_pending.TryDequeue(out pending!)) return;
            pending.Timer?.Dispose();
            if (refused) pending.Discard();
            else Deliver(pending, label, reported);
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
    private void Deliver(Pending pending, string? label, DesktopDragEffects reported = DesktopDragEffects.None)
    {
        var effect = reported is DesktopDragEffects.Copy or DesktopDragEffects.Move or DesktopDragEffects.Link
            && pending.Offered.HasFlag(reported) ? reported : pending.Effect;
        try { deliver(new DesktopFileDropEventArgs(pending.Paths, pending.PromisedFiles, label) { Effect = effect }); }
        catch (Exception error) when (error is not OutOfMemoryException) { pending.Discard(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_pending.TryDequeue(out var pending))
            {
                pending.Timer?.Dispose();
                pending.Discard();
            }
        }
    }

    private sealed class Pending(IReadOnlyList<string> paths, IReadOnlyList<IReadFileLease> promisedFiles, DesktopDragEffects effect,
        DesktopDragEffects offered)
    {
        internal IReadOnlyList<string> Paths { get; } = paths;
        internal IReadOnlyList<IReadFileLease> PromisedFiles { get; } = promisedFiles;
        internal DesktopDragEffects Effect { get; } = effect is DesktopDragEffects.Move or DesktopDragEffects.Link ? effect : DesktopDragEffects.Copy;
        internal DesktopDragEffects Offered { get; } = offered;
        internal Timer? Timer { get; set; }

        // A drop nobody receives deletes the staged copies of its promised files.
        internal void Discard()
        {
            foreach (var file in PromisedFiles)
                _ = file.DisposeAsync().AsTask().ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}

// The WebKit script's report: the page's label and effect, and the frame's origin (null in the top-level document).
internal sealed record FileDropReport(string? Label, DesktopDragEffects Effect, string? Origin);

// The WebView2 script's message: {"runicFileDrop": token, "target": label or null, "effect": dropEffect}. Anything else
// is not a drop.
internal sealed record FileDropMessage(string? Target, DesktopDragEffects Effect = DesktopDragEffects.Copy)
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
            return new FileDropMessage(FileDropSupport.ReadString(root, "target"),
                FileDropSupport.ParseEffect(FileDropSupport.ReadString(root, "effect")));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
