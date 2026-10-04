using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

// Generated bridge metadata describes the reachable DTO graph with delegates.
// Keeping the shape in generated code means subscriptions require neither
// reflection nor runtime type discovery (and therefore remain AOT-safe).
/// <summary>Describes one observable member of a bridged data graph. Created by generated bridges.</summary>
public sealed class BridgeDataSubscriptionMember
{
    /// <summary>Describes a member and, optionally, its observable children.</summary>
    /// <param name="name">The member's wire name.</param>
    /// <param name="read">Reads the member value from its owner.</param>
    /// <param name="children">Members of the value's own data type.</param>
    /// <param name="enumerateChildren">Enumerates collection elements that have generated data metadata.</param>
    /// <param name="propertyName">The .NET property name, when it differs from <paramref name="name"/>.</param>
    /// <param name="enumerateValidationChildren">Enumerates children whose validation errors are reported.</param>
    /// <param name="isPathTransparent">Whether the member adds no segment to validation paths.</param>
    public BridgeDataSubscriptionMember(
        string name,
        Func<object, object?> read,
        IReadOnlyList<BridgeDataSubscriptionMember>? children = null,
        Func<object, IEnumerable?>? enumerateChildren = null,
        string? propertyName = null,
        Func<object, IEnumerable<BridgeValidationChild>>? enumerateValidationChildren = null,
        bool isPathTransparent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);
        Name = name;
        PropertyName = propertyName ?? name;
        Read = read;
        Children = children ?? [];
        EnumerateChildren = enumerateChildren;
        EnumerateValidationChildren = enumerateValidationChildren;
        IsPathTransparent = isPathTransparent;
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

    // The validation traversal needs each collection's wire path segment as
    // well as its value. Subscription reconciliation intentionally retains
    // only values, so this generated delegate stays separate.
    internal Func<object, IEnumerable<BridgeValidationChild>>? EnumerateValidationChildren { get; }
    internal bool IsPathTransparent { get; }
}

// Own this alongside the bridge instance. The retained graph gives each
// reachable owner/property and collection edge a small subscription. Edits
// reconcile only the affected edge, retaining subscriptions for every other
// branch. Nodes are reference-keyed, which preserves shared DTOs and cycles.
internal sealed class BridgeDataSubscriptions : IDisposable, IBridgeSnapshotBatchParticipant
{
    private readonly object _gate = new();
    private readonly object _root;
    private readonly IReadOnlyList<BridgeDataSubscriptionMember> _members;
    private readonly Action _changed;
    private readonly Func<INotifyCollectionChanged, NotifyCollectionChangedEventArgs, bool>? _collectionChanged;
    private readonly Func<object, bool>? _itemChanged;
    private readonly bool _rootObservedExternally;
    private readonly IReadOnlySet<string>? _incrementalCollectionNames;
    private readonly BridgeModelTurn _modelTurn;
    private readonly Dictionary<NodeKey, Node> _nodes = new(NodeKeyComparer.Instance);
    private readonly Dictionary<NodeKey, int> _incomingReferences = new(NodeKeyComparer.Instance);
    private readonly Dictionary<object, PropertyWatch> _propertyWatches =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<INotifyCollectionChanged, CollectionWatch> _collectionWatches =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<INotifyDataErrorInfo, ErrorWatch> _errorWatches =
        new(ReferenceEqualityComparer.Instance);
    private readonly NodeKey _rootKey;
    private bool _disposed;

