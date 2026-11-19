using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool.WebKit;

namespace Runic.Application.Tool;

/// <summary>
/// The stable loopback address of <c>dotnet runic dev --browser</c>, <c>http://127.0.0.1:9224/</c> (#1187, D077.12,
/// D077.14): a proxy to the pages of the serve-only Window process that runs now, so a browser tab keeps its address
/// when <c>dotnet watch</c> restarts the Window. <c>/</c> is the Window's first page, <c>/windows/&lt;n&gt;</c> the n-th
/// Window the process opened; until a Window serves, a page that reloads itself every second.
/// <para>
/// Admission: the first request carries a one-time code (<c>?code=…</c>), which dev prints in the
/// <c>[dev] Browser:</c> line and writes into a page only this user can read (<see cref="CodeFile"/>), which an IDE's
/// launch configuration opens; the session file names that page, not the code. That request gets a new cookie of dev's
/// (<c>HttpOnly</c>, <c>SameSite=Strict</c>), and the code is replaced and the file rewritten. The cookie becomes the
/// admitted browser's when that browser's next navigation, which the admission's page makes, presents it; then the
/// previous browser's cookie ends and its open WebSocket connections close. Until then the previous admission stays.
/// Every other request needs the admitted browser's cookie. On Linux the address also answers only the user who runs dev
/// (<see cref="CheckPeer"/>).
/// </para>
/// <para>
/// Forwarding: a request of the bound browser goes to the Window's own loopback server with its <c>Host</c> rewritten
/// and its <c>Origin</c> kept, which the Window admits (<c>RUNIC_APPLICATION_SERVE_ORIGIN</c>); a redirect to the
/// Window's address comes back as one to this address. A Runic Desktop page's Bridge WebSocket comes here too. After a
/// restart, its reconnection to the old Window's surface path gets the Bridge's token refusal from this address (the new
/// Window would refuse the old session credential before any token check, and the page would keep reconnecting), so the
/// page reloads itself, and its reload goes to the new Window. A CS-WebUI page's WebUI connection goes to the Window's
/// port directly.
/// </para>
/// </summary>
internal sealed class BrowserEntry : IAsyncDisposable
{
    /// <summary>The port of the template's "Window in Chrome" configuration; after WebKit's 9222 (CDP) and 9223 (--no-window).</summary>
    internal const int DefaultPort = 9224;

    /// <summary>The serve-only protocol's lines (Runic.Application.Views.Desktop README, "The serve-only protocol").</summary>
    internal const string UrlPrefix = "RUNIC_APPLICATION_URL=";
    internal const string WindowPrefix = "RUNIC_APPLICATION_WINDOW=";

    /// <summary>The variable that makes RunicDesktopHost and RunicCsWebUiHost serve their Windows without presenting them.</summary>
    internal const string ServeOnlyVariable = "RUNIC_APPLICATION_SERVE_ONLY";

    /// <summary>The variable that names this address to the serve-only Window, which admits it as an origin of its pages.</summary>
    internal const string ServeOriginVariable = "RUNIC_APPLICATION_SERVE_ORIGIN";

    /// <summary>The query parameter of the one-time code, and dev's browser cookie.</summary>
    internal const string CodeParameter = "code";
    internal const string CookieName = "runic_dev_browser";

    /// <summary>The page in the session directory that opens the address with the current code.</summary>
    internal const string CodeFileName = "dev-browser.html";

    private const int MaximumHead = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly List<Served> _windows = [];
    // The surface paths of earlier Window processes, by Window index: a page of an earlier process reconnects there.
    private readonly Dictionary<string, int> _staleBases = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly LoopbackSockets? _sockets;
    private readonly Action<string> _report;
    private readonly HashSet<uint?> _reportedPeers = [];
    private Task _accepting = Task.CompletedTask;
    private string _code = NewSecret();
    private string? _cookie;
    // The cookie of the last admission request, which becomes _cookie when its browser's next navigation presents it.
    private string? _pendingCookie;
    // The WebSocket connections the proxy relays now, which a new admission closes.
    private readonly HashSet<Action> _upgrades = [];
    // How long after its close command a Window's address waits for a successor, as when dotnet watch restarts it.
    internal static readonly TimeSpan ClosingGrace = TimeSpan.FromSeconds(5);

    private BrowserEntry(TcpListener listener, LoopbackSockets? sockets, Action<string> report, string? codeFile)
    {
        _listener = listener;
        _sockets = sockets;
        _report = report;
        CodeFile = codeFile;
    }

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The address of the Window's first page, without the code.</summary>
    internal string Url => RootUrl(Port);

    /// <summary>The page that opens the address with the current code, readable only by this user; null when none is written.</summary>
    internal string? CodeFile { get; }

    /// <summary>The current one-time code.</summary>
    internal string Code
    {
        get { lock (_gate) return _code; }
    }

    /// <summary>Raised after an admission replaced the code (and rewrote <see cref="CodeFile"/>), for dev's line.</summary>
    internal event Action? CodeChanged;

