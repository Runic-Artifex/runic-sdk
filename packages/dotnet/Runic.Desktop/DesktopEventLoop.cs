using Runic.Desktop.Internal;

namespace Runic.Desktop;

/// <summary>Runs asynchronous application work while the process main thread services native events.</summary>
/// <remarks>
/// <see cref="Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> picks the loop for the platform and the
/// configured window host: the GTK 4 runner when the host uses it, the macOS AppKit loop on macOS, and a plain wait
/// elsewhere.
/// </remarks>
public static class DesktopEventLoop
{
    private static int _running;
    internal static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>Starts a Desktop host and runs <paramref name="application"/> on the event loop its window host needs.</summary>
    /// <remarks>
    /// <para>
    /// Call this directly from <c>Main</c>, before any <see langword="await"/>, and return its result as the exit code.
    /// It picks the loop from the platform and <see cref="DesktopHostOptions.WindowHostFactory"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A supported <see cref="IDesktopEventLoopWindowHostFactory"/>, such as the GTK 4 factory set by
    /// <c>WithGtk4()</c>, runs its own main-thread loop.</description></item>
    /// <item><description>On macOS, the AppKit loop of <see cref="Run(Func{Task})"/>.</description></item>
    /// <item><description>Elsewhere, including Windows and Linux GTK 3, the call waits for the work.</description></item>
    /// </list>
    /// <para>
    /// An event-loop factory that is not supported, for example because its native libraries are missing, is not
    /// started: the host still runs, so <see cref="DesktopHost.Validate"/> can explain the problem and a browser
    /// fallback can open. The host is disposed before the loop ends.
    /// </para>
    /// </remarks>
    /// <param name="options">The host options, for example <c>new DesktopHostOptions().WithGtk4()</c>.</param>
    /// <param name="application">The application work. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>.</returns>
    public static int Run(DesktopHostOptions options, Func<DesktopHost, Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(application);

        async Task<int> RunHostAsync()
        {
            await using var host = await DesktopHost.StartAsync(options).ConfigureAwait(false);
            return await application(host).ConfigureAwait(false);
        }

        if (options.WindowHostFactory is IDesktopEventLoopWindowHostFactory { IsSupported: true } loop)
        {
            return loop.RunEventLoop(RunHostAsync);
        }
        int result = 0;
        Run(async () => result = await RunHostAsync().ConfigureAwait(false));
        return result;
    }

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
