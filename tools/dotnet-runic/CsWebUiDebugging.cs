using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool.WebKit;

namespace Runic.Application.Tool;

/// <summary>
/// The Chrome DevTools Protocol endpoint of a CS-WebUI Window's installed Chromium or Edge (D070.7), for an IDE's
/// JavaScript debugger. Runic.Desktop opens it only in Development and only when
/// <c>RUNIC_WEBUI_REMOTE_DEBUGGING_PORT</c> asks for it (<c>WebUiRemoteDebugging</c> there). Any local process can attach
/// to it (DEV-SEC-50), so it opens only when an IDE asks (D070.17). For a project with the CS-WebUI host, without
/// <c>--no-debug-endpoint</c> or <c>--browser</c>, and unless the developer passes their own value with <c>-e</c>:
/// <list type="bullet">
/// <item>With <c>--debug-endpoint</c>, for an IDE to pass when it starts dev, a Window that dev starts gets <c>0</c>: each
/// browser opens a free loopback port and the Window prints it (<see cref="OpenedPrefix"/>), and its end
/// (<see cref="ClosedPrefix"/>), so the session file lists it while it runs. A terminal <c>dotnet runic dev</c> sets
/// nothing.</item>
/// <item>With <c>--no-window</c> the IDE starts the Window, with the session file's environment, and dev cannot read its
/// output. That environment names <see cref="NoWindowPort"/>, which the session file lists unless the port is taken
/// (<see cref="NoWindowPortCheck"/>). Only an IDE applies that environment, so this is the IDE asking too.</item>
/// </list>
/// </summary>
internal static partial class CsWebUiDebugging
{
    internal const string PortVariable = "RUNIC_WEBUI_REMOTE_DEBUGGING_PORT";

    /// <summary>
    /// The port of the template's "CS-WebUI script debugging" profile and of <c>--no-window</c>: after WebKit's 9222 (CDP),
    /// 9223 (<c>--no-window</c> inspector) and <c>--browser</c>'s 9224.
    /// </summary>
    internal const int NoWindowPort = 9225;

    internal const string Engine = "chromium";
    internal const string Protocol = "cdp";

    /// <summary>The Window's lines (Runic.Desktop <c>WebUiRemoteDebugging</c>): <c>&lt;engine&gt; &lt;protocol&gt; &lt;url&gt;</c>, and the URL.</summary>
    internal const string OpenedPrefix = "RUNIC_APPLICATION_DEBUG_ENDPOINT=";
    internal const string ClosedPrefix = "RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=";

    /// <summary>The reasons in <c>unavailableDebugEndpoints</c> for <see cref="NoWindowPort"/>.</summary>
    internal const string PortTakenReason = "port-taken";
    internal const string ForeignListenerReason = "foreign-listener";

    /// <summary>
    /// Whether dev handles the endpoint at all: a CS-WebUI project, without <c>--no-debug-endpoint</c> or <c>--browser</c>.
    /// dev then reads the Window's lines, so the developer's own <c>-e</c> port is listed too.
    /// </summary>
    internal static bool Applies(DevProjectConfiguration configuration, DevOptions options) =>
        options.DebugEndpoint && !options.Browser && configuration.ApplicationHosts.Contains("cswebui");

    /// <summary>Whether an IDE asks for the port (D070.17): <c>--debug-endpoint</c>, or <c>--no-window</c>.</summary>
    internal static bool Asked(DevOptions options) => options.CsWebUiDebugEndpoint || !options.StartWindow;