    internal static string RootUrl(int port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>The entry address of the Window opened <paramref name="index"/>-th (0-based) since the Window process started.</summary>
    internal static string WindowUrl(int port, int index) =>
        index == 0 ? RootUrl(port) : $"{RootUrl(port)}windows/{(index + 1).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The entry address with a one-time code, which admits a browser.</summary>
    internal static string CodeUrl(int port, int index, string code) => $"{WindowUrl(port, index)}?{CodeParameter}={code}";

    /// <summary>
    /// Listens on 127.0.0.1:<paramref name="port"/> (0 for any free port); null when the port is taken.
    /// <paramref name="sockets"/> tells who owns a connection's other end (null: no check, off Linux); <paramref name="report"/>
    /// receives the <c>[dev]</c> line for each user whose request it refused; <paramref name="codeFile"/> is the page
    /// that opens the address with the current code (<see cref="CodeFileName"/> in the session directory), or null.
    /// </summary>
    internal static BrowserEntry? TryStart(int port, LoopbackSockets? sockets = null, Action<string>? report = null, string? codeFile = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            listener.Dispose();
            return null;
        }
        var entry = new BrowserEntry(listener, sockets, report ?? Console.Error.WriteLine, codeFile);
        entry.WriteCodeFile();
        entry._accepting = Task.Run(entry.AcceptAsync);
        return entry;
    }

    /// <summary>A Window the running Window process serves: its entry address (without the code) and, once its line arrived, its type.</summary>
    internal sealed record ServedWindow(int Index, string EntryUrl, string? Type);

    // Closed: the Window sent its page the Bridge's close command (RelayBridgeAsync); its address waits for a successor.
    private sealed record Served(Uri Target, string Base, string? Type, DateTimeOffset? ClosedAt = null)
    {
        internal bool Closed => ClosedAt is not null;
    }

    /// <summary>The Windows the current Window process serves, in the order it opened them.</summary>
    internal IReadOnlyList<ServedWindow> Windows
    {
        get
        {
            lock (_gate)
            {
                var windows = new List<ServedWindow>(_windows.Count);
                for (int index = 0; index < _windows.Count; index++) windows.Add(new(index, WindowUrl(Port, index), _windows[index].Type));
                return windows;
            }
        }
    }

    /// <summary>Whether a line of the Window's output belongs to the serve-only protocol; dev does not print them, since they carry the credential.</summary>
    internal static bool IsServeOnlyLine(string line) =>
        line.StartsWith(UrlPrefix, StringComparison.Ordinal) || line.StartsWith(WindowPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads one line of the Window's output. A <c>RUNIC_APPLICATION_URL=</c> line adds a Window and returns it; a
    /// <c>RUNIC_APPLICATION_WINDOW=</c> line names the type of the Window with that URL and returns it too. Other lines
    /// return null.
    /// </summary>
    internal ServedWindow? Observe(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (line.StartsWith(UrlPrefix, StringComparison.Ordinal))
            {
                if (LoopbackHttp(line[UrlPrefix.Length..].Trim()) is not { } target) return null;
                _windows.Add(new(target, BasePath(target), null));
                return new(_windows.Count - 1, WindowUrl(Port, _windows.Count - 1), null);
            }
            if (line.StartsWith(WindowPrefix, StringComparison.Ordinal))
            {
                // "<url> <type>", and later releases may add fields after them.
                string[] fields = line[WindowPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || LoopbackHttp(fields[0]) is not { } target) return null;
                int index = _windows.FindLastIndex(window => window.Target == target);
                if (index < 0) return null;
                _windows[index] = _windows[index] with { Type = fields[1] };
                return new(index, WindowUrl(Port, index), fields[1]);
            }
            return null;
        }
    }

    /// <summary>
    /// Forgets the Windows of a Window process that exited; the entry answers with the waiting page until the next one
    /// serves. A page of the old process reloads (its Bridge socket is turned away), and its reload goes to the entry of
    /// the Window at the same place.
    /// </summary>
    internal void Clear()
    {
        lock (_gate)
        {
            for (int index = 0; index < _windows.Count; index++)
                if (_windows[index].Base != "/") _staleBases[_windows[index].Base] = index;
            _windows.Clear();
        }
    }

    /// <summary>
    /// A Window whose Bridge socket at <paramref name="socketPath"/> received the Window's close command: it serves that
    /// page no more (the Window closed, or <c>dotnet watch</c> is stopping its process). Its paths count as an earlier
    /// Window's from now on, so the page's reload goes to the entry. For a few seconds that waits (<see cref="ClosingGrace"/>),
    /// as for a restart; then <c>/</c> opens the first Window that serves, and <c>/windows/&lt;n&gt;</c> says the Window closed.
    /// </summary>
    internal void BridgeClosed(string socketPath)
    {
        string closedBase = socketPath[..^BridgeSocketPath.Length];
        lock (_gate)
        {
            int index = _windows.FindIndex(window => window.Base == closedBase && !window.Closed);
            if (index < 0 || closedBase == "/") return;
            _windows[index] = _windows[index] with { ClosedAt = DateTimeOffset.UtcNow };
            _staleBases[closedBase] = index;
        }
    }

