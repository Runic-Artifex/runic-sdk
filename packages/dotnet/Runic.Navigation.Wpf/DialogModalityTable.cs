using System.Runtime.InteropServices;

namespace Runic.Navigation.Wpf;

// The shared disable table (W240-001 §8.4): one refcount per HWND for the UI thread, shared by every dialog host.
// A host counts only windows it disabled itself, or that another host disabled, and never re-enables a window
// that something else disabled.
internal static class DialogModalityTable
{
    [ThreadStatic] private static Dictionary<nint, int>? _counts;

    private static Dictionary<nint, int> Counts => _counts ??= [];

    // Takes one count on `hwnd` if it is enabled or disabled by a host. Returns whether it took one.
    public static bool TryDisable(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return false;
        if (NativeMethods.IsWindowEnabled(hwnd))
        {
            Counts[hwnd] = 1;
            NativeMethods.EnableWindow(hwnd, false);
            return true;
        }
        if (Counts.TryGetValue(hwnd, out var count) && count > 0)
        {
            Counts[hwnd] = count + 1;
            return true;
        }
        // Disabled by the app or a native dialog: not ours to count.
        return false;
    }

    // Releases one count; the last one re-enables the window.
    public static void Release(nint hwnd)
    {
        if (!Counts.TryGetValue(hwnd, out var count)) return;
        if (count > 1)
        {
            Counts[hwnd] = count - 1;
            return;
        }
        Counts.Remove(hwnd);
        if (NativeMethods.IsWindow(hwnd)) NativeMethods.EnableWindow(hwnd, true);
    }

    internal static int CountOf(nint hwnd) => Counts.TryGetValue(hwnd, out var count) ? count : 0;

    // The visible top-level windows of the calling thread.
    public static List<nint> ThreadWindows()
    {
        var windows = new List<nint>();
        var handle = GCHandle.Alloc(windows);
        try
        {
            unsafe
            {
                NativeMethods.EnumThreadWindows(NativeMethods.GetCurrentThreadId(), &Collect, GCHandle.ToIntPtr(handle));
            }
        }
        finally { handle.Free(); }
        return windows;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint state)
    {
        if (NativeMethods.IsWindowVisible(hwnd)) ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(hwnd);
        return 1;
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowEnabled(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumThreadWindows(uint threadId, delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();
}