    internal BridgeDataSubscriptions(
        object root,
        IReadOnlyList<BridgeDataSubscriptionMember> members,
        Action changed,
        Func<INotifyCollectionChanged, NotifyCollectionChangedEventArgs, bool>? collectionChanged = null,
        Func<object, bool>? itemChanged = null,
        bool rootObservedExternally = false,
        IReadOnlySet<string>? incrementalCollectionNames = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(changed);
        _root = root;
        _members = members;
        _changed = changed;
        _collectionChanged = collectionChanged;
        _itemChanged = itemChanged;
        _rootObservedExternally = rootObservedExternally;
        _incrementalCollectionNames = incrementalCollectionNames;
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
        var itemOnly = false;
        var changed = _modelTurn.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_propertyWatches.TryGetValue(owner, out var watch)) return false;
                itemOnly = watch.Nodes.Count == 1 && watch.Nodes.All(node =>
                    _incomingReferences.GetValueOrDefault(new(node.Owner, node.Members)) == 1);
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
                if (topologyMayHaveChanged) SweepOrDefer();
                return true;
            }
        });
        // ViewModelBridge observes the root itself; this graph owns only its
        // child subscriptions and must not capture the root notification twice.
        if (changed && (!_rootObservedExternally || !ReferenceEquals(owner, _root)) &&
            (!itemOnly || _itemChanged?.Invoke(owner) != true)) _changed();
    }

    private void OnCollectionChanged(INotifyCollectionChanged collection, NotifyCollectionChangedEventArgs args)
    {
        var collectionOnly = false;
        var changed = _modelTurn.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed || !_collectionWatches.TryGetValue(collection, out var watch)) return false;
                collectionOnly = watch.Edges.All(edge => ReferenceEquals(edge.Node.Owner, _root) &&
                    _incrementalCollectionNames?.Contains(edge.Member.Name) == true);
                foreach (var edge in watch.Edges.ToArray()) ReconcileCollection(edge, args);
                if (args.Action != NotifyCollectionChangedAction.Move) SweepOrDefer();
                return true;
            }
        });
        if (changed && (!collectionOnly || _collectionChanged?.Invoke(collection, args) != true)) _changed();
    }

    private void SweepOrDefer()
    {
        if (!BridgeSnapshotBatch.TryDefer(_root, this)) SweepDetached();
    }

    void IBridgeSnapshotBatchParticipant.FlushSnapshotBatch() => _modelTurn.Run(() =>
    {
        lock (_gate) if (!_disposed) SweepDetached();
    });

    private void OnErrorsChanged(INotifyDataErrorInfo source, DataErrorsChangedEventArgs args)
    {
        var changed = _modelTurn.Run(() =>
        {
            lock (_gate) return !_disposed && _errorWatches.ContainsKey(source);
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
                ClearItems(edge);
                AddItems(edge, edge.Member.EnumerateChildren!(edge.Value!));
                break;
            // A move does not alter membership, but callers still receive a
            // snapshot publication for the ordering change.
            case NotifyCollectionChangedAction.Move:
                break;
            default:
                ClearItems(edge);
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
                ClearItems(edge);
                AddItems(edge, snapshotEnumerate(next));
            }
            return;
        }
        DetachCollection(edge);
        if (edge.ValueChild is { } previousChild) RemoveReference(previousChild);
        edge.Value = next;
        edge.ValueChild = null;
        ClearItems(edge);
        if (next is null) return;

        if (edge.Member.EnumerateChildren is { } enumerate)
        {
            AttachCollection(edge, next as INotifyCollectionChanged);
            AddItems(edge, enumerate(next));
        }
        else if (edge.Member.Children.Count > 0)
        {
            edge.ValueChild = AddNode(next, edge.Member.Children);
            if (edge.ValueChild is { } child) AddReference(child);
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
            {
                edge.Items[nodeKey] = edge.Items.TryGetValue(nodeKey, out var count) ? count + 1 : 1;
                AddReference(nodeKey);
            }
        }
    }

    private void RemoveItems(Edge edge, IList? items)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is null) continue;
            var key = new NodeKey(item, edge.Member.Children);
            if (!edge.Items.TryGetValue(key, out var count)) continue;
            RemoveReference(key);
            if (count == 1) edge.Items.Remove(key);
            else edge.Items[key] = count - 1;
        }
    }

    private void AddReference(NodeKey key) => _incomingReferences[key] = _incomingReferences.GetValueOrDefault(key) + 1;

    private void RemoveReference(NodeKey key, int count = 1)
    {
        var remaining = _incomingReferences.GetValueOrDefault(key) - count;
        if (remaining > 0) _incomingReferences[key] = remaining;
        else _incomingReferences.Remove(key);
    }

    private void ClearItems(Edge edge)
    {
        foreach (var pair in edge.Items) RemoveReference(pair.Key, pair.Value);
        edge.Items.Clear();
    }

    private NodeKey? AddNode(object owner, IReadOnlyList<BridgeDataSubscriptionMember> members)
    {
        if (members.Count == 0 && owner is not INotifyPropertyChanged && owner is not INotifyDataErrorInfo) return null;
        // Boxing makes value-type identity unstable; value types cannot raise
        // useful nested notifications and are represented by their owner read.
        if (owner.GetType().IsValueType) return null;
        var key = new NodeKey(owner, members);
        if (_nodes.ContainsKey(key)) return key;

        var node = new Node(owner, members);
        _nodes.Add(key, node); // Reserve before children so recursive DTOs terminate.
        AttachProperty(node);
        // ViewModelBridge already observes the root INotifyDataErrorInfo.
        // Avoid emitting a second revision for root-level errors while still
        // watching every reachable nested (including errors-only) object.
        if (!ReferenceEquals(owner, _root)) AttachErrors(node);
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

    private void AttachErrors(Node node)
    {
        if (node.Owner is not INotifyDataErrorInfo source) return;
        if (!_errorWatches.TryGetValue(source, out var watch))
        {
            watch = new ErrorWatch(this, source);
            _errorWatches.Add(source, watch);
            source.ErrorsChanged += watch.Handler;
        }
        watch.Nodes.Add(node);
    }

    private void DetachErrors(Node node)
    {
        if (node.Owner is not INotifyDataErrorInfo source ||
            !_errorWatches.TryGetValue(source, out var watch)) return;
        watch.Nodes.Remove(node);
        if (watch.Nodes.Count != 0) return;
        source.ErrorsChanged -= watch.Handler;
        _errorWatches.Remove(source);
    }

    private void SweepDetached()
    {
        var reachable = new HashSet<NodeKey>(NodeKeyComparer.Instance);
        MarkReachable(_rootKey, reachable);
        foreach (var pair in _nodes.ToArray())
        {
            if (reachable.Contains(pair.Key)) continue;
            foreach (var edge in pair.Value.Edges)
            {
                DetachCollection(edge);
                if (edge.ValueChild is { } child) RemoveReference(child);
                ClearItems(edge);
            }
            DetachProperty(pair.Value);
            DetachErrors(pair.Value);
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
            foreach (var watch in _errorWatches.Values) watch.Source.ErrorsChanged -= watch.Handler;
            _propertyWatches.Clear();
            _collectionWatches.Clear();
            _errorWatches.Clear();
            _nodes.Clear();
            _incomingReferences.Clear();
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

    private sealed class ErrorWatch
    {
        internal ErrorWatch(BridgeDataSubscriptions owner, INotifyDataErrorInfo source)
        {
            Source = source;
            Handler = (_, args) => owner.OnErrorsChanged(source, args);
        }
        internal INotifyDataErrorInfo Source { get; }
        internal EventHandler<DataErrorsChangedEventArgs> Handler { get; }
        internal HashSet<Node> Nodes { get; } = new(ReferenceEqualityComparer.Instance);
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