    /// <summary>Only the serve-only Window's own loopback page is a target; anything else is ignored.</summary>
    private static Uri? LoopbackHttp(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback ? uri : null;

    // The directory of the Window's page, ending in a slash: /surface-<id>/ on Runic Desktop, / on CS-WebUI.
    internal static string BasePath(Uri target)
    {
        string path = target.AbsolutePath;
        return path[..(path.LastIndexOf('/') + 1)];
    }

    /// <summary>What the entry does with a request (<see cref="Decide"/>).</summary>
    internal enum Outcome
    {
        Refuse,
        Admit,
        Redirect,
        Wait,
        Forward,
        TurnAway,
        Closed,
    }

    /// <summary>
    /// A decision: a status and body for <see cref="Outcome.Refuse"/>; the place to go for <see cref="Outcome.Admit"/> and
    /// <see cref="Outcome.Redirect"/> (a path on this address); the Window's authority and the path to request there for
    /// <see cref="Outcome.Forward"/>. <see cref="Outcome.TurnAway"/> answers an earlier Window's Bridge socket itself
    /// (<see cref="TurnAwayFrame"/>). <see cref="Outcome.Closed"/> answers with <see cref="ClosedPage"/>. Confirms: the
    /// request presents the pending admission's cookie on a navigation to an entry, which makes that browser the admitted one.
    /// </summary>
    internal sealed record Decision(Outcome Outcome, int Status = 200, string Body = "", string? Location = null, string? Authority = null,
        string? Path = null, bool Confirms = false);

    /// <summary>The request as the decision sees it.</summary>
    internal sealed record Request(string Method, string Target, string? Host, string? Origin, string? FetchSite, string? Cookie, bool Upgrade);

    /// <summary>
    /// The Windows of the running process (null for one that closed its page; <paramref name="Closing"/> those that closed
    /// within <see cref="ClosingGrace"/>), the surface paths of earlier ones, and the cookies of the admitted browser and
    /// of the pending admission, as the decision sees them.
    /// </summary>
    internal sealed record State(string Code, string? BrowserCookie, IReadOnlyList<Uri?> Windows, IReadOnlyDictionary<string, int> StaleBases,
        string? PendingCookie = null, IReadOnlySet<int>? Closing = null);

    internal const string WaitingPage =
        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"1\">" +
        "<title>Waiting for the Window</title><p>dotnet runic dev is starting the Window, or the application closed it. This page " +
        "opens it as soon as it serves.</p></html>";

    internal const string ClosedPage =
        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"2\">" +
        "<title>The Window closed</title><p>The application closed this Window. This page opens the Window that serves at this " +
        "address next; the first Window is at <a href=\"/\">/</a>.</p></html>";

    internal const string CodeRequiredBody =
        "Forbidden: open the address that dotnet runic dev prints in its [dev] Browser: line, with its code, or the page it writes as obj/runic/" +
        CodeFileName + ", as your IDE's launch configuration does.";

    /// <summary>
    /// Decides a request (#1187, D077.12). The Host must be an IP literal or <c>localhost</c> (DNS rebinding). A request
    /// with the current code (<c>GET</c> or <c>HEAD</c> of an entry address) admits its browser, also from another site,
    /// such as the page of <see cref="CodeFile"/> (a <c>file:</c> document); every other request needs the bound browser's
    /// cookie, an <c>Origin</c> of this address if any, and a <c>Sec-Fetch-Site</c> of <c>none</c> or <c>same-origin</c>
    /// if any. An entry address goes to its Window's page, or the waiting page; a path of a running Window goes to it; a
    /// path of an earlier process's Window goes to the running one's entry: its Bridge socket is turned away, so the page
    /// reloads, and that navigation goes to the entry.
    /// </summary>
    internal static Decision Decide(Request request, State state)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(state);
        if (!CdpRequestPolicy.IsLoopbackOrLiteralHost(request.Host)) return Refused(403, "Forbidden");
        string target = request.Target;
        int question = target.IndexOf('?', StringComparison.Ordinal);
        string path = question < 0 ? target : target[..question];
        string? query = question < 0 ? null : target[(question + 1)..];
        int? entry = EntryIndex(path);

        if (QueryValue(query, CodeParameter) is { } code)
        {
            if (request.Method is not ("GET" or "HEAD")) return Refused(405, "Method not allowed");
            if (entry is null || request.Upgrade) return Refused(404, "Not found");
            if (FixedTimeEquals(code, state.Code)) return new(Outcome.Admit, Location: path);
            // A used code (an old [dev] Browser: line) in the bound browser: its entry, without the code.
            return HasCookie(request, state) ? new(Outcome.Redirect, Location: path) : Refused(403, CodeRequiredBody);
        }

        // The admitted browser's cookie; or the pending admission's, on the navigation that the admission's page makes.
        string presented = CookieValue(request.Cookie, CookieName) ?? "";
        bool confirms = !HasCookie(request, state) && entry is not null && request.Method is "GET" or "HEAD" && !request.Upgrade &&
            state.PendingCookie is not null && FixedTimeEquals(presented, state.PendingCookie);
        if (!HasCookie(request, state) && !confirms) return Refused(403, CodeRequiredBody);
        if (!string.IsNullOrEmpty(request.Origin) && !IsOwnOrigin(request.Origin, request.Host!)) return Refused(403, "Forbidden");
        if (request.FetchSite is not null and not ("none" or "same-origin")) return Refused(403, "Forbidden");
        Decision decided = Route(request, state, target, path, query, entry);
        return confirms ? decided with { Confirms = true } : decided;

        static bool HasCookie(Request request, State state) =>
            state.BrowserCookie is not null && FixedTimeEquals(CookieValue(request.Cookie, CookieName) ?? "", state.BrowserCookie);
        static Decision Refused(int status, string body) => new(Outcome.Refuse, status, body);
    }

