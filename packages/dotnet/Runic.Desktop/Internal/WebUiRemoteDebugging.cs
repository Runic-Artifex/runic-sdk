using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Runic.Desktop.Internal;

// D070.7: the Chrome DevTools Protocol endpoint of a CS-WebUI window's installed Chromium or Edge, for an IDE's JavaScript
// debugger (Visual Studio attaches to CS-WebUI pages only this way). It can script the page and its Bridge, so it opens
// only when all of these hold:
// - the environment is Development (WebContentRules.IsDevelopment, the test that turns DevTools on), and the window's
//   DesktopWebContentPolicy.DevTools is not false;
// - RUNIC_WEBUI_REMOTE_DEBUGGING_PORT asks for it: 0 for a free port that the browser picks, or a fixed port. dotnet runic
//   dev sets it, and so does the template's "CS-WebUI script debugging" launch profile;
// - the browser is Chromium-based and has its own profile directory (Chromium 136 and later refuse the switch for the
//   default profile, and the directory is where the browser reports a free port);
// - the application's own BrowserArguments do not name --remote-debugging-port: then theirs applies, unchanged.
// Outside Development the variable is ignored, so no build opens the port from the environment unless it runs in
// Development. Chromium binds the port to 127.0.0.1 (--remote-debugging-address applies to headless shells, and Runic never
// passes it), or to [::1] when 127.0.0.1's is taken, and since Chromium 111 it refuses WebSocket clients that send an
// Origin, so web pages cannot attach; local processes can (DEV-SEC-50).
// Chromium writes DevToolsActivePort only for port 0, so a fixed port is confirmed through /json/version on 127.0.0.1,
// and on Linux by the listener's owner, before the Window reports it (DEV-SEC-54, DEV-SEC-51).
internal static class WebUiRemoteDebugging
{
    internal const string PortVariable = "RUNIC_WEBUI_REMOTE_DEBUGGING_PORT";
    internal const string PortSwitch = "--remote-debugging-port";
    internal const string ActivePortFile = "DevToolsActivePort";

    // The lines that tell dotnet runic dev, which reads the Window's output, where the endpoint is and when it closed
    // (tools/dotnet-runic CsWebUiDebugging). Fields are separated by one space; later releases may add fields after them.
    internal const string OpenedPrefix = "RUNIC_APPLICATION_DEBUG_ENDPOINT=";
    internal const string ClosedPrefix = "RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=";

    internal static readonly TimeSpan PortPollInterval = TimeSpan.FromMilliseconds(25);
    internal static readonly TimeSpan PortWait = TimeSpan.FromSeconds(30);

