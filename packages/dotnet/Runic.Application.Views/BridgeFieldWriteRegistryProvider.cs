using System.ComponentModel;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FieldRegistryProviderProbe")]

namespace Runic.Application.Views;

// Window/session-owned field registry provider. It intentionally has no
// global instance: receipt history and request IDs belong to one window owner,
// while BridgeModelTurn supplies the shared per-model synchronous turn.
internal sealed class BridgeFieldWriteRegistryProvider : IDisposable
{
    private readonly object _gate = new();
    private readonly string _ownerId;
    private readonly Dictionary<object, ModelEntries> _models = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    internal BridgeFieldWriteRegistryProvider(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        _ownerId = ownerId;
    }

    // The first registration for a (model identity, contract, property) key
    // owns its read, apply, validation, and receipt-retention semantics.
    // Later callers receive that same registry; delegate compatibility is not
    // inferred from closures or method bodies.
    internal BridgeFieldWriteRegistry<T> GetOrCreate<T>(
        INotifyPropertyChanged model,
        string contract,
        string property,
        Func<T> read,
        Action<T> apply,
        int maximumRetainedWrites = 64,
        Func<T, string?>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(apply);

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
                return (BridgeFieldWriteRegistry<T>)existing.Registry;
            }

            var turn = BridgeModelTurn.For(model);
            var registry = new BridgeFieldWriteRegistry<T>(
                $"{_ownerId}:{contract}:{property}", property, read, apply,
                maximumRetainedWrites, validate, turn);
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
            entries.Fields.Add(key, new(typeof(T), registry, () =>
            {
                model.PropertyChanged -= observer;
                registry.Dispose();
            }));
            return registry;
        }
    }

    // Releases the provider's strong model reference and its single observer
    // when a dynamic view model leaves this window. This is intentionally a
    // separate seam from WindowContentSession.Forget until their ownership
    // lifetimes are integrated.
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
        if (_disposed) throw new ObjectDisposedException(nameof(BridgeFieldWriteRegistryProvider));
    }

    private sealed class ModelEntries
    {
        public Dictionary<FieldKey, Entry> Fields { get; } = [];
    }

    private sealed class Entry(Type valueType, object registry, Action dispose) : IDisposable
    {
        public Type ValueType { get; } = valueType;
        public object Registry { get; } = registry;
        public void Dispose() => dispose();
    }

    private readonly record struct FieldKey(string Contract, string Property);
}
