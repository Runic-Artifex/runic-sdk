using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

/// <summary>Turns Ctrl+C, SIGTERM and SIGHUP into an orderly shutdown of one Desktop host.</summary>
/// <remarks>
/// On Windows, SIGINT is Ctrl+C, SIGHUP a closed console window and SIGTERM a logoff or system shutdown.
/// </remarks>
internal sealed class ShutdownSignals : IDisposable
{
    /// <summary>
    /// Repeats sooner than this are copies of the first signal: a terminal Ctrl+C reaches every process in the
    /// group, and dotnet run, dotnet watch or dotnet runic dev forward their own.
    /// </summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(1);

    private readonly DesktopHost _host;
    private readonly PosixSignalRegistration[] _registrations;
    private long _firstSignalAt;

    private ShutdownSignals(DesktopHost host)
    {
        _host = host;
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handle),
        ];
    }

    internal static ShutdownSignals Register(DesktopHost host) => new(host);

    private void Handle(PosixSignalContext context)
    {
        // A windowless application that never read ShutdownRequested cannot learn of the request (#279): absorbing
        // the signal would leave it running until SIGKILL, so the signal keeps its default action. A repeat after an
        // absorbed first signal is still a copy of that one.
        if (Volatile.Read(ref _firstSignalAt) == 0 && !_host.CanShutDownInOrder) return;
        // Handlers for different signals run concurrently, so the first time is recorded before anything else.
        long now = Environment.TickCount64;
        long first = Interlocked.CompareExchange(ref _firstSignalAt, now, 0);
        if (!_host.RequestShutdown(context.Signal.ToString()) &&
            context.Signal == PosixSignal.SIGINT &&
            first != 0 && now - first >= (long)RepeatWindow.TotalMilliseconds)
        {
            // A second Ctrl+C keeps its default action, so a shutdown that hangs can still be ended. Other
            // repeats are absorbed: supervisors that send SIGTERM escalate with SIGKILL themselves.
            return;
        }

        context.Cancel = true;
        if (OperatingSystem.IsWindows() && context.Signal is PosixSignal.SIGHUP or PosixSignal.SIGTERM)
        {
            // Windows ends the process as soon as a close, logoff or shutdown handler returns, whatever it reports.
            // Keep this handler thread blocked; the process exits when Main returns, or when Windows' own close
            // timeout expires.
            Thread.Sleep(Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations) registration.Dispose();
    }
}
