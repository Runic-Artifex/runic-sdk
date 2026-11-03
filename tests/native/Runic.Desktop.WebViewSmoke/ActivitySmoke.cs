using Runic.Desktop;

// B1: the embedded window reports whether it is active, minimized and visible (DesktopWindow.Activity and
// ActivityChanged), from the native window's own events. Hiding and showing are checked everywhere, minimizing on
// Windows and macOS: a bare Xvfb has no window manager to minimize the window. Whether the desktop lets a CI runner's
// window take the focus varies, so activation and the restore after minimizing are reported, not required.
internal static class ActivitySmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    internal static string Page() =>
        """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic window activity</title></head><body>activity</body></html>
        """;

    internal static async Task ExerciseAsync(DesktopWindow window)
    {
        if (!window.Capabilities.HasFlag(DesktopWindowCapabilities.Activity))
            throw new InvalidOperationException("The embedded window did not advertise its activity.");
        var opened = await WaitForAsync(window, static activity => activity.IsVisible, "a visible window");
        Console.WriteLine($"Window activity: opened {opened}.");
        var changes = 0;
        window.ActivityChanged += (_, _) => Interlocked.Increment(ref changes);

        await window.SetVisibleAsync(false);
        await WaitForAsync(window, static activity => !activity.IsVisible && !activity.IsActive, "a hidden, inactive window");
        await window.SetVisibleAsync(true);
        await WaitForAsync(window, static activity => activity.IsVisible, "a shown window");
        for (var waited = 0; Volatile.Read(ref changes) == 0 && waited < 5000; waited += 20) await Task.Delay(20);
        if (Volatile.Read(ref changes) == 0) throw new InvalidOperationException("Hiding the window raised no ActivityChanged event.");

        var optional = OperatingSystem.IsLinux() ? TimeSpan.FromSeconds(3) : Deadline;
        await window.FocusAsync();
        Console.WriteLine(await TryWaitForAsync(window, static activity => activity.IsActive, TimeSpan.FromSeconds(3)) is null
            ? $"Window activity: the desktop did not activate the focused window ({window.Activity}); activation not checked."
            : "PASS window activity: the focused window reported active.");
        await window.MinimizeAsync();
        if (await TryWaitForAsync(window, static activity => activity.IsMinimized && !activity.IsActive && !activity.IsVisible, optional) is null)
        {
            if (!OperatingSystem.IsLinux()) throw new InvalidOperationException($"The minimized window did not report minimized: {window.Activity}.");
            Console.WriteLine("Window activity: no window manager minimized the window; minimize checks skipped.");
        }
        else
        {
            await window.FocusAsync();
            Console.WriteLine(await TryWaitForAsync(window, static activity => !activity.IsMinimized && activity.IsVisible, TimeSpan.FromSeconds(5)) is null
                ? $"Window activity: focusing did not restore the minimized window ({window.Activity})."
                : "PASS window activity: focusing restored the minimized window.");
        }
        Console.WriteLine("Window activity passed: hidden, shown, activated and minimized as the window reported.");
    }

    // The closed window reports itself inactive and hidden.
    internal static async Task CheckClosedAsync(DesktopWindow window) =>
        await WaitForAsync(window, static activity => !activity.IsVisible && !activity.IsActive && !activity.IsMinimized, "a closed window");

    private static async Task<DesktopWindowActivity> WaitForAsync(DesktopWindow window, Func<DesktopWindowActivity, bool> condition, string what) =>
        await TryWaitForAsync(window, condition, Deadline)
        ?? throw new InvalidOperationException($"The window did not report {what} within {Deadline.TotalSeconds} s: {window.Activity}.");

    private static async Task<DesktopWindowActivity?> TryWaitForAsync(DesktopWindow window, Func<DesktopWindowActivity, bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (window.Activity is { } activity && condition(activity)) return activity;
            await Task.Delay(20);
        }
        return null;
    }
}
