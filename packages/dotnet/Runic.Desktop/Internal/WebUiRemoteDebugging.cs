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
//   default profile, and the directory is where the browser reports the port);
// - the application's own BrowserArguments do not name --remote-debugging-port: then theirs applies, unchanged.
// Outside Development the variable is ignored, so a release never opens the port from the environment. Chromium binds the
// port to 127.0.0.1 only (--remote-debugging-address applies to headless shells, and Runic never passes it), and since
// Chromium 111 it refuses WebSocket clients that send an Origin, so web pages cannot attach; local processes can.
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

    // A fixed port that another listener holds already (another window of this application, a second copy of it, or
    // another program): Chromium would start without the endpoint, so ask for a free one instead.
    internal static int Available(int port)
    {
        if (port == 0) return 0;
        try
        {
            using var probe = new TcpListener(IPAddress.Loopback, port);
            probe.Start();
            return port;
        }
        catch (SocketException)
        {
            return 0;
        }
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

    // After the browser starts: wait for its record, announce the endpoint, and announce its end when the browser exits.
    // A browser that exits first reports nothing; one that never opens the port (another process took a fixed port
    // meanwhile) reports only through unavailable.
    internal static async Task ReportAsync(Task exited, string profilePath, int requestedPort, Action<string> writeLine,
        Action<int> unavailable, TimeProvider? time = null)
    {
        using var stop = new CancellationTokenSource(PortWait, time ?? TimeProvider.System);
        var read = ReadActivePortAsync(profilePath, stop.Token, time);
        if (await Task.WhenAny(read, exited).ConfigureAwait(false) != read)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return;
        }
        int port;
        try
        {
            port = await read.ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            if (!exited.IsCompleted) unavailable(requestedPort);
            return;
        }
        writeLine(OpenedLine(port));
        await exited.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        writeLine(ClosedLine(port));
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
