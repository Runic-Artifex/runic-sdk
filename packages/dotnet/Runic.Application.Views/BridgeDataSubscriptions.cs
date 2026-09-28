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
        Func<object, IEnumerable?>? enumerateChildren = null,
        string? propertyName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);
        Name = name;
        PropertyName = propertyName ?? name;
        Read = read;
        Children = children ?? [];
        EnumerateChildren = enumerateChildren;
    }

    internal string Name { get; }
    internal string PropertyName { get; }
    internal Func<object, object?> Read { get; }
    internal IReadOnlyList<BridgeDataSubscriptionMember> Children { get; }

    // A collection member supplies this explicitly when its elements have
    // generated data metadata. Mutable List and Dictionary instances without
    // INotifyCollectionChanged are supported as snapshot values: replace the
    // owning property (or notify it) after changing their contents.
    internal Func<object, IEnumerable?>? EnumerateChildren { get; }
}

// Own this alongside the bridge instance. The retained graph gives each
// reachable owner/property and collection edge a small subscription. Edits
// reconcile only the affected edge, retaining subscriptions for every other
// branch. Nodes are reference-keyed, which preserves shared DTOs and cycles.
internal sealed class BridgeDataSubscriptions : IDisposable
{
    private readonly object _gate = new();
    private readonly object _root;
    private readonly IReadOnlyList<BridgeDataSubscriptionMember> _members;
    private readonly Action _changed;
    private readonly IBridgeModelTurn _modelTurn;
    private readonly Dictionary<NodeKey, Node> _nodes = new(NodeKeyComparer.Instance);
    private readonly Dictionary<object, PropertyWatch> _propertyWatches =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<INotifyCollectionChanged, CollectionWatch> _collectionWatches =
        new(ReferenceEqualityComparer.Instance);
    private readonly NodeKey _rootKey;
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
        _rootKey = new(root, members);
        _modelTurn.Run(() =>
        {
            lock (_gate) AddNode(root, members);
        });
    }

    private void OnPropertyChanged(INotifyPropertyChanged owner, PropertyChangedEventArgs args)
    {
        // ObservableCollection raises bookkeeping properties immediately
        // before CollectionChanged. Its collection event reconciles the edge.
        if (owner is INotifyCollectionChanged && args.PropertyName is "Count" or "Item[]") return;
        var changed = _modelTurn.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_propertyWatches.TryGetValue(owner, out var watch)) return false;
                var topologyMayHaveChanged = false;
                foreach (var node in watch.Nodes.ToArray())
                {
                    foreach (var edge in node.Edges)
                    {
                        if (string.IsNullOrEmpty(args.PropertyName) ||
                            string.Equals(edge.Member.PropertyName, args.PropertyName, StringComparison.Ordinal) ||
                            string.Equals(edge.Member.Name, args.PropertyName, StringComparison.OrdinalIgnoreCase))
                        {
                            RefreshEdge(edge, forceCollectionRefresh: true);
                            topologyMayHaveChanged |= edge.Member.Children.Count > 0 || edge.Member.EnumerateChildren is not null;
                        }
                    }
                }
                // Scalar leaf updates still publish a new snapshot, but they
                // cannot alter reachability. Avoid walking the entire graph
                // and allocating a reachability set on that hot path.
                if (topologyMayHaveChanged) SweepDetached();
                return true;
            }
        });
        if (changed) _changed();
    }

    private void OnCollectionChanged(INotifyCollectionChanged collection, NotifyCollectionChangedEventArgs args)
    {
        var changed = _modelTurn.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_collectionWatches.TryGetValue(collection, out var watch)) return false;
                foreach (var edge in watch.Edges.ToArray()) ReconcileCollection(edge, args);
                SweepDetached();
                return true;
            }
        });
        if (changed) _changed();
    }

    private void ReconcileCollection(Edge edge, NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                AddItems(edge, args.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove:
                RemoveItems(edge, args.OldItems);
                break;
            case NotifyCollectionChangedAction.Replace:
                RemoveItems(edge, args.OldItems);
                AddItems(edge, args.NewItems);
                break;
            case NotifyCollectionChangedAction.Reset:
                edge.Items.Clear();
                AddItems(edge, edge.Member.EnumerateChildren!(edge.Value!));
                break;
            // A move does not alter membership, but callers still receive a
            // snapshot publication for the ordering change.
            case NotifyCollectionChangedAction.Move:
                break;
            default:
                edge.Items.Clear();
                AddItems(edge, edge.Member.EnumerateChildren!(edge.Value!));
                break;
        }
    }

    private void RefreshEdge(Edge edge, bool forceCollectionRefresh = false)
    {
        var next = edge.Member.Read(edge.Node.Owner);
        if (ReferenceEquals(next, edge.Value))
        {
            // Plain List/Dictionary values have no collection event. An
            // explicit owner property notification is their opt-in signal to
            // reconcile nested subscriptions without replacing the instance.
            if (forceCollectionRefresh && next is not null && edge.Member.EnumerateChildren is { } snapshotEnumerate)
            {
                edge.Items.Clear();
                AddItems(edge, snapshotEnumerate(next));
            }
            return;
        }
        DetachCollection(edge);
        edge.Value = next;
        edge.ValueChild = null;
        edge.Items.Clear();
        if (next is null) return;

        if (edge.Member.EnumerateChildren is { } enumerate)
        {
            AttachCollection(edge, next as INotifyCollectionChanged);
            AddItems(edge, enumerate(next));
        }
        else if (edge.Member.Children.Count > 0)
        {
            edge.ValueChild = AddNode(next, edge.Member.Children);
        }
    }

    private void AddItems(Edge edge, IEnumerable? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is null) continue;
            var key = AddNode(item, edge.Member.Children);
            if (key is { } nodeKey)
                edge.Items[nodeKey] = edge.Items.TryGetValue(nodeKey, out var count) ? count + 1 : 1;
        }
    }

    private static void RemoveItems(Edge edge, IList? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is null) continue;
            var key = new NodeKey(item, edge.Member.Children);
            if (!edge.Items.TryGetValue(key, out var count)) continue;
            if (count == 1) edge.Items.Remove(key);
            else edge.Items[key] = count - 1;
        }
    }

    private NodeKey? AddNode(object owner, IReadOnlyList<BridgeDataSubscriptionMember> members)
    {
        // Boxing makes value-type identity unstable; value types cannot raise
        // useful nested notifications and are represented by their owner read.
        if (owner.GetType().IsValueType) return null;
        var key = new NodeKey(owner, members);
        if (_nodes.ContainsKey(key)) return key;

        var node = new Node(owner, members);
        _nodes.Add(key, node); // Reserve before children so recursive DTOs terminate.
        AttachProperty(node);
        foreach (var member in members)
        {
            var edge = new Edge(node, member);
            node.Edges.Add(edge);
            RefreshEdge(edge);
        }
        return key;
    }

    private void AttachProperty(Node node)
    {
        if (node.Owner is not INotifyPropertyChanged source) return;
        if (!_propertyWatches.TryGetValue(source, out var watch))
        {
            watch = new PropertyWatch(this, source);
            _propertyWatches.Add(source, watch);
            source.PropertyChanged += watch.Handler;
        }
        watch.Nodes.Add(node);
    }

    private void DetachProperty(Node node)
    {
        if (node.Owner is not INotifyPropertyChanged source ||
            !_propertyWatches.TryGetValue(source, out var watch)) return;
        watch.Nodes.Remove(node);
        if (watch.Nodes.Count != 0) return;
        source.PropertyChanged -= watch.Handler;
        _propertyWatches.Remove(source);
    }

    private void AttachCollection(Edge edge, INotifyCollectionChanged? source)
    {
        if (source is null) return;
        edge.Collection = source;
        if (!_collectionWatches.TryGetValue(source, out var watch))
        {
            watch = new CollectionWatch(this, source);
            _collectionWatches.Add(source, watch);
            source.CollectionChanged += watch.Handler;
        }
        watch.Edges.Add(edge);
    }

    private void DetachCollection(Edge edge)
    {
        if (edge.Collection is not { } source || !_collectionWatches.TryGetValue(source, out var watch))
        {
            edge.Collection = null;
            return;
        }
        watch.Edges.Remove(edge);
        edge.Collection = null;
        if (watch.Edges.Count != 0) return;
        source.CollectionChanged -= watch.Handler;
        _collectionWatches.Remove(source);
    }

    private void SweepDetached()
    {
        var reachable = new HashSet<NodeKey>(NodeKeyComparer.Instance);
        MarkReachable(_rootKey, reachable);
        foreach (var pair in _nodes.ToArray())
        {
            if (reachable.Contains(pair.Key)) continue;
            foreach (var edge in pair.Value.Edges) DetachCollection(edge);
            DetachProperty(pair.Value);
            _nodes.Remove(pair.Key);
        }
    }

    private void MarkReachable(NodeKey key, HashSet<NodeKey> reachable)
    {
        if (!reachable.Add(key) || !_nodes.TryGetValue(key, out var node)) return;
        foreach (var edge in node.Edges)
        {
            if (edge.ValueChild is { } child) MarkReachable(child, reachable);
            foreach (var item in edge.Items.Keys) MarkReachable(item, reachable);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var watch in _propertyWatches.Values) watch.Source.PropertyChanged -= watch.Handler;
            foreach (var watch in _collectionWatches.Values) watch.Source.CollectionChanged -= watch.Handler;
            _propertyWatches.Clear();
            _collectionWatches.Clear();
            _nodes.Clear();
        }
    }

    private sealed class Node(object owner, IReadOnlyList<BridgeDataSubscriptionMember> members)
    {
        internal object Owner { get; } = owner;
        internal IReadOnlyList<BridgeDataSubscriptionMember> Members { get; } = members;
        internal List<Edge> Edges { get; } = [];
    }

    private sealed class Edge(Node node, BridgeDataSubscriptionMember member)
    {
        internal Node Node { get; } = node;
        internal BridgeDataSubscriptionMember Member { get; } = member;
        internal object? Value { get; set; }
        internal NodeKey? ValueChild { get; set; }
        internal Dictionary<NodeKey, int> Items { get; } = new(NodeKeyComparer.Instance);
        internal INotifyCollectionChanged? Collection { get; set; }
    }

    private sealed class PropertyWatch
    {
        internal PropertyWatch(BridgeDataSubscriptions owner, INotifyPropertyChanged source)
        {
            Source = source;
            Handler = (_, args) => owner.OnPropertyChanged(source, args);
        }
        internal INotifyPropertyChanged Source { get; }
        internal PropertyChangedEventHandler Handler { get; }
        internal HashSet<Node> Nodes { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class CollectionWatch
    {
        internal CollectionWatch(BridgeDataSubscriptions owner, INotifyCollectionChanged source)
        {
            Source = source;
            Handler = (_, args) => owner.OnCollectionChanged(source, args);
        }
        internal INotifyCollectionChanged Source { get; }
        internal NotifyCollectionChangedEventHandler Handler { get; }
        internal HashSet<Edge> Edges { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private readonly record struct NodeKey(object Owner, IReadOnlyList<BridgeDataSubscriptionMember> Members);

    private sealed class NodeKeyComparer : IEqualityComparer<NodeKey>
    {
        internal static NodeKeyComparer Instance { get; } = new();
        public bool Equals(NodeKey left, NodeKey right) =>
            ReferenceEquals(left.Owner, right.Owner) && ReferenceEquals(left.Members, right.Members);
        public int GetHashCode(NodeKey value) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(value.Owner), RuntimeHelpers.GetHashCode(value.Members));
    }
}
