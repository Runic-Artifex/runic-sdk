using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Runs asynchronous application work while the macOS process main thread services native events.</summary>
public static class DesktopEventLoop
{
    private static int _running;
    internal static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Call from the synchronous process entry point. The loop stays alive through
    /// application shutdown and asynchronous native release. Other platforms simply join the task.</summary>
    public static void Run(Func<Task> application)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!OperatingSystem.IsMacOS()) { application().GetAwaiter().GetResult(); return; }
        if (!MacOsWkWebViewHost.IsMainThread) throw new InvalidOperationException("The Desktop event loop must start on the macOS process main thread.");
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) throw new InvalidOperationException("The Desktop event loop is already running.");
        try
        {
            var work = Task.Run(application);
            while (!work.IsCompleted)
            {
                MacOsWkWebViewHost.ProcessApplicationEvents();
                Thread.Sleep(5);
            }
            MacOsWkWebViewHost.ProcessApplicationEvents();
            work.GetAwaiter().GetResult();
        }
        finally { Volatile.Write(ref _running, 0); }
    }
}
