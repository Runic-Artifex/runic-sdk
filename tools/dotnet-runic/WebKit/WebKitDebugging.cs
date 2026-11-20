using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;

namespace Runic.Application.Tool.WebKit;

/// <summary>
/// What <c>dotnet runic dev</c> opens for debugging the Window's pages on Linux (D066.38, D066.39): by default WebKit's
/// remote inspector server (<c>WEBKIT_INSPECTOR_SERVER</c>) on a loopback port, and Runic's CDP adapter on 9222 for
/// JavaScript debuggers; with <c>--webkit-inspector</c>, WebKit's own inspector over HTTP instead, where WebKitGTK refuses
/// other origins (bug 330602). WebKit never serves both: with <c>WEBKIT_INSPECTOR_HTTP_SERVER</c> it ignores
/// <c>WEBKIT_INSPECTOR_SERVER</c> and its HTTP server takes the inspector server's only client slot
/// (<c>WebKitInitialize.cpp</c>, <c>initializeRemoteInspectorServer</c>).
/// </summary>
internal static class WebKitDebugging
{
    internal const string InspectorServerVariable = "WEBKIT_INSPECTOR_SERVER";
    internal const string InspectorHttpServerVariable = "WEBKIT_INSPECTOR_HTTP_SERVER";

    /// <summary>The CDP port, WebView2's in the template, so one attach configuration serves Windows and Linux (D066.39).</summary>
    internal const int DefaultCdpPort = 9222;

    /// <summary>
    /// WebKit's inspector port under <c>--no-window</c>: the IDE starts the Window, and its launch configuration can only
    /// name a fixed address (the template's "Window with JavaScript debugging (Linux)"). A Window that dev starts gets a
    /// free port instead, so several applications under dev do not collide.
    /// </summary>
    internal const int NoWindowInspectorPort = 9223;

    internal const string Engine = "webkitgtk";
    internal const string CdpProtocol = "cdp";
    internal const string RemoteInspectorProtocol = "webkit-remote-inspector";
    internal const string HttpInspectorProtocol = "webkit-inspector";

    /// <summary>
    /// The first WebKitGTK release whose HTTP inspector refuses other origins and host names (WebKit bug 330602, merged into
    /// webkitglib/2.54 on 2026-11-10, after 2.54.2): any web page could otherwise script the Window through it.
    /// </summary>
    internal static readonly Version HttpInspectorFixedVersion = new(2, 54, 3);

    /// <summary>The first stable series that branched after the fix landed on WebKit's main (326175@main).</summary>
    internal static readonly Version HttpInspectorFixedSeries = new(2, 56, 0);

    /// <summary>
    /// Whether WebKitGTK <paramref name="version"/> carries the fix for bug 330602: 2.54.3 or later within 2.54, and 2.56.0
    /// or later. The 2.55 development releases count as unfixed, since none is confirmed to carry it (2.55.0 predates it);
    /// so do older series and an unknown version.
    /// </summary>
    internal static bool HttpInspectorIsFixed(Version? version) => version switch
    {
        null => false,
        { Major: 2, Minor: 54 } => version.Build >= HttpInspectorFixedVersion.Build,
        _ => version >= HttpInspectorFixedSeries,
    };

    private static string VersionText(Version? version) => version is null ? "(version unknown)" : version.ToString(3);

    // Under --no-window the IDE starts the Window, which may load another WebKitGTK than dev does.
    private static string CheckedVersionNote(DevOptions options) => options.StartWindow
        ? ""
        : " The check used the WebKitGTK that dev loads; a Window that the IDE starts in another environment may load another one.";

    /// <summary>The decision, before the CDP endpoint is bound.</summary>
    internal sealed record Plan(
        bool StartAdapter,
        string? InspectorHost,
        int InspectorPort,
        IReadOnlyList<KeyValuePair<string, string>> WindowEnvironment,
        IReadOnlyList<DevSession.DebugEndpoint> Endpoints,
        IReadOnlyList<string> Hints)
    {
        internal static Plan None { get; } = new(false, null, 0, [], [], []);
    }

