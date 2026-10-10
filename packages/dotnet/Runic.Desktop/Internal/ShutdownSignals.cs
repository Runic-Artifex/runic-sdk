using System.Runtime.InteropServices;

namespace Runic.Desktop.Internal;

/// <summary>Turns Ctrl+C, SIGTERM and SIGHUP into an orderly shutdown of one Desktop host.</summary>
/// <remarks>
/// On Windows, SIGINT is Ctrl+C, SIGHUP a closed console window and SIGTERM a logoff or system shutdown.
/// </remarks>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly DesktopHost _host;
    private readonly PosixSignalRegistration[] _registrations;

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
        // A repeated signal keeps its default action, so a shutdown that hangs can still be ended.
        if (!_host.RequestShutdown(context.Signal.ToString())) return;
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
