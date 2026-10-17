using Runic.Desktop;

// W315-001 and W315-017: the embedded window reports its bounds, maximized state and changes, and restores a saved
// placement onto a monitor that exists now. The first placement stands for one saved on a monitor that is no longer
// connected: far outside every screen and larger than any of them.
internal static class WindowStateSmoke
{
    // Small enough for xvfb-run's default 640x480 screen.
    private const uint ResizedWidth = 420;
    private const uint ResizedHeight = 300;
    private const int MovedX = 40;
    private const int MovedY = 60;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static string Page() =>
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic window state</title></head><body>state</body></html>
        """;

    internal static DesktopWindowOptions DisconnectedMonitorOptions() => new()
    {
        Browser = BrowserKind.Embedded,
        Placement = new DesktopWindowPlacement { X = -100_000, Y = -100_000, Width = 20_000, Height = 15_000 },
    };

    internal static DesktopWindowOptions RestoreOptions(DesktopWindowPlacement placement) => new()
    {
        Browser = BrowserKind.Embedded,
        Placement = placement,
    };

    // Returns the placement the window would save when it closes now, maximized.
    internal static async Task<DesktopWindowPlacement> ExerciseAsync(DesktopWindow window)
    {
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.State))
            throw new InvalidOperationException("The embedded window did not advertise its state.");
        var opened = await WaitForAsync(window, static state => true, "an initial state");
        Console.WriteLine($"Window state: opened from a disconnected-monitor placement at {opened}.");
        if (opened.X is { } x && opened.Y is { } y && (x <= -50_000 || y <= -50_000))
            throw new InvalidOperationException($"The restored window stayed off screen: {opened}.");
        if (opened.Width >= 20_000 || opened.Height >= 15_000)
            throw new InvalidOperationException($"The restored window was not shrunk to a monitor: {opened}.");

        var changes = 0;
        window.StateChanged += (_, _) => Interlocked.Increment(ref changes);
        await window.ResizeAsync(ResizedWidth, ResizedHeight);
        var resized = await WaitForAsync(window, static state => Near(state.Width, ResizedWidth) && Near(state.Height, ResizedHeight),
            $"the size {ResizedWidth}x{ResizedHeight}");
        if (Volatile.Read(ref changes) == 0)
            throw new InvalidOperationException("Resizing the window raised no StateChanged event.");
        var moves = window.Capabilities.HasFlag(DesktopWindowCapabilities.Move) && resized.X is not null;
        if (moves)
        {
            await window.MoveAsync(MovedX, MovedY);
            await WaitForAsync(window, static state => state.X == MovedX && state.Y == MovedY, $"the position {MovedX},{MovedY}");
        }

        await window.ToggleMaximizedAsync();
        // Without a window manager, as under Xvfb, X11 has nobody to maximize the window.
        var maximized = await TryWaitForAsync(window, static state => state.IsMaximized,
            OperatingSystem.IsLinux() ? TimeSpan.FromSeconds(3) : Deadline);
        if (maximized is null)
        {
            if (!OperatingSystem.IsLinux())
                throw new InvalidOperationException($"The window did not report maximized: {window.State}.");
            Console.WriteLine("Window state: no window manager maximized the window; maximized checks skipped.");
            var unmaximized = window.State!.Placement with { Maximized = true };
            CheckRestoreBounds(unmaximized, moves);
            return unmaximized;
        }
        Console.WriteLine($"Window state: maximized to {maximized}.");
        CheckRestoreBounds(maximized.Placement, moves);
        if (!maximized.Placement.Maximized)
            throw new InvalidOperationException("The placement of a maximized window was not marked maximized.");

        await window.ToggleMaximizedAsync();
        await WaitForAsync(window, static state => !state.IsMaximized && Near(state.Width, ResizedWidth), "the restored size");
        await window.ToggleMaximizedAsync();
        var saved = await WaitForAsync(window, static state => state.IsMaximized, "maximized again");
        return saved.Placement;
    }

    // After the window closed, its last state stays readable: the save path.
    internal static void CheckSavedAfterClose(DesktopWindow window, DesktopWindowPlacement saved)
    {
        if (window.IsOpen) throw new InvalidOperationException("The window state check expected a closed window.");
        var last = window.State ?? throw new InvalidOperationException("A closed window lost its last state.");
        if (last.Placement with { Maximized = saved.Maximized } != saved)
            throw new InvalidOperationException($"A closed window reported {last.Placement} instead of the saved {saved}.");
    }

    internal static async Task ExerciseRestoredAsync(DesktopWindow window, DesktopWindowPlacement saved)
    {
        var restored = await WaitForAsync(window,
            state => (!saved.Maximized || OperatingSystem.IsLinux() || state.IsMaximized)
                && Near(state.Placement.Width, saved.Width) && Near(state.Placement.Height, saved.Height),
            $"the restored placement {saved}");
        if (saved.Maximized && OperatingSystem.IsLinux() && !restored.IsMaximized)
            Console.WriteLine("Window state: no window manager maximized the restored window.");
        Console.WriteLine($"Window state: restored {saved} as {restored}.");
    }

    private static void CheckRestoreBounds(DesktopWindowPlacement placement, bool moves)
    {
        if (!Near(placement.Width, ResizedWidth) || !Near(placement.Height, ResizedHeight)
            || (moves && (placement.X != MovedX || placement.Y != MovedY)))
            throw new InvalidOperationException($"The placement did not keep the restored bounds: {placement}.");
    }

    // Device-independent sizes can round by a pixel at fractional scales.
    private static bool Near(uint actual, uint expected) => Math.Abs((long)actual - expected) <= 2;

    private static async Task<DesktopWindowState> WaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, string expected) =>
        await TryWaitForAsync(window, condition, Deadline)
            ?? throw new TimeoutException($"The window did not report {expected}; last state: {window.State?.ToString() ?? "<none>"}.");

    private static async Task<DesktopWindowState?> TryWaitForAsync(DesktopWindow window, Func<DesktopWindowState, bool> condition, TimeSpan timeout)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            if (window.State is { } state && condition(state)) return state;
            await Task.Delay(20);
        }
        return null;
    }
}