    /// <summary>
    /// Decides what dev opens. <paramref name="reservePort"/> picks a free loopback port; <paramref name="webKitVersion"/>
    /// reads the installed WebKitGTK's version, and is called only for <c>--webkit-inspector</c> and a developer-set
    /// <c>WEBKIT_INSPECTOR_HTTP_SERVER</c>.
    /// </summary>
    internal static Plan Decide(
        DevProjectConfiguration configuration,
        DevOptions options,
        Func<string, string?> readEnvironment,
        bool linux,
        Func<int> reservePort,
        Func<Version?> webKitVersion)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        // --browser: no WebKit view; js-debug owns the debugging connection of the browser it launches.
        if (!linux || configuration.RunsOnCsWebUiOnly || !options.DebugEndpoint || options.Browser) return Plan.None;
        string? Chosen(string name) =>
            options.Environment.LastOrDefault(variable => variable.Key.Equals(name, StringComparison.Ordinal)).Value
            ?? readEnvironment(name);

        // A WebKit inspector the developer configured wins. dev cannot unset it for a Window that the IDE starts, so it is
        // used as it is, with a warning where it is unsafe.
        if (Chosen(InspectorHttpServerVariable) is { Length: > 0 } http)
        {
            var hints = new List<string>
            {
                $"[dev] Debugging: WebKit's Web Inspector at http://{http}/ ({InspectorHttpServerVariable} is set). WebKit then " +
                $"ignores {InspectorServerVariable}, so JavaScript debuggers cannot attach through Runic's CDP adapter.",
            };
            Version? version = webKitVersion();
            if (!HttpInspectorIsFixed(version))
            {
                hints.Add($"[dev] Warning: WebKitGTK {VersionText(version)} lets any web page that you open in a browser reach " +
                    $"the HTTP inspector that {InspectorHttpServerVariable} opens and script the Window and its Bridge (WebKit bug " +
                    $"330602, fixed in {HttpInspectorFixedVersion.ToString(3)} and {HttpInspectorFixedSeries.ToString(2)}). Unset " +
                    $"{InspectorHttpServerVariable} unless you need it.{CheckedVersionNote(options)}");
            }
            if (!TrySplitLoopback(http, out _, out _))
            {
                hints.Add(NotLoopbackWarning(InspectorHttpServerVariable, http));
            }
            return new Plan(false, null, 0, [], [new(Engine, HttpInspectorProtocol, $"http://{http}/")], hints);
        }
        if (Chosen(InspectorServerVariable) is { Length: > 0 } chosen)
        {
            if (!TrySplitLoopback(chosen, out string host, out int port))
            {
                return new Plan(false, null, 0, [], [new(Engine, RemoteInspectorProtocol, $"inspector://{chosen}")],
                    [$"[dev] Debugging: {InspectorServerVariable}={chosen} is not a loopback address with a port, so Runic's CDP " +
                     "adapter does not attach to it.", NotLoopbackWarning(InspectorServerVariable, chosen)]);
            }
            return new Plan(true, host, port, [], [new(Engine, RemoteInspectorProtocol, $"inspector://{chosen}")], []);
        }

        if (options.WebKitInspector)
        {
            Version? version = webKitVersion();
            if (HttpInspectorIsFixed(version))
            {
                string address = $"127.0.0.1:{reservePort().ToString(CultureInfo.InvariantCulture)}";
                return new Plan(false, null, 0, [new(InspectorHttpServerVariable, address)],
                    [new(Engine, HttpInspectorProtocol, $"http://{address}/")],
                    [$"[dev] Debugging: WebKit's Web Inspector for the Window at http://{address}/ (--webkit-inspector); open it in " +
                     "a browser. WebKit serves either it or the inspector that JavaScript debuggers attach to, so IDE attach is off."]);
            }
            int fallback = reservePort();
            string fallbackAddress = $"127.0.0.1:{fallback.ToString(CultureInfo.InvariantCulture)}";
            return new Plan(true, "127.0.0.1", fallback, [new(InspectorServerVariable, fallbackAddress)],
                [new(Engine, RemoteInspectorProtocol, $"inspector://{fallbackAddress}")],
                [$"[dev] Debugging: WebKitGTK {VersionText(version)} lets any web page reach its HTTP " +
                 $"inspector (WebKit bug 330602, fixed in {HttpInspectorFixedVersion.ToString(3)} and {HttpInspectorFixedSeries.ToString(2)}), so " +
                 $"--webkit-inspector opened inspector://{fallbackAddress} instead: open it in GNOME Web while no debugger is attached." +
                 CheckedVersionNote(options)]);
        }

