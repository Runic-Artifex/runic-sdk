namespace Runic.Desktop.Internal;

// The owners that window-modal windows block (DesktopWindowModality.Window), by native handle, for the built-in hosts:
// an owner takes input again only when the last window modal to it closed.
internal static class ModalOwners
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, int> Counts = [];

    // True for the first window modal to owner: the caller blocks the owner.
    internal static bool Acquire(nint owner)
    {
        lock (Gate)
        {
            Counts.TryGetValue(owner, out var count);
            Counts[owner] = count + 1;
            return count == 0;
        }
    }

    // True when the last window modal to owner closed: the caller unblocks the owner.
    internal static bool Release(nint owner)
    {
        lock (Gate)
        {
            if (!Counts.TryGetValue(owner, out var count)) return false;
            if (count > 1)
            {
                Counts[owner] = count - 1;
                return false;
            }
            Counts.Remove(owner);
            return true;
        }
    }

    internal static int Count(nint owner)
    {
        lock (Gate) return Counts.GetValueOrDefault(owner);
    }
}
