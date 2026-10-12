using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// Picks the loopback port of a frontend development server. The port is free when picked but is only bound
/// later, by the server process, so another process can take it in between. A server that then exits because
/// the port is in use is started again on a new port instead of failing the session.
/// </summary>
internal static class LoopbackPort
{
    internal const int StartAttempts = 3;

    internal static int Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Recognizes the bind failures of the supported servers: Vite and Angular print "Port N is already in
    /// use", and a Node listener reports EADDRINUSE.
    /// </summary>
    internal static bool IsInUseMessage(string line) =>
        line.Contains("already in use", StringComparison.OrdinalIgnoreCase)
        || line.Contains("EADDRINUSE", StringComparison.Ordinal);

    /// <summary>
    /// Runs <paramref name="attempt"/> with a freshly picked port. An attempt reports a lost port race by
    /// throwing <see cref="PortInUseException"/> after it has stopped its server; any other failure
    /// propagates unchanged.
    /// </summary>
    internal static async Task<T> StartAsync<T>(
        string description,
        Func<int, Task<T>> attempt,
        Func<int>? reservePort = null)
    {
        reservePort ??= Reserve;
        for (int number = 1; ; number++)
        {
            int port = reservePort();
            try
            {
                return await attempt(port).ConfigureAwait(false);
            }
            catch (PortInUseException) when (number < StartAttempts)
            {
                Console.Error.WriteLine(
                    $"[dev] Port {port} was taken before the {description} could bind it; " +
                    $"retrying on another port (attempt {number + 1} of {StartAttempts}).");
            }
            catch (PortInUseException)
            {
                throw new DevDevelopmentException(
                    "RAPPDEV1007",
                    $"The {description} could not bind a loopback port after {StartAttempts} attempts because " +
                    "each chosen port was taken by another process first.");
            }
        }
    }

    /// <summary>
    /// Whether a request to a development server failed because the connection did. Vite and the Angular CLI
    /// check a port before they bind it by listening on it briefly and closing again, which resets a connection
    /// accepted meanwhile. HttpClient reports most connection failures as <see cref="HttpRequestException"/>,
    /// but a connection reset right after it was accepted can surface as an unwrapped
    /// <see cref="SocketException"/> ("Transport endpoint is not connected") while the handler reads the
    /// remote address.
    /// </summary>
    internal static bool IsConnectionFailure(Exception error) =>
        error is HttpRequestException or SocketException;

    internal sealed class PortInUseException : Exception;
}

/// <summary>Records whether a development server reported that its port is in use.</summary>
internal sealed class PortConflictWatch
{
    private int _seen;

    internal bool Seen => Volatile.Read(ref _seen) != 0;

    /// <summary>Whether <paramref name="process"/> has exited because its port was in use.</summary>
    internal async Task<bool> ExplainsExitAsync(RunningProcess process)
    {
        if (!process.Completion.IsCompleted)
        {
            return false;
        }

        await process.DrainOutputAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        return Seen;
    }

    internal void Observe(string line)
    {
        if (LoopbackPort.IsInUseMessage(line))
        {
            Volatile.Write(ref _seen, 1);
        }
    }
}
