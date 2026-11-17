using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Runic.Application.Tool;

/// <summary>
/// The Chrome DevTools Protocol endpoint of a CS-WebUI Window's installed Chromium or Edge (D070.7), for an IDE's
/// JavaScript debugger. Runic.Desktop opens it only in Development and only when
/// <c>RUNIC_WEBUI_REMOTE_DEBUGGING_PORT</c> asks for it (<c>WebUiRemoteDebugging</c> there). dev sets the variable for a
/// project with the CS-WebUI host unless <c>--no-debug-endpoint</c> or <c>--browser</c> is given, or the developer passes
/// their own with <c>-e</c>:
/// <list type="bullet">
/// <item>A Window that dev starts gets <c>0</c>: each browser opens a free loopback port and the Window prints it
/// (<see cref="OpenedPrefix"/>), and its end (<see cref="ClosedPrefix"/>), so the session file lists it while it runs.</item>
/// <item>With <c>--no-window</c> the IDE starts the Window and dev cannot read its output, so the session file's
/// environment names <see cref="NoWindowPort"/>, which the session file lists.</item>
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

    internal static bool Applies(DevProjectConfiguration configuration, DevOptions options) =>
        options.DebugEndpoint && !options.Browser && configuration.ApplicationHosts.Contains("cswebui");

    private static string? DeveloperPort(DevOptions options) =>
        options.Environment.LastOrDefault(variable => variable.Key.Equals(PortVariable, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The variable for the Window, unless the developer passed their own with <c>-e</c>, which dev forwards as it is.</summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> WindowEnvironment(DevProjectConfiguration configuration, DevOptions options) =>
        Applies(configuration, options) && DeveloperPort(options) is null
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
        "(127.0.0.1 only, Development only; --no-debug-endpoint turns it off). Attach a JavaScript debugger to it " +
        "(docs/debugging.md, \"CS-WebUI pages in a JavaScript debugger\").";

    /// <summary>The <c>[dev]</c> line for <c>--no-window</c>, whose Window the IDE starts with the session file's environment.</summary>
    internal static string NoWindowHint(DevSession.DebugEndpoint endpoint) =>
        $"[dev] Debugging: a CS-WebUI Window started with the session file's environment opens the Chrome DevTools Protocol on {endpoint.Url} " +
        $"({PortVariable}).";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex Token();

    [GeneratedRegex(@"^http://127\.0\.0\.1:(?<port>[0-9]{1,5})/$")]
    private static partial Regex Url();
}
