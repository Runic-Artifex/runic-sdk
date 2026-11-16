using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool.WebKit;

namespace Runic.Application.Tool;

/// <summary>
/// The fixed loopback address of <c>dotnet runic dev --browser</c>, <c>http://127.0.0.1:9224/</c>: it sends a browser to
/// the page that the serve-only Window serves (<c>RUNIC_APPLICATION_URL=</c>). That page's address carries the surface's
/// credential and changes with every start of the Window, so an IDE's launch configuration names this address instead, and
/// js-debug's Chrome or Edge follows the redirect under its debugger. <c>/</c> is the Window's first page,
/// <c>/windows/&lt;n&gt;</c> the n-th Window it opened since it started; until that Window serves, a page that reloads
/// itself every second. Only a navigation the browser itself starts gets the redirect: a typed address, a command line or
/// a reload, not a link or script of a web page (<see cref="Respond"/>), which could otherwise take the surface's one
/// client slot.
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

    private const int MaximumRequestHead = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly List<Served> _windows = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private Task _accepting = Task.CompletedTask;

    private BrowserEntry(TcpListener listener) => _listener = listener;

    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The address of the Window's first page.</summary>
    internal string Url => RootUrl(Port);

    internal static string RootUrl(int port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>The entry address of the Window opened <paramref name="index"/>-th (0-based) since the Window process started.</summary>
    internal static string WindowUrl(int port, int index) =>
        index == 0 ? RootUrl(port) : $"{RootUrl(port)}windows/{(index + 1).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Listens on 127.0.0.1:<paramref name="port"/> (0 for any free port); null when the port is taken.</summary>
    internal static BrowserEntry? TryStart(int port)
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
        var entry = new BrowserEntry(listener);
        entry._accepting = Task.Run(entry.AcceptAsync);
        return entry;
    }

    /// <summary>A Window the running Window process serves: its entry address and, once its line arrived, its type.</summary>
    internal sealed record ServedWindow(int Index, string EntryUrl, string? Type);

    private sealed record Served(string Target, string? Type);

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
                _windows.Add(new(target, null));
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

    /// <summary>Forgets the Windows of a Window process that exited; the entry answers with the waiting page until the next one serves.</summary>
    internal void Clear()
    {
        lock (_gate) _windows.Clear();
    }

    /// <summary>
    /// Only the serve-only Window's own loopback page is a target, normalized so that it is one ASCII line for the
    /// <c>Location</c> header; anything else is ignored.
    /// </summary>
    private static string? LoopbackHttp(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback ? uri.AbsoluteUri : null;

    /// <summary>One answer of the entry.</summary>
    internal sealed record Response(int Status, string? Location, string Body);

    internal const string WaitingPage =
        "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"1\">" +
        "<title>Waiting for the Window</title><p>dotnet runic dev is starting the Window. This page opens it as soon as it serves.</p></html>";

    /// <summary>
    /// The answer to a request. A web page could otherwise send the browser here and take the surface's one client slot,
    /// so the entry refuses (403) a <c>Host</c> that is not an IP literal or <c>localhost</c> (DNS rebinding), any
    /// <c>Origin</c>, and a <c>Sec-Fetch-Site</c> other than <c>none</c> (the browser's own navigation) or
    /// <c>same-origin</c> (the waiting page's reload). A browser without <c>Sec-Fetch-Site</c> is let through, as other
    /// local programs are: they can reach the Window's pages through the CDP endpoints dev opens anyway.
    /// </summary>
    internal static Response Respond(string method, string path, string? host, string? origin, string? fetchSite, IReadOnlyList<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!CdpRequestPolicy.IsLoopbackOrLiteralHost(host) || !string.IsNullOrEmpty(origin) ||
            (fetchSite is not null && fetchSite is not ("none" or "same-origin")))
        {
            return new(403, null, "Forbidden: open this address from the browser's address bar or your IDE's launch configuration.");
        }
        if (method is not ("GET" or "HEAD")) return new(405, null, "Method not allowed");
        string[] segments = path.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        int index;
        if (segments.Length == 0) index = 0;
        else if (segments is ["windows", var number] &&
            int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int position) && position >= 1)
        {
            index = position - 1;
        }
        else return new(404, null, "Not found");
        return index < targets.Count ? new(302, targets[index], "") : new(200, null, WaitingPage);
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
                HttpRequestHead? request;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    timeout.CancelAfter(RequestTimeout);
                    request = await HttpRequestHead.ReadAsync(stream, MaximumRequestHead, timeout.Token).ConfigureAwait(false);
                }
                if (request is null) return;
                string[] targets;
                lock (_gate) targets = [.. _windows.ConvertAll(static window => window.Target)];
                Response response = Respond(request.Method, request.Path, request.Header("Host"), request.Header("Origin"),
                    request.Header("Sec-Fetch-Site"), targets);
                await WriteAsync(stream, response, request.Method == "HEAD").ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task WriteAsync(Stream stream, Response response, bool headOnly)
    {
        byte[] content = Encoding.UTF8.GetBytes(response.Body);
        string reason = response.Status switch
        {
            200 => "OK", 302 => "Found", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error",
        };
        var head = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {reason}\r\n")
            .Append(response.Status == 200 ? "Content-Type: text/html; charset=UTF-8\r\n" : "Content-Type: text/plain; charset=UTF-8\r\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {content.Length}\r\n")
            // The redirect carries the surface's credential: never cache it or pass it on as a referrer.
            .Append("Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\n")
            .Append("Content-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\n");
        if (response.Location is not null) head.Append(CultureInfo.InvariantCulture, $"Location: {response.Location}\r\n");
        head.Append("Connection: close\r\n\r\n");
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
    }
}
