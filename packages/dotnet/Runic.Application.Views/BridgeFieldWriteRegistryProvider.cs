using System.ComponentModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Runic.Application.Testing.Tests")]
[assembly: InternalsVisibleTo("Runic.Application.Testing")]

namespace Runic.Application.Views;

// Window/session-owned field registry provider. It intentionally has no
// global instance: receipt history and request IDs belong to one window owner,
// while BridgeModelTurn supplies the shared per-model synchronous turn.
//
// Each attached bridge holds a lease on the registries it uses. A field's
// registry, its PropertyChanged observer, and the provider's strong model
// reference are released when the last bridge presenting that model in this
// window detaches, so churning content does not accumulate retained models.
internal sealed class BridgeFieldWriteRegistryProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly string _ownerId;
    private readonly int _maximumRetainedReceiptBytes;
    private readonly Dictionary<object, ModelEntries> _models = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal BridgeFieldWriteRegistryProvider(string ownerId, int maximumRetainedReceiptBytes = 262_144)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedReceiptBytes);
        _ownerId = ownerId;
        _maximumRetainedReceiptBytes = maximumRetainedReceiptBytes;
    }

    // Number of models with at least one live field registry.
    internal int RetainedModelCount
    {
        get { lock (_gate) return _models.Count; }
    }

    // The first registration for a (model identity, contract, property) key
    // owns its read, apply, validation, and receipt-retention semantics.
    // Later callers receive that same registry; delegate compatibility is not
    // inferred from closures or method bodies. Dispose the returned lease when
    // the caller no longer presents the field.
    internal BridgeFieldWriteLease<T> Acquire<T>(
        INotifyPropertyChanged model,
        string contract,
        string property,
        Func<T> read,
        Action<T> apply,
        int maximumRetainedWrites = 64,
        Func<T, string?>? validate = null,
        Func<T, T>? snapshot = null,
        IEqualityComparer<T>? equalityComparer = null,
        Func<T, string>? canonicalize = null,
        Func<T, int>? retainedValueByteCount = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(apply);

        // The provider may allocate a registry, whose initial snapshot reads
        // the model. Acquire the shared model turn before the provider gate so
        // a snapshot callback that exposes checked fields cannot invert
        // model-turn -> provider ownership.
        var turn = BridgeModelTurn.For(model);
        return turn.Run(() => AcquireCore(model, contract, property, read, apply,
            maximumRetainedWrites, validate, snapshot, equalityComparer, canonicalize, retainedValueByteCount, turn));
    }

    private BridgeFieldWriteLease<T> AcquireCore<T>(
        INotifyPropertyChanged model,
        string contract,
        string property,
        Func<T> read,
        Action<T> apply,
        int maximumRetainedWrites,
        Func<T, string?>? validate,
        Func<T, T>? snapshot,
        IEqualityComparer<T>? equalityComparer,
        Func<T, string>? canonicalize,
        Func<T, int>? retainedValueByteCount,
        BridgeModelTurn turn)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_models.TryGetValue(model, out var entries))
            {
                entries = new();
                _models.Add(model, entries);
            }

            var key = new FieldKey(contract, property);
            if (entries.Fields.TryGetValue(key, out var existing))
            {
                if (existing.ValueType != typeof(T))
                    throw new InvalidOperationException($"{contract}.{property} was requested with incompatible field types.");
                existing.References++;
                return new((BridgeFieldWriteRegistry<T>)existing.Registry, new Lease(this, model, key, existing));
            }

            var registry = new BridgeFieldWriteRegistry<T>(
                $"{_ownerId}:{contract}:{property}", property, read, apply,
                maximumRetainedWrites, validate, turn, snapshot, equalityComparer,
                _maximumRetainedReceiptBytes, canonicalize, retainedValueByteCount);
            PropertyChangedEventHandler observer = (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.PropertyName) && args.PropertyName != property) return;
                // Do not capture the field outside the model turn. A
                // background PropertyChanged callback could otherwise read an
                // old value, wait on the turn, then fail the registry's
                // authoritative re-read after another writer commits.
                try { turn.Run(() => registry.ObserveExternal(read())); }
                catch (ObjectDisposedException) { }
            };
            model.PropertyChanged += observer;
            var entry = new Entry(typeof(T), registry, () =>
            {
                model.PropertyChanged -= observer;
                registry.Dispose();
            }) { References = 1 };
            entries.Fields.Add(key, entry);
            return new(registry, new Lease(this, model, key, entry));
        }
    }

    // Releases the provider's strong model reference and its observers
    // regardless of outstanding leases.
    internal void Forget(INotifyPropertyChanged model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Entry[] entries;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_models.Remove(model, out var modelEntries)) return;
            entries = modelEntries.Fields.Values.ToArray();
        }
        foreach (var entry in entries) entry.Dispose();
    }

    private void Release(object model, FieldKey key, Entry entry)
    {
        lock (_gate)
        {
            if (_disposed || !_models.TryGetValue(model, out var entries)
                || !entries.Fields.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) return;
            if (--entry.References != 0) return;
            entries.Fields.Remove(key);
            if (entries.Fields.Count == 0) _models.Remove(model);
        }
        entry.Dispose();
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _models.Values.SelectMany(value => value.Fields.Values).ToArray();
            _models.Clear();
        }
        foreach (var entry in entries) entry.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ModelEntries
    {
        public Dictionary<FieldKey, Entry> Fields { get; } = [];
    }

    private sealed class Entry(Type valueType, object registry, Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public Type ValueType { get; } = valueType;
        public object Registry { get; } = registry;
        public int References { get; set; }
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class Lease(BridgeFieldWriteRegistryProvider owner, object model, FieldKey key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(model, key, entry);
        }
    }

    private readonly record struct FieldKey(string Contract, string Property);
}

internal readonly record struct BridgeFieldWriteLease<T>(BridgeFieldWriteRegistry<T> Registry, IDisposable Lease);
