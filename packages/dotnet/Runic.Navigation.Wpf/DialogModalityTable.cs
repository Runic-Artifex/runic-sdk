using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Runic.Navigation.Wpf;

// The shared disable table (W240-001 §8.4): one refcount per HWND for the UI thread, shared by every dialog host.
// A host counts only windows it disabled itself, or that another host disabled, and never re-enables a window
// that something else disabled. A count is a Hold on one table entry: an entry dies with its window, so a
// recycled window handle never inherits counts, and a stale Hold never re-enables an unrelated window.
internal static class DialogModalityTable
{
    [ThreadStatic] private static Dictionary<nint, Hold>? _entries;

    private static Dictionary<nint, Hold> Entries => _entries ??= [];

    // Takes one count on `hwnd` if it is enabled or disabled by a host. Returns the hold, or null.
    public static Hold? TryDisable(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd)) return null;
        Entries.TryGetValue(hwnd, out var entry);
        if (NativeMethods.IsWindowEnabled(hwnd))
        {
            // Hosts hold it but something re-enabled it: keep their counts and add this one.
            entry ??= Track(hwnd);
            entry.Count++;
            NativeMethods.EnableWindow(hwnd, false);
            return entry;
        }
        if (entry is { Count: > 0 })
        {
            entry.Count++;
            return entry;
        }
        // Disabled by the app or a native dialog: not ours to count.
        return null;
    }

    // Releases one count; the last one re-enables the window.
    public static void Release(Hold hold)
    {
        if (hold.Dead || !Entries.TryGetValue(hold.Hwnd, out var entry) || !ReferenceEquals(entry, hold)) return;
        if (--hold.Count > 0) return;
        Forget(hold);
        if (NativeMethods.IsWindow(hold.Hwnd)) NativeMethods.EnableWindow(hold.Hwnd, true);
    }

    internal static int CountOf(nint hwnd) => Entries.TryGetValue(hwnd, out var entry) ? entry.Count : 0;

    private static Hold Track(nint hwnd)
    {
        var entry = new Hold(hwnd);
        Entries[hwnd] = entry;
        // A WPF window's entry ends with its HwndSource.
        if (HwndSource.FromHwnd(hwnd) is { } source)
        {
            entry.Source = source;
            source.Disposed += entry.OnSourceDisposed;
        }
        return entry;
    }

    private static void Forget(Hold entry)
    {
        entry.Dead = true;
        if (Entries.TryGetValue(entry.Hwnd, out var current) && ReferenceEquals(current, entry)) Entries.Remove(entry.Hwnd);
        if (entry.Source is { } source) source.Disposed -= entry.OnSourceDisposed;
        entry.Source = null;
    }

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

    // One window's entry in the table; hosts keep it as the proof of their counts.
    internal sealed class Hold(nint hwnd)
    {
        public nint Hwnd { get; } = hwnd;

        public int Count { get; set; }

        public bool Dead { get; set; }

        public HwndSource? Source { get; set; }

        public void OnSourceDisposed(object? sender, EventArgs e) => Forget(this);
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