    // The port to ask Chromium for, or null for none: Development, DevTools not turned off, and a valid variable.
    internal static int? RequestedPort(Func<string, string?> readEnvironment, bool? devTools)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);
        if (devTools == false || !WebContentRules.IsDevelopment(readEnvironment)) return null;
        var value = readEnvironment(PortVariable);
        return int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 0 and <= 65535
            ? port
            : null;
    }

    // Whether the application's arguments name the switch themselves, with or without a value.
    internal static bool NamesPort(IReadOnlyList<string> browserArguments) =>
        browserArguments.Any(static argument =>
            argument.Equals(PortSwitch, StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith(PortSwitch + "=", StringComparison.OrdinalIgnoreCase));

    internal static string Argument(int port) => $"{PortSwitch}={port.ToString(CultureInfo.InvariantCulture)}";

    internal static string EndpointUrl(int port) => $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/";

    internal static string OpenedLine(int port) => $"{OpenedPrefix}chromium cdp {EndpointUrl(port)}";

    internal static string ClosedLine(int port) => $"{ClosedPrefix}{EndpointUrl(port)}";

    // A fixed port, reserved on 127.0.0.1 and [::1] until just before the browser starts, so another process cannot take
    // either in between (DEV-SEC-51). A port that something holds already is not used: Chromium would listen on [::1] or
    // not at all, and a debugger would attach to the other listener. The window then asks for a free one and warns.
    internal sealed class Reservation : IDisposable
    {
        private readonly List<TcpListener> _listeners;

        internal Reservation(int port, bool fellBack, List<TcpListener> listeners)
        {
            Port = port;
            FellBack = fellBack;
            _listeners = listeners;
        }

        // The port to pass to Chromium: the requested one, or 0.
        internal int Port { get; }

        // Whether a fixed port was taken, so Port is 0.
        internal bool FellBack { get; }

        public void Dispose()
        {
            foreach (var listener in _listeners) listener.Dispose();
            _listeners.Clear();
        }
    }

    internal static Reservation Reserve(int port)
    {
        if (port == 0) return new(0, fellBack: false, []);
        var listeners = new List<TcpListener>();
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
                listeners.Add(listener);
            }
            catch (SocketException error) when (address.AddressFamily == AddressFamily.InterNetworkV6 &&
                error.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
            {
                // No IPv6 loopback, so nobody can listen there either.
                listener.Dispose();
            }
            catch (SocketException)
            {
                listener.Dispose();
                foreach (var held in listeners) held.Dispose();
                return new(0, fellBack: true, []);
            }
        }
        return new(port, fellBack: false, listeners);
    }

    // A record that an earlier browser left in a configured profile would name a port nobody listens on.
    internal static void DeleteStaleRecord(string profilePath)
    {
        try
        {
            File.Delete(Path.Combine(profilePath, ActivePortFile));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Chromium writes "<port>\n/devtools/browser/<id>" into the profile once the endpoint listens.
    internal static async Task<int> ReadActivePortAsync(string profilePath, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        var path = Path.Combine(profilePath, ActivePortFile);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Creation is not publication: Chromium can still hold a Windows sharing lock or be writing the port. A
                // newline completes the port record; EOF alone could be a valid-looking prefix of that number.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                using var reader = new StreamReader(file);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var newline = content.IndexOf('\n', StringComparison.Ordinal);
                if (newline >= 0 && int.TryParse(content.AsSpan(0, newline).TrimEnd('\r'),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535)
                {
                    return port;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33)
            {
                // Retry only sharing/lock violations; other I/O failures remain errors.
            }
            await Task.Delay(PortPollInterval, time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
    }

    // After the browser starts: confirm the endpoint, announce it, and announce its end when the browser exits. A browser
    // that exits first reports nothing. One whose endpoint cannot be confirmed reports only through unavailable, with the
    // reason, and no line: a debugger must not be sent to a listener that may not be the browser's.
    // - Port 0: Chromium writes the port it bound into DevToolsActivePort in the profile.
    // - A fixed port: Chromium writes no record, so /json/version must answer on 127.0.0.1, no other user may listen there
    //   (Linux), and [::1] must not answer: Chromium listens there when 127.0.0.1's port is taken.
    internal static async Task ReportAsync(Task exited, string profilePath, int requestedPort, Action<string> writeLine,
        Action<int, string> unavailable, TimeProvider? time = null, Probes? probes = null)
    {
        probes ??= Probes.System;
        using var stop = new CancellationTokenSource(PortWait, time ?? TimeProvider.System);
        var confirm = requestedPort == 0
            ? ReadActivePortAsync(profilePath, stop.Token, time)
            : ConfirmFixedAsync(requestedPort, probes, time, stop.Token);
        if (await Task.WhenAny(confirm, exited).ConfigureAwait(false) != confirm)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await ((Task)confirm).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return;
        }
        int port;
        try
        {
            port = await confirm.ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            if (!exited.IsCompleted)
            {
                var reason = requestedPort == 0
                    ? "the browser wrote no DevToolsActivePort record"
                    : await probes.Answers(IPAddress.IPv6Loopback, requestedPort, CancellationToken.None).ConfigureAwait(false)
                        ? $"the browser listens on [::1]:{requestedPort.ToString(CultureInfo.InvariantCulture)} instead, so another process holds 127.0.0.1's port"
                        : "nothing answered /json/version there";
                unavailable(requestedPort, reason);
            }
            return;
        }
        catch (ForeignListenerException foreign)
        {
            if (!exited.IsCompleted) unavailable(requestedPort, foreign.Message);
            return;
        }
        writeLine(OpenedLine(port));
        await exited.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        writeLine(ClosedLine(port));
    }

    // The checks of a fixed port, replaceable in tests.
    internal sealed record Probes(
        Func<IPAddress, int, CancellationToken, Task<bool>> Answers,
        Func<int, IReadOnlyList<uint>> ListenerOwners,
        uint CurrentUser)
    {
        internal static Probes System { get; } = new(
            AnswersVersionAsync,
            static port => [.. LoopbackPeerOwner.ListenerOwners(IPAddress.Loopback, port), .. LoopbackPeerOwner.ListenerOwners(IPAddress.IPv6Loopback, port)],
            OperatingSystem.IsLinux() ? LoopbackPeerOwner.CurrentUser : 0);
    }

    private sealed class ForeignListenerException(string message) : Exception(message);

    private static async Task<int> ConfirmFixedAsync(int port, Probes probes, TimeProvider? time, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await probes.Answers(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false))
            {
                var foreign = probes.ListenerOwners(port).Where(owner => owner != probes.CurrentUser).ToList();
                if (foreign.Count != 0)
                {
                    throw new ForeignListenerException(
                        $"another user (uid {foreign[0].ToString(CultureInfo.InvariantCulture)}) listens on its port");
                }
                if (await probes.Answers(IPAddress.IPv6Loopback, port, cancellationToken).ConfigureAwait(false))
                {
                    throw new ForeignListenerException("[::1] answers on the same port too, so the listener on 127.0.0.1 may not be the browser's");
                }
                return port;
            }
            await Task.Delay(FixedPortPollInterval, time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static readonly TimeSpan FixedPortPollInterval = TimeSpan.FromMilliseconds(100);

    private static readonly HttpClient VersionClient = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    // Whether a DevTools Protocol endpoint answers /json/version at the address.
    private static async Task<bool> AnswersVersionAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        try
        {
            using var response = await VersionClient.GetAsync(
                new Uri($"http://{host}:{port.ToString(CultureInfo.InvariantCulture)}/json/version"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Contains("\"webSocketDebuggerUrl\"", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // Warnings go to standard error, one write each.
    internal static void WriteWarning(string line)
    {
        try
        {
            Console.Error.Write(line + Environment.NewLine);
            Console.Error.Flush();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }
    }

    // The protocol lines go to standard output, which dotnet runic dev reads; one write each, so they never interleave.
    internal static void WriteLine(string line)
    {
        try
        {
            Console.Out.Write(line + Environment.NewLine);
            Console.Out.Flush();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }
    }
}
