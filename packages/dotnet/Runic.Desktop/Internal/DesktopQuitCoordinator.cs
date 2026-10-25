namespace Runic.Desktop.Internal;

internal enum DesktopQuitOutcome
{
    // The application decided to keep running.
    Refused,
    // ShutdownRequested is cancelled and the windows close; the application ends when its work returns.
    Accepted,
    // As Accepted, but no host has a surface or HandlesShutdownWithoutWindow, so nothing would end the application.
    AcceptedWithoutOrder,
    // No Desktop host is alive.
    NoHost,
}

// One quit path (D045.2): the application's decision (DesktopHostOptions.ConfirmQuitAsync), then each window's close
// guard, then the shutdown that cancels DesktopHost.ShutdownRequested and closes the remaining windows. The platform's
// requests (on macOS Quit, the Dock and the system) ask every live host; a request while one is deciding joins it.
internal static class DesktopQuitCoordinator
{
    private static readonly Lock Gate = new();
    private static Task<DesktopQuitOutcome>? s_pending;

    internal static Task<DesktopQuitOutcome> RequestAsync(DesktopQuitSource source)
    {
        var hosts = DesktopHost.LiveHosts();
        if (hosts.Length == 0) return Task.FromResult(DesktopQuitOutcome.NoHost);
        lock (Gate)
        {
            if (s_pending is { IsCompleted: false } pending) return pending;
            return s_pending = Task.Run(() => QuitAsync(source, hosts));
        }
    }

    internal static async Task<DesktopQuitOutcome> QuitAsync(DesktopQuitSource source, IReadOnlyList<DesktopHost> hosts)
    {
        foreach (var host in hosts)
        {
            if (!await host.ConfirmQuitAsync(source).ConfigureAwait(false)) return DesktopQuitOutcome.Refused;
        }
        foreach (var host in hosts)
        {
            if (!await host.RequestWindowsCloseAsync().ConfigureAwait(false)) return DesktopQuitOutcome.Refused;
        }
        // Whether the shutdown reaches the application: a surface it closes, or an application that ends on
        // ShutdownRequested alone.
        bool inOrder = hosts.Any(static host => host.CanShutDownInOrder);
        foreach (var host in hosts) host.RequestShutdown(Reason(source), quit: true);
        return inOrder ? DesktopQuitOutcome.Accepted : DesktopQuitOutcome.AcceptedWithoutOrder;
    }

    internal static string Reason(DesktopQuitSource source) => source switch
    {
        DesktopQuitSource.Menu => "Quit",
        DesktopQuitSource.Dock => "Quit from the Dock",
        DesktopQuitSource.System => "Log out, restart or shut down",
        _ => source.ToString(),
    };
}
