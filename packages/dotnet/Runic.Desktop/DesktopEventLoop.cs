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
    // One Desktop event loop per process, on every platform.
    private static int _running;
    // Whether the macOS AppKit loop is pumping, so native work can be marshalled to the main thread.
    private static int _appKitPumping;
    internal static bool IsRunning => Volatile.Read(ref _appKitPumping) != 0;

    /// <summary>Starts a Desktop host and runs <paramref name="application"/> on the event loop its window host needs.</summary>
    /// <remarks>
    /// <para>
    /// Call this directly from <c>Main</c>, before any <see langword="await"/>, and return its result as the exit code.
    /// The options are validated and the host is built before a loop starts, so an options error never uses up a
    /// native toolkit's single per-process lifetime. The loop is picked from the platform and
    /// <see cref="DesktopHostOptions.WindowHostFactory"/>:
    /// </para>
    /// <list type="bullet">
    /// <item><description>A supported <see cref="IDesktopEventLoopWindowHostFactory"/>, such as the GTK 4 factory set by
    /// <c>WithGtk4()</c>, runs its own main-thread loop.</description></item>
    /// <item><description>On macOS, the AppKit loop of <see cref="Run(Func{Task})"/>.</description></item>
    /// <item><description>Elsewhere, including Windows and Linux GTK 3, the call waits for the work.</description></item>
    /// </list>
    /// <para>
    /// An event-loop factory that is not supported, or whose <see cref="IDesktopEventLoopWindowHostFactory.PrepareEventLoop"/>
    /// reports that it cannot run (for example because no display is available), is not started. The reasons are
    /// reported as warnings to <see cref="DesktopHostOptions.DiagnosticSink"/> and the Desktop logger, and the host runs on the
    /// plain loop so a browser fallback can open. The host is disposed before the loop ends.
    /// </para>
    /// </remarks>
    /// <param name="options">The host options, for example <c>new DesktopHostOptions().WithGtk4()</c>.</param>
    /// <param name="application">The application work. Its result becomes the return value.</param>
    /// <returns>The value returned by <paramref name="application"/>.</returns>
    /// <exception cref="InvalidOperationException">A Desktop event loop is already running in this process.</exception>
    public static int Run(DesktopHostOptions options, Func<DesktopHost, Task<int>> application)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(application);
        const string EntryPoint = "DesktopEventLoop.Run(options, application)";
        Enter(EntryPoint);
        DesktopHost host;
        try
        {
            // Validates the options before any native loop starts.
            host = DesktopHost.StartAsync(options).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            Exit();
            throw;
        }

        var started = 0;
        async Task<int> RunHostAsync()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                throw new InvalidOperationException("The event loop started the application more than once.");
            await using (host.ConfigureAwait(false))
            {
                return await application(host).ConfigureAwait(false);
            }
        }

        try
        {
            if (options.WindowHostFactory is IDesktopEventLoopWindowHostFactory loop)
            {
                var unavailable = loop.IsSupported ? loop.PrepareEventLoop() : loop.GetAvailabilityDiagnostics();
                if (unavailable.Count == 0 && loop.IsSupported)
                {
                    return loop.RunEventLoop(RunHostAsync);
                }
                ReportFallback(host, loop, unavailable);
            }
            int result = 0;
            RunCore(async () => result = await RunHostAsync().ConfigureAwait(false), EntryPoint);
            return result;
        }
        finally
        {
            try
            {
                // The loop failed before the application started: release the host it would have disposed.
                if (Volatile.Read(ref started) == 0) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                Exit();
            }
        }
    }

    /// <summary>Call from the synchronous process entry point. The loop stays alive through
    /// application shutdown and asynchronous native release. Other platforms simply join the task.</summary>
    /// <exception cref="InvalidOperationException">A Desktop event loop is already running in this process.</exception>
    public static void Run(Func<Task> application)
    {
        ArgumentNullException.ThrowIfNull(application);
        const string EntryPoint = "DesktopEventLoop.Run(application)";
        Enter(EntryPoint);
        try { RunCore(application, EntryPoint); }
        finally { Exit(); }
    }

    private static void Enter(string entryPoint)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException($"{entryPoint} was called while a Desktop event loop is already running; run one loop per process.");
    }

    private static void Exit() => Volatile.Write(ref _running, 0);

    private static void RunCore(Func<Task> application, string entryPoint)
    {
        if (!OperatingSystem.IsMacOS()) { application().GetAwaiter().GetResult(); return; }
        if (!MacOsWkWebViewHost.IsMainThread)
            throw new InvalidOperationException($"Call {entryPoint} directly from the macOS process main thread, before awaiting application work.");
        Volatile.Write(ref _appKitPumping, 1);
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
        finally { Volatile.Write(ref _appKitPumping, 0); }
    }

    private static void ReportFallback(
        DesktopHost host, IDesktopEventLoopWindowHostFactory loop, IReadOnlyList<DesktopDiagnostic> reasons)
    {
        var codes = reasons.Count == 0 ? "no reason reported" : string.Join(", ", reasons.Select(static reason => reason.Code));
        host.ReportConfiguration(new DesktopDiagnostic(
            DesktopErrorCategory.Unavailable,
            "event-loop-unavailable",
            $"{loop.GetType().Name} cannot run its event loop ({codes}); the plain loop runs instead, so embedded windows from it are unavailable.",
            Retryable: false,
            Remediation: "Fix the reported problems, or keep an installed-browser fallback for this configuration.")
        {
            Severity = DesktopDiagnosticSeverity.Warning,
        });
        // The application continues on the plain loop, so the reasons are warnings, not errors.
        foreach (var reason in reasons)
            host.ReportConfiguration(reason with { Severity = DesktopDiagnosticSeverity.Warning });
    }
}