    // Where a request of the admitted browser goes (Decide).
    private static Decision Route(Request request, State state, string target, string path, string? query, int? entry)
    {
        if (entry is { } entryIndex)
        {
            bool getOrHead = request.Method is "GET" or "HEAD";
            if (!getOrHead) return Refused(405, "Method not allowed");
            if (entryIndex < state.Windows.Count && state.Windows[entryIndex] is null)
            {
                // A Window that sent its page the close command: wait a moment, as for a restart. Then / follows the first
                // Window that still serves, and /windows/<n> says it closed.
                if (state.Closing?.Contains(entryIndex) == true) return new(Outcome.Wait);
                if (entryIndex > 0) return new(Outcome.Closed);
                int live = -1;
                for (int index = 0; index < state.Windows.Count && live < 0; index++)
                    if (state.Windows[index] is not null) live = index;
                if (live < 0) return new(Outcome.Wait);
                entryIndex = live;
            }
            if (entryIndex >= state.Windows.Count || state.Windows[entryIndex] is not { } window) return new(Outcome.Wait);
            if (BasePath(window) == "/")
            {
                // A CS-WebUI Window serves at its server's root, which is also the entry's. The proxy has one root, the
                // first such Window's; a later one (a second CS-WebUI Window) opens at its own address, as before #1187.
                if (Enumerable.Range(0, state.Windows.Count).First(index => state.Windows[index] is { } other && BasePath(other) == "/") != entryIndex)
                    return new(Outcome.Redirect, Location: window.AbsoluteUri);
                // The root with a query, or the page itself, is the Window's.
                if (path == "/" && (query is not null || window.PathAndQuery == "/")) return Forward(window, target);
            }
            return new(Outcome.Redirect, Location: window.PathAndQuery);
        }

        // The longest base of a running Window that the path is in (a CS-WebUI Window's is /).
        int best = -1;
        for (int index = 0; index < state.Windows.Count; index++)
        {
            if (state.Windows[index] is not { } candidate) continue;
            string windowBase = BasePath(candidate);
            if (path.StartsWith(windowBase, StringComparison.Ordinal) && (best < 0 || windowBase.Length > BasePath(state.Windows[best]!).Length))
                best = index;
        }
        if (best >= 0) return Forward(state.Windows[best]!, target);

        foreach ((string staleBase, int index) in state.StaleBases)
        {
            if (!path.StartsWith(staleBase, StringComparison.Ordinal)) continue;
            // The page of a Window that has exited reconnects its Bridge here. The new Window would refuse its session
            // credential at the handshake (401), after which the page keeps reconnecting; the entry answers instead with
            // the Bridge's token refusal, after which the page reloads itself.
            if (request.Upgrade)
                return path == staleBase + BridgeSocketPath ? new(Outcome.TurnAway) : Refused(404, "Not found");
            return request.Method is "GET" or "HEAD"
                ? new(Outcome.Redirect, Location: index == 0 ? "/" : $"/windows/{(index + 1).ToString(CultureInfo.InvariantCulture)}")
                : Refused(404, "Not found");
        }
        return Refused(404, "Not found");

        static Decision Forward(Uri window, string path) => new(Outcome.Forward, Authority: window.Authority, Path: path);
        static Decision Refused(int status, string body) => new(Outcome.Refuse, status, body);
    }

