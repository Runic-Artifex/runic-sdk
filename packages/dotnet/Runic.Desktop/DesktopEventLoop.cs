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
    // Whether the macOS AppKit loop runs, so native work queued for the main thread runs.
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
    /// <para>
    /// Ctrl+C, SIGTERM and a closed Windows console stop the application in the same order as closing its last
    /// window: <see cref="DesktopHost.ShutdownRequested"/> is cancelled, the open windows close so
    /// <see cref="DesktopWindow.WaitForCloseAsync"/> returns, the application releases its work and returns, and the host
    /// is disposed before the loop ends. An application without a window, such as one behind a status icon, sets
    /// <see cref="DesktopHostOptions.HandlesShutdownWithoutWindow"/> and ends its work when
    /// <see cref="DesktopHost.ShutdownRequested"/> is cancelled; without that option, a signal that arrives while the
    /// host has no surface keeps its default action and ends the process. A second Ctrl+C, a second or
    /// later after the first signal, ends the process immediately; copies forwarded by dotnet run or dotnet watch do
    /// not. Set
    /// <see cref="DesktopHostOptions.HandleShutdownSignals"/> to <see langword="false"/> to keep the default signal
    /// handling.
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

        ShutdownSignals? signals = null;
        try
        {
            // A signal only closes windows and lets the application return; the host is disposed once, below or by
            // RunHostAsync, and each loop ends on its own thread when the application completes.
            if (options.HandleShutdownSignals) signals = ShutdownSignals.Register(host);
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
                signals?.Dispose();
                Exit();
            }
        }
    }

    /// <summary>Runs <paramref name="application"/> on the platform's main-thread loop, without a <see cref="DesktopHost"/>.</summary>
    /// <remarks>
    /// <para>
    /// The low-level loop for a process that uses native platform services but starts no Desktop host, such as a
    /// tool that shows a status icon or calls AppKit APIs on macOS. Call it from the synchronous process entry point.
    /// On macOS it creates the shared <c>NSApplication</c> with Runic's application delegate, runs AppKit's own loop
    /// (<c>[NSApp run]</c>) on the main thread, starts <paramref name="application"/> once AppKit finished launching and
    /// runs the loop until <paramref name="application"/> completes, and then runs the main-thread work it queued last, such as native
    /// release; other platforms join the task.
    /// </para>
    /// <para>
    /// An application that opens windows uses
    /// <see cref="Run(DesktopHostOptions, Func{DesktopHost, Task{int}})"/> instead: it validates the options, runs a
    /// window host's own loop such as GTK 4, handles Ctrl+C and SIGTERM, disposes the host and returns the exit code.
    /// This overload does none of that.
    /// </para>
    /// </remarks>
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
        // NSApp and Runic's application delegate exist before the application's code runs; the main thread then runs
        // AppKit's own loop, which sends finishLaunching once and runs queued main-thread work in every mode.
        var nativeApplication = MacOsApplication.Start();
        Volatile.Write(ref _appKitPumping, 1);
        try
        {
            // The application's work starts on the thread pool once AppKit finished launching.
            MacOsApplication.Run(nativeApplication, application).GetAwaiter().GetResult();
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