    private static string? DeveloperPort(DevOptions options) =>
        options.Environment.LastOrDefault(variable => variable.Key.Equals(PortVariable, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The variable for the Window, unless the developer passed their own with <c>-e</c>, which dev forwards as it is.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> WindowEnvironment(DevProjectConfiguration configuration, DevOptions options) =>
        Applies(configuration, options) && Asked(options) && DeveloperPort(options) is null
            ? [new(PortVariable, options.StartWindow ? "0" : NoWindowPort.ToString(CultureInfo.InvariantCulture))]
            : [];

    /// <summary>
    /// With <c>--no-window</c>, the endpoint at the fixed port, listed for as long as the session runs, as WebView2's is;
    /// a Window that dev starts reports its own instead.
    /// </summary>
    internal static IReadOnlyList<DevSession.DebugEndpoint> NoWindowEndpoints(DevProjectConfiguration configuration, DevOptions options)
    {
        if (options.StartWindow || !Applies(configuration, options)) return [];
        int port = NoWindowPort;
        if (DeveloperPort(options) is { } value &&
            !(int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535))
        {
            return [];
        }
        return [Endpoint(port)];
    }

    /// <summary>What holds a fixed port: the reason for <c>unavailableDebugEndpoints</c>, and on Linux another user's uid.</summary>
    internal readonly record struct PortHolder(string Reason, uint? Owner);

    private static readonly IPAddress[] LoopbackAddresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    /// <summary>
    /// Whether something already holds <paramref name="port"/> on 127.0.0.1 or <c>[::1]</c> (DEV-SEC-51): on Linux another
    /// user's listener (<paramref name="sockets"/>), and on every system a port that cannot be bound. A Window started
    /// then would not listen on 127.0.0.1:<paramref name="port"/>, and the IDE would attach to the other listener.
    /// </summary>
    internal static PortHolder? NoWindowPortCheck(int port, LoopbackSockets? sockets, Func<IPAddress, int, bool> canBind)
    {
        ArgumentNullException.ThrowIfNull(canBind);
        if (ForeignOwner(port, sockets) is { } owner) return new(ForeignListenerReason, owner);
        foreach (IPAddress address in LoopbackAddresses)
        {
            if (!canBind(address, port)) return new(PortTakenReason, null);
        }
        return null;
    }

    private static uint? ForeignOwner(int port, LoopbackSockets? sockets)
    {
        if (sockets is null) return null;
        foreach (IPAddress address in LoopbackAddresses)
        {
            if (sockets.ForeignListener(address, port) is { } owner) return owner;
        }
        return null;
    }

    /// <summary>Whether a listener could bind the address and port now; a system without IPv6 has no <c>[::1]</c> to take.</summary>
    internal static bool CanBind(IPAddress address, int port)
    {
        try
        {
            using var listener = new TcpListener(address, port);
            listener.Start();
            return true;
        }
        catch (SocketException error) when (LoopbackListeners.IsMissingAddress(address, error))
        {
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>How often <see cref="WatchNoWindowPortAsync"/> looks at the listeners.</summary>
    internal static readonly TimeSpan NoWindowPortWatchInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Linux, <c>--no-window</c>: while dev runs, moves the listed endpoint to <c>unavailableDebugEndpoints</c> and warns once
    /// when another user starts listening on its port, on 127.0.0.1 or <c>[::1]</c>. The Window's own listener is this
    /// user's and is not reported.
    /// </summary>
    internal static async Task WatchNoWindowPortAsync(DevSession session, DevSession.DebugEndpoint endpoint, int port,
        LoopbackSockets sockets, Action<string> writeLine, Action<Action> writeSession, TimeProvider time,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(NoWindowPortWatchInterval, time, cancellationToken).ConfigureAwait(false);
                if (ForeignOwner(port, sockets) is not { } owner) continue;
                writeSession(() => session.MarkUnavailable(endpoint.Url, ForeignListenerReason));
                writeLine(PortTakenWarning(endpoint, owner));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static DevSession.DebugEndpoint Endpoint(int port) =>
        new(Engine, Protocol, $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");

    internal static bool IsProtocolLine(string line) =>
        line.StartsWith(OpenedPrefix, StringComparison.Ordinal) || line.StartsWith(ClosedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads a line of the Window's output: an endpoint it opened, or the URL of one it closed. Only a loopback
    /// <c>http://127.0.0.1:&lt;port&gt;/</c> is taken; other lines, and later fields, are ignored.
    /// </summary>
    internal static bool TryRead(string line, out DevSession.DebugEndpoint? opened, out string? closed)
    {
        ArgumentNullException.ThrowIfNull(line);
        opened = null;
        closed = null;
        if (line.StartsWith(OpenedPrefix, StringComparison.Ordinal))
        {
            string[] fields = line[OpenedPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || !Token().IsMatch(fields[0]) || !Token().IsMatch(fields[1]) || LoopbackUrl(fields[2]) is not { } url)
                return false;
            opened = new DevSession.DebugEndpoint(fields[0], fields[1], url);
            return true;
        }
        if (line.StartsWith(ClosedPrefix, StringComparison.Ordinal))
        {
            string[] fields = line[ClosedPrefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 1 || LoopbackUrl(fields[0]) is not { } url) return false;
            closed = url;
            return true;
        }
        return false;
    }

    private static string? LoopbackUrl(string value)
    {
        Match match = Url().Match(value);
        return match.Success && int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) &&
            port is > 0 and <= 65535
            ? Endpoint(port).Url
            : null;
    }

    /// <summary>The <c>[dev]</c> line for an endpoint the Window opened.</summary>
    internal static string OpenedHint(DevSession.DebugEndpoint endpoint) =>
        $"[dev] Debugging: the CS-WebUI page's browser listens for the Chrome DevTools Protocol on {endpoint.Url} " +
        "(127.0.0.1, Development only). Any local process can attach to it; --no-debug-endpoint turns it off. Attach a " +
        "JavaScript debugger to it (docs/debugging.md, \"CS-WebUI pages in a JavaScript debugger\").";

    /// <summary>The <c>[dev]</c> line for a CS-WebUI Window that dev starts without <c>--debug-endpoint</c>.</summary>
    internal const string OffHint =
        "[dev] Debugging: the CS-WebUI page's browser opens no DevTools Protocol port. Pass --debug-endpoint to attach a " +
        "JavaScript debugger to it; any local process can attach to that port too.";

    /// <summary>
    /// The <c>[dev]</c> warning for a <see cref="NoWindowPort"/> that something holds when dev starts: a Window started
    /// now would get another port, or Chromium would listen on <c>[::1]</c>, and an IDE attaching to the listed address
    /// would reach that listener instead (DEV-SEC-51). <paramref name="owner"/> is its user on Linux, when it is another.
    /// </summary>
    internal static string PortTakenWarning(DevSession.DebugEndpoint endpoint, uint? owner) =>
        $"[dev] Debugging: warning: {endpoint.Url.TrimEnd('/')} (or its [::1] port) is held by " +
        (owner is { } uid ? $"another user (uid {uid.ToString(CultureInfo.InvariantCulture)})" : "another process") +
        ", so the session file lists it under unavailableDebugEndpoints. Do not attach a debugger to it; free the port " +
        $"and restart dev, or pass -e {PortVariable}=<port>.";

    /// <summary>The <c>[dev]</c> line for <c>--no-window</c>, whose Window the IDE starts with the session file's environment.</summary>
    internal static string NoWindowHint(DevSession.DebugEndpoint endpoint) =>
        $"[dev] Debugging: a CS-WebUI Window started with the session file's environment opens the Chrome DevTools Protocol on {endpoint.Url} " +
        $"({PortVariable}).";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^http://127\.0\.0\.1:(?<port>[0-9]{1,5})/$")]
    private static partial Regex Url();
}