    // The Window index of an entry address: / is 0, /windows/<n> (n >= 1) is n - 1; null for any other path.
    private static int? EntryIndex(string path)
    {
        if (path == "/") return 0;
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["windows", var number] && !path.EndsWith('/') &&
            int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int position) && position >= 1
                ? position - 1
                : null;
    }

    /// <summary>The Runic Desktop Bridge's WebSocket path below a surface's base (Runic.Desktop WebUiWindow.WebSocketPath).</summary>
    internal const string BridgeSocketPath = "_webui_ws_connect";

    /// <summary>
    /// The Bridge's answer to a page whose Window has gone: one unmasked binary WebSocket frame with a token check reply
    /// (signature 221, command 245 <c>CMD_CHECK_TK</c>) that refuses the token. The page's client then stops reconnecting
    /// and reloads itself (Runic.Desktop Internal/WebUiBridge.js, <c>receivePacket</c>).
    /// </summary>
    internal static readonly byte[] TurnAwayFrame = [0x82, 9, 221, 0, 0, 0, 0, 0, 0, 245, 0];

    /// <summary>
    /// The Bridge packet a Window sends its page when it closes, also when <c>dotnet watch</c> stops it: command 250, an
    /// 8-byte header and a terminating byte (Runic.Desktop Internal/WebUiProtocol.cs).
    /// </summary>
    private static bool IsBridgeClose(ReadOnlySpan<byte> payload) => payload.Length == 9 && payload[0] == 221 && payload[7] == 250;

    /// <summary>
    /// Relays the Window's WebSocket frames to the page, but for the Bridge's close command: the page would close and stop
    /// reconnecting, also when <c>dotnet watch</c> only restarts the Window. It gets the token refusal instead
    /// (<see cref="TurnAwayFrame"/>), reloads itself, and the reload goes to the Window that serves then. The Window
    /// sends unmasked, uncompressed frames (the proxy offers no extension), each a whole Bridge packet.
    /// </summary>
    internal static async Task RelayBridgeAsync(Stream from, Stream to, CancellationToken cancellationToken, Action? closed = null)
    {
        byte[] head = new byte[14];
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            if (!await ReadFullyAsync(from, head.AsMemory(0, 2), cancellationToken).ConfigureAwait(false)) return;
            int headLength = 2;
            long length = head[1] & 0x7F;
            if (length == 126)
            {
                if (!await ReadFullyAsync(from, head.AsMemory(2, 2), cancellationToken).ConfigureAwait(false)) return;
                length = (head[2] << 8) | head[3];
                headLength = 4;
            }
            else if (length == 127)
            {
                if (!await ReadFullyAsync(from, head.AsMemory(2, 8), cancellationToken).ConfigureAwait(false)) return;
                length = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(2, 8));
                headLength = 10;
            }
            if ((head[1] & 0x80) != 0)
            {
                if (!await ReadFullyAsync(from, head.AsMemory(headLength, 4), cancellationToken).ConfigureAwait(false)) return;
                headLength += 4;
            }
            // A whole binary frame of a Bridge packet's size, unmasked: the close command is replaced.
            if (head[0] == 0x82 && headLength == 2 && length == 9)
            {
                if (!await ReadFullyAsync(from, buffer.AsMemory(0, 9), cancellationToken).ConfigureAwait(false)) return;
                if (IsBridgeClose(buffer.AsSpan(0, 9)))
                {
                    closed?.Invoke();
                    await to.WriteAsync(TurnAwayFrame, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                await to.WriteAsync(head.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
                await to.WriteAsync(buffer.AsMemory(0, 9), cancellationToken).ConfigureAwait(false);
                continue;
            }
            await to.WriteAsync(head.AsMemory(0, headLength), cancellationToken).ConfigureAwait(false);
            for (long remaining = length; remaining > 0;)
            {
                int read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
    }

    private static async Task<bool> ReadFullyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    // Completes the handshake of a page's Bridge socket and sends it the token refusal, then closes. A browser fails a
    // handshake that selects none of the subprotocols the page offered (its runic-desktop.<credential>), so the answer
    // selects the first one.
    private static async Task TurnAwayAsync(Stream stream, string key, string? protocols)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(TurnAwayHead(key, protocols))).ConfigureAwait(false);
        await stream.WriteAsync(TurnAwayFrame).ConfigureAwait(false);
        // A close frame (1000, normal closure).
        await stream.WriteAsync(new byte[] { 0x88, 2, 0x03, 0xE8 }).ConfigureAwait(false);
    }

    internal static string TurnAwayHead(string key, string? protocols)
    {
        string? protocol = protocols?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {CdpServer.WebSocketAccept(key)}\r\n" +
            (protocol is null ? "" : $"Sec-WebSocket-Protocol: {protocol}\r\n") + "\r\n";
    }

    private static string? QueryValue(string? query, string name)
    {
        if (string.IsNullOrEmpty(query)) return null;
        foreach (string pair in query.Split('&'))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && pair[..equals] == name) return Uri.UnescapeDataString(pair[(equals + 1)..]);
        }
        return null;
    }

    internal static string? CookieValue(string? header, string name)
    {
        if (string.IsNullOrEmpty(header)) return null;
        foreach (string part in header.Split(';'))
        {
            string pair = part.Trim();
            if (pair.StartsWith(name + "=", StringComparison.Ordinal)) return pair[(name.Length + 1)..];
        }
        return null;
    }

    // The Cookie header without dev's own cookie, which stays out of the Window's requests; null when nothing is left.
    internal static string? WithoutOwnCookie(string header)
    {
        string[] kept = [.. header.Split(';').Select(static part => part.Trim())
            .Where(static part => part.Length > 0 && !part.StartsWith(CookieName + "=", StringComparison.Ordinal))];
        return kept.Length == 0 ? null : string.Join("; ", kept);
    }

    // An Origin of this address: http with the request's own Host.
    private static bool IsOwnOrigin(string origin, string host) =>
        string.Equals(origin, "http://" + host, StringComparison.OrdinalIgnoreCase);

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // An admission request: a new code, and a pending cookie for its browser (Confirm). The admitted browser stays admitted
    // until a browser presents that cookie, so a page that only sends the code somewhere (a file: page that frames or opens
    // the code file) cannot end its admission.
    private string Admit()
    {
        string cookie = NewSecret();
        lock (_gate)
        {
            _pendingCookie = cookie;
            _code = NewSecret();
        }
        WriteCodeFile();
        try
        {
            CodeChanged?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
        return cookie;
    }

    // The pending admission's browser presented its cookie: it is the admitted browser now. The previous one's cookie ends,
    // and so do the WebSocket connections the proxy relays for it, which would otherwise keep driving the Window.
    private void Confirm(string? presented)
    {
        Action[] upgrades;
        lock (_gate)
        {
            if (_pendingCookie is null || presented is null || !FixedTimeEquals(presented, _pendingCookie)) return;
            _cookie = _pendingCookie;
            _pendingCookie = null;
            upgrades = [.. _upgrades];
            _upgrades.Clear();
        }
        foreach (Action close in upgrades) close();
    }

    // Registers a WebSocket connection that the proxy relays, which Confirm closes; disposing the result removes it.
    private Untrack TrackUpgrade(Action close)
    {
        lock (_gate) _upgrades.Add(close);
        return new Untrack(this, close);
    }

    private sealed class Untrack(BrowserEntry entry, Action close) : IDisposable
    {
        public void Dispose()
        {
            lock (entry._gate) entry._upgrades.Remove(close);
        }
    }

    /// <summary>The number of WebSocket connections the proxy relays now (tests).</summary>
    internal int RelayedUpgrades
    {
        get { lock (_gate) return _upgrades.Count; }
    }

    /// <summary>The page that opens the address with <paramref name="code"/>: a refresh to it, and the address to type.</summary>
    internal static string CodePage(int port, string code)
    {
        string url = WebUtility.HtmlEncode(CodeUrl(port, 0, code));
        return "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">" +
            $"<meta http-equiv=\"refresh\" content=\"0;url={url}\"><title>dotnet runic dev --browser</title>" +
            $"<p>Opening the Window served by dotnet runic dev: <a href=\"{url}\">{url}</a></p></html>\n";
    }

    // Writes the code page atomically, readable only by this user: 0600 on Unix, an ACL of this user alone (nothing
    // inherited) on Windows.
    private void WriteCodeFile()
    {
        if (CodeFile is not { } path) return;
        string content;
        lock (_gate) content = CodePage(Port, _code);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            WritePrivateFile(temporary, Encoding.UTF8.GetBytes(content));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _report($"[dev] Warning: could not write {path}: {error.Message}");
        }
    }

    internal static void WritePrivateFile(string path, byte[] content)
    {
        if (File.Exists(path)) File.Delete(path);
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using FileStream stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096,
                FileOptions.None, security);
            stream.Write(content);
            return;
        }
        using (FileStream stream = new(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write(content);
        }
    }

    /// <summary>What <see cref="CheckPeer"/> decided for a connection.</summary>
    internal readonly record struct PeerCheck(bool Allowed, bool Report, uint? Owner);

    /// <summary>
    /// Whether the other end of a connection belongs to the user who runs dev, as #1046's CDP adapter decides it. A peer that
    /// is not listed while the entry's listener is (it closed already, such as a port probe) is refused without a line;
    /// tables that cannot be read are refused with one.
    /// </summary>
    internal static PeerCheck CheckPeer(LoopbackSockets? sockets, IPEndPoint local, IPEndPoint remote)
    {
        if (sockets is null) return new(true, false, null);
        uint? owner = sockets.PeerOwner(local, remote);
        if (owner == sockets.User) return new(true, false, owner);
        return new(false, owner is not null || sockets.ListenerOwners(local.Address, local.Port).Count == 0, owner);
    }

    internal static string RefusedPeerMessage(int port, uint? owner) => owner is { } uid
        ? $"[dev] Warning: refused a request to {RootUrl(port)} from another user (uid {uid.ToString(CultureInfo.InvariantCulture)}); " +
          "--browser's address answers only the user who runs dotnet runic dev."
        : $"[dev] Warning: refused a request to {RootUrl(port)} whose user could not be determined from /proc/net/tcp; " +
          "--browser's address answers only the user who runs dotnet runic dev.";

    internal const string RefusedPeerBody = "Forbidden: this address answers only the user who runs dotnet runic dev.";

    private bool IsOwnUsers(TcpClient client)
    {
        if (_sockets is null) return true;
        if (client.Client.LocalEndPoint is not IPEndPoint local || client.Client.RemoteEndPoint is not IPEndPoint remote) return false;
        PeerCheck check = CheckPeer(_sockets, local, remote);
        if (check.Allowed) return true;
        bool first;
        lock (_reportedPeers) first = check.Report && _reportedPeers.Add(check.Owner);
        if (first) _report(RefusedPeerMessage(Port, check.Owner));
        return false;
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_connections)
                {
                    _connections.RemoveAll(static task => task.IsCompleted);
                    _connections.Add(Task.Run(() => ServeAsync(client)));
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                HttpRequestHead? head;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(RequestTimeout);
                    head = await HttpRequestHead.ReadAsync(stream, MaximumHead, timeout.Token).ConfigureAwait(false);
                }
                if (head is null) return;
                // Another local user would otherwise reach the Window, which hands its pages the surface's credential.
                if (!IsOwnUsers(client))
                {
                    await WriteAsync(stream, 403, RefusedPeerBody, head.Method == "HEAD").ConfigureAwait(false);
                    return;
                }
                State state;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                lock (_gate)
                {
                    state = new(_code, _cookie, [.. _windows.Select(static window => window.Closed ? null : window.Target)],
                        new Dictionary<string, int>(_staleBases), _pendingCookie,
                        Enumerable.Range(0, _windows.Count).Where(index => now - _windows[index].ClosedAt < ClosingGrace).ToHashSet());
                }
                var request = new Request(head.Method, head.Path, head.Header("Host"), head.Header("Origin"), head.Header("Sec-Fetch-Site"),
                    head.Header("Cookie"), head.IsWebSocketUpgrade(out string webSocketKey));
                Decision decision = Decide(request, state);
                if (decision.Confirms) Confirm(CookieValue(request.Cookie, CookieName));
                switch (decision.Outcome)
                {
                    case Outcome.Admit:
                        string cookie = Admit();
                        await WriteAsync(stream, 200, AdmittedPage(decision.Location!), head.Method == "HEAD",
                            $"Set-Cookie: {CookieName}={cookie}; Path=/; HttpOnly; SameSite=Strict\r\n").ConfigureAwait(false);
                        return;
                    case Outcome.Redirect:
                        await WriteAsync(stream, 302, "", head.Method == "HEAD", $"Location: {decision.Location}\r\n").ConfigureAwait(false);
                        return;
                    case Outcome.Wait:
                        await WriteAsync(stream, 200, WaitingPage, head.Method == "HEAD").ConfigureAwait(false);
                        return;
                    case Outcome.Closed:
                        await WriteAsync(stream, 200, ClosedPage, head.Method == "HEAD").ConfigureAwait(false);
                        return;
                    case Outcome.TurnAway:
                        await TurnAwayAsync(stream, webSocketKey, head.Header("Sec-WebSocket-Protocol")).ConfigureAwait(false);
                        return;
                    case Outcome.Forward:
                        await ForwardAsync(stream, head, request.Upgrade, decision.Authority!, decision.Path!).ConfigureAwait(false);
                        return;
                    default:
                        await WriteAsync(stream, decision.Status, decision.Body, head.Method == "HEAD").ConfigureAwait(false);
                        return;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    // The answer to an admission: a same-origin navigation to the entry, which then carries the SameSite=Strict cookie
    // (a redirect that a file: page started would count as cross-site and leave it out).
    internal static string AdmittedPage(string location)
    {
        string encoded = WebUtility.HtmlEncode(location);
        return $"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url={encoded}\">" +
            $"<title>Opening the Window</title><p>Opening the Window: <a href=\"{encoded}\">{encoded}</a></p></html>";
    }

    /// <summary>
    /// Sends the request to the Window at <paramref name="authority"/> and its answer back: the request's header lines with
    /// <c>Host</c> for the Window, dev's cookie left out and, but for a WebSocket upgrade, one request per connection; the
    /// answer's <c>Location</c> on the Window's own address moves to this one.
    /// </summary>
    private async Task ForwardAsync(NetworkStream client, HttpRequestHead head, bool upgrade, string authority, string path)
    {
        string? host = head.Header("Host");
        if (head.Header("Transfer-Encoding") is not null)
        {
            await WriteAsync(client, 411, "Length required", head.Method == "HEAD").ConfigureAwait(false);
            return;
        }
        using var window = new TcpClient();
        int colon = authority.LastIndexOf(':');
        string windowHost = authority[..colon].Trim('[', ']');
        int windowPort = int.Parse(authority[(colon + 1)..], CultureInfo.InvariantCulture);
        try
        {
            await window.ConnectAsync(windowHost, windowPort, _stop.Token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // The Window process has exited and its successor has not served yet: a navigation, such as the page's own
            // reload, gets the page that reloads itself until the entry sends it to the new Window.
            if (head.Header("Sec-Fetch-Mode") == "navigate" && head.Method is "GET" or "HEAD")
                await WriteAsync(client, 200, WaitingPage, head.Method == "HEAD").ConfigureAwait(false);
            else
                await WriteAsync(client, 503, "The Window is restarting.", head.Method == "HEAD").ConfigureAwait(false);
            return;
        }
        NetworkStream server = window.GetStream();
        await server.WriteAsync(Encoding.Latin1.GetBytes(ForwardedHead(head, upgrade, authority, path)), _stop.Token).ConfigureAwait(false);

        using var done = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        // A new admission closes the WebSocket connections of the browser admitted before (Confirm).
        using Untrack? tracked = upgrade ? TrackUpgrade(() =>
        {
            try
            {
                done.Cancel();
                window.Close();
                client.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }) : null;
        // A WebSocket carries frames both ways until either side closes; a request carries its body (Content-Length; a
        // browser does not send chunked request bodies to a page's server), and nothing after it reaches the Window.
        Task upload = upgrade ? client.CopyToAsync(server, done.Token) : CopyBodyAsync(client, server, head, done.Token);
        byte[]? answer = await ReadHeadAsync(server, done.Token).ConfigureAwait(false);
        if (answer is null) return;
        string response = ForwardedResponseHead(Encoding.Latin1.GetString(answer), upgrade, authority, host ?? $"127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}");
        await client.WriteAsync(Encoding.Latin1.GetBytes(response), done.Token).ConfigureAwait(false);
        // A Runic Desktop page's Bridge: the Window's close command becomes the token refusal (RelayBridgeAsync).
        Task download = upgrade && path.EndsWith("/" + BridgeSocketPath, StringComparison.Ordinal)
            ? RelayBridgeAsync(server, client, done.Token, () => BridgeClosed(path))
            : server.CopyToAsync(client, done.Token);
        await (upgrade ? Task.WhenAny(upload, download) : download).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(upload, download).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    // The request head for the Window: hop-by-hop lines and dev's cookie dropped, Host the Window's, Origin kept.
    internal static string ForwardedHead(HttpRequestHead head, bool upgrade, string authority, string path)
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{head.Method} {path} HTTP/1.1\r\n");
        text.Append(CultureInfo.InvariantCulture, $"Host: {authority}\r\n");
        foreach ((string name, string value) in head.Lines)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                // Uncompressed frames, which RelayBridgeAsync reads.
                name.Equals("Sec-WebSocket-Extensions", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                if (WithoutOwnCookie(value) is { } kept) text.Append(CultureInfo.InvariantCulture, $"Cookie: {kept}\r\n");
                continue;
            }
            text.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }
        text.Append(upgrade ? "Connection: Upgrade\r\nUpgrade: websocket\r\n" : "Connection: close\r\n");
        return text.Append("\r\n").ToString();
    }

    // The Window's answer head for the browser: a Location on the Window's address moves to this one, and but for a
    // WebSocket upgrade the connection closes after it.
    internal static string ForwardedResponseHead(string head, bool upgrade, string authority, string entryHost)
    {
        string[] lines = head.Split("\r\n");
        var text = new StringBuilder();
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            string name = colon > 0 ? line[..colon].Trim() : "";
            if (!upgrade && (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (name.Equals("Location", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out Uri? location) && location.Scheme == Uri.UriSchemeHttp &&
                (string.Equals(location.Authority, authority, StringComparison.OrdinalIgnoreCase) ||
                 (location.IsLoopback && location.Port == new Uri("http://" + authority).Port)))
            {
                text.Append(CultureInfo.InvariantCulture, $"Location: http://{entryHost}{location.PathAndQuery}{location.Fragment}\r\n");
                continue;
            }
            text.Append(line).Append("\r\n");
        }
        if (!upgrade) text.Append("Connection: close\r\n");
        return text.Append("\r\n").ToString();
    }

    private static async Task CopyBodyAsync(Stream from, Stream to, HttpRequestHead head, CancellationToken cancellationToken)
    {
        if (!long.TryParse(head.Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out long remaining)) return;
        byte[] buffer = new byte[16 * 1024];
        while (remaining > 0)
        {
            int read = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<byte[]?> ReadHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0) return null;
            bytes.Add(one[0]);
            if (bytes.Count > MaximumHead) return null;
            int count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' && bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                return [.. bytes];
        }
    }

    private static async Task WriteAsync(Stream stream, int status, string body, bool headOnly, string extraHeaders = "")
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        string reason = status switch
        {
            200 => "OK", 302 => "Found", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 411 => "Length Required", 503 => "Service Unavailable",
            _ => "Error",
        };
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {reason}\r\n")
            .Append(status == 200 ? "Content-Type: text/html; charset=UTF-8\r\n" : "Content-Type: text/plain; charset=UTF-8\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {content.Length}\r\n")
            // Never cached or passed on as a referrer: the entry hands out the browser's admission.
            .Append("Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n")
            .Append("Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n")
            .Append(extraHeaders)
            .Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
        if (!headOnly) await stream.WriteAsync(content).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        Task[] connections;
        lock (_connections) connections = [.. _connections];
        try
        {
            await Task.WhenAll([_accepting, .. connections]).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
        _listener.Dispose();
        _stop.Dispose();
        // The code admits no browser once dev has stopped: the page that carries it goes too.
        if (CodeFile is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
