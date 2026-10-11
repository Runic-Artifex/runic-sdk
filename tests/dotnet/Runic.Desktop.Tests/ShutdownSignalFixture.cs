using System.Runtime.InteropServices;

namespace Runic.Desktop.Tests;

// The test assembly doubles as the application that ShutdownSignalTests stops with a signal.
internal static class Program
{
    private static int Main(string[] args) =>
        args is [ShutdownSignalFixture.Argument, var mode] ? ShutdownSignalFixture.Run(mode) : 0;
}

// A Desktop application shaped like the template: it opens one window, waits for it to close, then drains its
// accepted work before returning. It reports each step on standard output.
internal static partial class ShutdownSignalFixture
{
    internal const string Argument = "--shutdown-signal-fixture";
    internal const int ApplicationExitCode = 3;

    internal static int Run(string mode)
    {
        var factory = new ClosableWindowHostFactory();
        DesktopHost? started = null;
        var exitCode = DesktopEventLoop.Run(
            new DesktopHostOptions
            {
                WindowHostFactory = factory,
                WaitForConnection = false,
                HandleShutdownSignals = mode != "opt-out",
            },
            async host =>
            {
                started = host;
                host.ShutdownRequested.Register(static () => Report("shutdown-requested"));
                await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
                {
                    Content = new DesktopContent.Html("fixture"),
                });
                var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
                // Accepted work still running when the signal arrives, as an AcceptedWorkScope would own it.
                var work = Task.Run(static async () =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    Report("work-completed");
                });
                Report("ready");
                if (mode == "stuck")
                {
                    // Ignores the shutdown request, so only a second signal can end the process.
                    await Task.Delay(Timeout.Infinite);
                }
                await window.WaitForCloseAsync();
                Report("window-closed");
                await work;
                Report("work-drained");
                return ApplicationExitCode;
            });
        try
        {
            started?.Validate();
            Report("host-alive");
        }
        catch (ObjectDisposedException)
        {
            Report(factory.Host is { Disposed: true } ? "host-disposed" : "window-host-leaked");
        }
        return exitCode;
    }

    private static void Report(string step)
    {
        Console.Out.WriteLine(step);
        Console.Out.Flush();
        // On Windows the test cannot signal another console, so it asks the fixture to raise Ctrl+C on its own one.
        if (step == "ready" && OperatingSystem.IsWindows()) _ = Task.Run(RaiseCtrlCOnRequest);
    }

    private static void RaiseCtrlCOnRequest()
    {
        // A process can inherit Ctrl+C disabled; the fixture must receive it like a console application does.
        SetConsoleCtrlHandler(0, false);
        while (Console.In.ReadLine() is { } line)
        {
            if (line != "ctrl-c") continue;
            // Ctrl+C reaches every process on the console; never send it to a console the test runner shares.
            var attached = GetConsoleProcessList(new uint[2], 2);
            if (attached != 1) Report($"ctrl-c-refused {attached} console processes");
            else if (!GenerateConsoleCtrlEvent(0, 0)) Report($"ctrl-c-failed {Marshal.GetLastPInvokeError()}");
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList([Out] uint[] processIds, uint count);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(nint handler, [MarshalAs(UnmanagedType.Bool)] bool add);
}

// An embedded window that closes when asked, like a native window the user closes.
internal sealed class ClosableWindowHostFactory : IDesktopWindowHostFactory
{
    internal ClosableWindowHost? Host { get; private set; }
    public bool IsSupported => true;
    public IDesktopWindowHost Create() => Host = new ClosableWindowHost();
}

internal sealed class ClosableWindowHost : IDesktopWindowHost
{
    private int _open;
    public event EventHandler? Closed;
    public bool IsOpen => Volatile.Read(ref _open) != 0;
    public bool Disposed { get; private set; }
    public bool SupportsCloseConfirmation => true;
    public nint NativeHandle => IsOpen ? 1 : 0;

    public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _open, 1);
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _open, 0) != 0) Closed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _open, 0);
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