        int inspectorPort = options.StartWindow ? reservePort() : NoWindowInspectorPort;
        string inspectorAddress = $"127.0.0.1:{inspectorPort.ToString(CultureInfo.InvariantCulture)}";
        return new Plan(true, "127.0.0.1", inspectorPort, [new(InspectorServerVariable, inspectorAddress)],
            [new(Engine, RemoteInspectorProtocol, $"inspector://{inspectorAddress}")], []);
    }

    private static string NotLoopbackWarning(string variable, string address) =>
        $"[dev] Warning: {variable}={address} is not a loopback address, so WebKit's inspector for the Window, which can script " +
        "it and its Bridge, is open to other machines on the network.";

    /// <summary>The session file entry and hint for the CDP endpoint once it listens.</summary>
    internal static DevSession.DebugEndpoint CdpEndpoint(int port) =>
        new(Engine, CdpProtocol, $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/");

    /// <summary>What held 9222 when the adapter had to move, for <see cref="CdpHints"/> (#1065).</summary>
    /// <param name="Owner">The holder's user when it is another user's: an attach configuration that keeps 9222 would then
    /// hand the debugger to that user's process.</param>
    /// <param name="Holder">This user's process that holds it (<see cref="PortHolder.Describe"/>), when it can be told.</param>
    /// <param name="LaunchJson">The project's VS Code <c>launch.json</c>, relative to the project when inside it.</param>
    /// <param name="FixedAttach">Its attach configurations that name 9222.</param>
    internal sealed record DefaultPortTaken(uint? Owner, string? Holder = null, string? LaunchJson = null, IReadOnlyList<string>? FixedAttach = null)
    {
        /// <summary>Looks up what holds 9222 and which of the project's attach configurations expect it.</summary>
        internal static DefaultPortTaken Find(LoopbackSockets? sockets, string projectDirectory)
        {
            (uint? owner, string? holder) = PortHolder.OnLoopback(sockets, DefaultCdpPort);
            (string? path, IReadOnlyList<string> names) = PortHolder.FixedAttachConfigurations(projectDirectory, DefaultCdpPort);
            string? shown = path;
            if (path is not null && System.IO.Path.GetRelativePath(projectDirectory, path) is var relative &&
                !relative.StartsWith("..", StringComparison.Ordinal)) shown = relative.Replace('\\', '/');
            return new(owner, holder, shown, names);
        }
    }

    /// <summary>
    /// The hints for the CDP endpoint on <paramref name="port"/>. <paramref name="taken"/> says what holds 9222 when the
    /// adapter had to move: another user's process is a warning, since an attach configuration that keeps 9222 would hand
    /// the debugger to it; so is one of this user's while the project's <c>launch.json</c> attaches to 9222, which would
    /// then debug that process's page instead of this Window's (#1065).
    /// </summary>
    internal static IReadOnlyList<string> CdpHints(int port, Plan plan, DefaultPortTaken? taken = null)
    {
        string text = port.ToString(CultureInfo.InvariantCulture);
        string defaultText = DefaultCdpPort.ToString(CultureInfo.InvariantCulture);
        string inspector = plan.Endpoints.FirstOrDefault(static endpoint => endpoint.Protocol == RemoteInspectorProtocol)?.Url is { } url
            ? $" GNOME Web opens {url} while no debugger is attached."
            : "";
        bool moved = port != DefaultCdpPort;
        bool foreign = moved && taken?.Owner is not null;
        IReadOnlyList<string> attach = moved && !foreign ? taken?.FixedAttach ?? [] : [];
        string by = taken?.Holder is { } holder ? $" by {holder}" : "";
        string movedText = !moved || foreign
            ? ""
            : attach.Count > 0
                ? $" Port {defaultText} was taken{by}."
                : $" Port {defaultText} was taken{by}: set \"port\": {text} in your attach configuration.";
        var hints = new List<string>
        {
            $"[dev] Debugging: JavaScript debuggers attach to the Window at http://127.0.0.1:{text}/ " +
            $"(WebKitGTK through Runic's CDP adapter; in VS Code an attach configuration with \"port\": {text}).{movedText}{inspector}",
        };
        if (foreign)
        {
            hints.Add($"[dev] Warning: port {defaultText} belongs to another user " +
                $"(uid {taken!.Owner!.Value.ToString(CultureInfo.InvariantCulture)}), so Runic's CDP adapter listens on {text}. Set \"port\": {text} in " +
                $"your attach configuration: one that attaches to {defaultText} would hand " +
                "your debugger to that user's process.");
        }
        else if (attach.Count > 0)
        {
            string names = string.Join(", ", attach.Select(static name => $"\"{name}\""));
            string which = attach.Count == 1 ? $"{names} in {taken!.LaunchJson} attaches" : $"{names} in {taken!.LaunchJson} attach";
            string process = taken.Holder ?? "the process that holds it";
            hints.Add($"[dev] Warning: {which} to port {defaultText}, which would reach {process}, not this Window. " +
                $"Set \"port\": {text} there while this session runs, or stop {process} and restart dev to get {defaultText} back. " +
                "obj/runic/dev-session.json lists the port under debugEndpoints.");
        }
        return hints;
    }

    /// <summary>
    /// The warning when WebKit's inspector port, which dev's Window or the IDE's would open, already belongs to another
    /// user, or its owner cannot be read: the adapter does not connect to it.
    /// </summary>
    internal static string ForeignInspectorHint(string host, int port, uint? owner)
    {
        string address = $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
        return owner is { } uid
            ? $"[dev] Warning: WebKit's inspector port {address} belongs to another user (uid {uid.ToString(CultureInfo.InvariantCulture)}), " +
              $"so the Window cannot open it and Runic's CDP adapter does not connect to it. Choose another port with " +
              $"-e {InspectorServerVariable}=127.0.0.1:<port>, and the same in the IDE's launch configuration when it starts the Window."
            : $"[dev] Warning: the owner of WebKit's inspector port {address} could not be read from /proc/net/tcp, so Runic's CDP " +
              "adapter does not connect to it.";
    }

    internal const string NoWindowCdpPortTakenCode = "RAPPDEV1017";

    /// <summary>
    /// Under <c>--no-window</c> the IDE's attach configuration names the CDP port, 9222 in the template's, so dev does not
    /// move the adapter to another port when 9222 is taken (#1065): the attach would fail with "Cannot connect", or reach
    /// whatever holds 9222. <paramref name="owner"/> is that process's user when it is another user's;
    /// <paramref name="holder"/> names this user's process that holds it, when it can be told.
    /// </summary>
    internal static string NoWindowCdpPortTakenMessage(uint? owner, string? holder = null)
    {
        string port = DefaultCdpPort.ToString(CultureInfo.InvariantCulture);
        string held = owner is { } uid
            ? $"another user (uid {uid.ToString(CultureInfo.InvariantCulture)}) holds it"
            : holder is not null
                ? $"{holder} holds it"
                : "another process holds it, such as another dotnet runic dev session or a browser with remote debugging";
        string stop = owner is null && holder is not null ? $"Stop {holder}" : "Stop that process";
        return $"--no-window needs port {port} for Runic's CDP adapter, which the IDE's attach configuration names, but " +
            $"{held}. {stop}, or run dotnet runic dev without --no-window, which moves the adapter to a " +
            "free port and names it, or with --no-debug-endpoint.";
    }

    internal const string CdpUnavailableHint =
        "[dev] Debugging: no loopback port for Runic's CDP adapter could be opened, so JavaScript debuggers cannot attach.";

    /// <summary>A loopback IP literal and a port, as WEBKIT_INSPECTOR_SERVER takes them.</summary>
    internal static bool TrySplitLoopback(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        int colon = address.LastIndexOf(':');
        if (colon <= 0) return false;
        string name = address[..colon].Trim('[', ']');
        if (!IPAddress.TryParse(name, out IPAddress? ip) || !IPAddress.IsLoopback(ip)) return false;
        if (!int.TryParse(address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535) return false;
        host = name;
        return true;
    }

    /// <summary>
    /// The installed WebKitGTK's version, from <c>webkit_get_*_version</c> of the library the application loads: the lower
    /// of WebKitGTK 6.0's and 4.1's when both are installed, since dev does not know which one the Window uses.
    /// </summary>
    internal static Version? ReadInstalledVersion()
    {
        Version? gtk4 = Desktop.Internal.Gtk4NativeLibraries.WebKit.Select(ReadVersion).FirstOrDefault(static version => version is not null);
        Version? gtk3 = ReadVersion(Desktop.Internal.Gtk3NativeLibraries.WebKit);
        return gtk4 is null ? gtk3 : gtk3 is null || gtk4 < gtk3 ? gtk4 : gtk3;
    }

    private static unsafe Version? ReadVersion(string library)
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(library, out IntPtr handle)) return null;
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "webkit_get_major_version", out IntPtr major) ||
                !NativeLibrary.TryGetExport(handle, "webkit_get_minor_version", out IntPtr minor) ||
                !NativeLibrary.TryGetExport(handle, "webkit_get_micro_version", out IntPtr micro)) return null;
            return new Version(
                (int)((delegate* unmanaged<uint>)major)(),
                (int)((delegate* unmanaged<uint>)minor)(),
                (int)((delegate* unmanaged<uint>)micro)());
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
