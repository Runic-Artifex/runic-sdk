namespace Runic.Desktop.Internal;

// An engine's native ephemeral data stores by key (DesktopWindowHostOptions.EphemeralDataStore), on its UI thread: the
// first window of a key creates the store, each window holds it, and the last window to close frees it. A key's store
// lives while a window of the key is open, so a host session whose windows all closed starts with an empty store when
// it opens another. Runic.Desktop.Gtk4 compiles this file too.
internal sealed class EphemeralDataStoreCache<T> where T : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (T Store, int Windows)> _stores = new(StringComparer.Ordinal);

    // The key's store for one more window.
    internal T Acquire(string key, Func<T> create)
    {
        lock (_gate)
        {
            if (_stores.TryGetValue(key, out var entry))
            {
                _stores[key] = (entry.Store, entry.Windows + 1);
                return entry.Store;
            }
            var store = create();
            _stores[key] = (store, 1);
            return store;
        }
    }

    // One window of the key is gone; free runs for the last one.
    internal void Release(string key, Action<T> free)
    {
        T store;
        lock (_gate)
        {
            if (!_stores.TryGetValue(key, out var entry)) return;
            if (entry.Windows > 1)
            {
                _stores[key] = (entry.Store, entry.Windows - 1);
                return;
            }
            _stores.Remove(key);
            store = entry.Store;
        }
        free(store);
    }
}
