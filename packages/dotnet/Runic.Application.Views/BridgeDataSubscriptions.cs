using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

// Generated bridge metadata describes the reachable DTO graph with delegates.
// Keeping the shape in generated code means subscriptions require neither
// reflection nor runtime type discovery (and therefore remain AOT-safe).
public sealed class BridgeDataSubscriptionMember
{
    public BridgeDataSubscriptionMember(
        string name,
        Func<object, object?> read,
        IReadOnlyList<BridgeDataSubscriptionMember>? children = null,
        Func<object, IEnumerable?>? enumerateChildren = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);
        Name = name;
        Read = read;
        Children = children ?? [];
        EnumerateChildren = enumerateChildren;
    }

    internal string Name { get; }
    internal Func<object, object?> Read { get; }
    internal IReadOnlyList<BridgeDataSubscriptionMember> Children { get; }

    // A collection member supplies this explicitly when its elements have
    // generated data metadata. Scalar collections simply omit it.
    internal Func<object, IEnumerable?>? EnumerateChildren { get; }
}

// Own this alongside the bridge instance. On a change the graph is rebuilt
// from authoritative values, which handles property replacement and all
// collection add/remove/reset forms without retaining removed items.
internal sealed class BridgeDataSubscriptions : IDisposable
{
    private readonly object _gate = new();
    private readonly object _root;
    private readonly IReadOnlyList<BridgeDataSubscriptionMember> _members;
    private readonly Action _changed;
    private readonly IBridgeModelTurn _modelTurn;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    internal BridgeDataSubscriptions(
        object root,
        IReadOnlyList<BridgeDataSubscriptionMember> members,
        Action changed)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(changed);
        _root = root;
        _members = members;
        _changed = changed;
        _modelTurn = BridgeModelTurn.For(root);
        _modelTurn.Run(() =>
        {
            lock (_gate) Rebuild();
        });
    }

    private void OnGraphChanged(object? sender, PropertyChangedEventArgs args)
    {
        // ObservableCollection emits these bookkeeping notifications directly
        // before CollectionChanged. The collection event below performs the
        // one authoritative rebuild for that edit.
        if (sender is INotifyCollectionChanged && args.PropertyName is "Count" or "Item[]") return;
        RebuildAndNotify();
    }

    private void OnCollectionChanged(object? _, NotifyCollectionChangedEventArgs __) => RebuildAndNotify();

    private void RebuildAndNotify()
    {
        var rebuilt = _modelTurn.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed) return false;
                Rebuild();
                return true;
            }
        });
        if (rebuilt) _changed();
    }

    private void Rebuild()
    {
        DisposeSubscriptions();
        var paths = new HashSet<TraversalKey>(TraversalKeyComparer.Instance);
        var observed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(_root, _members, paths, observed);
    }

    private void Visit(
        object owner,
        IReadOnlyList<BridgeDataSubscriptionMember> members,
        HashSet<TraversalKey> paths,
        HashSet<object> observed)
    {
        if (!paths.Add(new(owner, members))) return;
        Observe(owner, observed);
        foreach (var member in members)
        {
            var value = member.Read(owner);
            if (value is null) continue;

            Observe(value, observed);
            if (member.EnumerateChildren is { } enumerate)
            {
                foreach (var child in enumerate(value) ?? Array.Empty<object>())
                {
                    if (child is not null) Visit(child, member.Children, paths, observed);
                }
            }
            else if (member.Children.Count > 0)
            {
                Visit(value, member.Children, paths, observed);
            }
        }
    }

    private void Observe(object value, HashSet<object> observed)
    {
        if (!observed.Add(value)) return;
        var collectionChanged = value as INotifyCollectionChanged;
        if (value is INotifyPropertyChanged propertyChanged)
        {
            PropertyChangedEventHandler handler = OnGraphChanged;
            propertyChanged.PropertyChanged += handler;
            _subscriptions.Add(new Subscription(() => propertyChanged.PropertyChanged -= handler));
        }
        if (collectionChanged is not null)
        {
            NotifyCollectionChangedEventHandler handler = OnCollectionChanged;
            collectionChanged.CollectionChanged += handler;
            _subscriptions.Add(new Subscription(() => collectionChanged.CollectionChanged -= handler));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeSubscriptions();
        }
    }

    private void DisposeSubscriptions()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private readonly record struct TraversalKey(object Owner, IReadOnlyList<BridgeDataSubscriptionMember> Members);

    private sealed class TraversalKeyComparer : IEqualityComparer<TraversalKey>
    {
        internal static TraversalKeyComparer Instance { get; } = new();

        public bool Equals(TraversalKey left, TraversalKey right) =>
            ReferenceEquals(left.Owner, right.Owner) && ReferenceEquals(left.Members, right.Members);

        public int GetHashCode(TraversalKey value) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(value.Owner), RuntimeHelpers.GetHashCode(value.Members));
    }
}
