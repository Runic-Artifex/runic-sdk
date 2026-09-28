using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class CheckedDataTests
{
    internal static void Run()
    {
        RetainedCheckedValuesAreImmutableBaselines();
        NestedDataSubscriptionsReleaseRemovedObjects();
        ScalarUpdatesDoNotChurnUnrelatedSubscriptions();
        OneShotCollectionsAreNotReenumeratedForUnrelatedChanges();
        PlainCollectionsReconcileWhenTheirOwnerNotifies();
        NestedCollectionsReconcileAtEveryLevel();
    }

    private static void RetainedCheckedValuesAreImmutableBaselines()
    {
        var owner = new MutableOwner { Value = new MutableValue(1) };
        using var registry = new BridgeFieldWriteRegistry<MutableValue>(
            "owner", "value", () => owner.Value, value => owner.Value = value,
            snapshot: value => new MutableValue(value.Number),
            equalityComparer: MutableValueComparer.Instance);

        var initial = registry.Current;
        initial.Value.Number = 100;
        Require(registry.Current.Value.Number == 1,
            "A caller mutated the registry's retained checked-write baseline.");

        var write = registry.Apply(new("write-1", initial.Version, new MutableValue(1), new MutableValue(2)));
        Require(write.Kind == BridgeFieldWriteReceiptKind.Applied && write.Current.Value.Number == 2,
            "A structural checked write did not apply.");
        write.Current.Value.Number = 200;
        Require(registry.Apply(new("write-1", 0, new MutableValue(0), new MutableValue(0))).Current.Value.Number == 2,
            "A retry observed a mutable retained receipt.");

        owner.Value.Number = 3;
        var conflict = registry.Apply(new("write-2", 1, new MutableValue(2), new MutableValue(4)));
        Require(conflict.Kind == BridgeFieldWriteReceiptKind.Conflict && conflict.Current.Value.Number == 3,
            "A mutation through a live DTO reference was not detected as an external change.");
    }

    private static void NestedDataSubscriptionsReleaseRemovedObjects()
    {
        var oldChild = new MutableNode();
        var root = new SubscriptionRoot { Child = oldChild };
        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("value", static owner => ((MutableNode)owner).Value),
            new BridgeDataSubscriptionMember("next", static owner => ((MutableNode)owner).Next,
                children: [])
        };
        // Add the recursive edge after constructing the shared generated shape.
        childMembers[1] = new BridgeDataSubscriptionMember("next", static owner => ((MutableNode)owner).Next,
            children: childMembers);
        var rootMembers = new[]
        {
            new BridgeDataSubscriptionMember("child", static owner => ((SubscriptionRoot)owner).Child, childMembers),
            new BridgeDataSubscriptionMember("items", static owner => ((SubscriptionRoot)owner).Items,
                childMembers, static value => (IEnumerable)value)
        };
        var notifications = 0;
        using var subscriptions = new BridgeDataSubscriptions(root, rootMembers, () => notifications++);

        oldChild.Value = 1;
        Require(notifications == 1, "A nested DTO property change was not observed.");

        var replacement = new MutableNode();
        root.Child = replacement;
        oldChild.Value = 2;
        Require(notifications == 2, "A replaced DTO remained subscribed.");
        replacement.Value = 3;
        Require(notifications == 3, "A replacement DTO was not subscribed.");

        var retained = new MutableNode();
        var removed = new MutableNode();
        root.Items.Add(retained);
        root.Items.Add(removed);
        Require(notifications == 5, "Collection additions did not rebuild nested subscriptions.");
        root.Items.Remove(removed);
        Require(notifications == 6, "Collection removal was not observed.");
        removed.Value = 4;
        Require(notifications == 6, "A removed collection item remained subscribed.");
        retained.Value = 5;
        Require(notifications == 7, "A retained collection item was not subscribed.");

        // A generated recursive shape must not recurse forever when the graph
        // contains a model cycle.
        retained.Next = retained;
        Require(notifications == 8, "A cyclic DTO graph did not complete its rebuild.");

        // The same object can occur more than once. Removing one occurrence
        // must retain the remaining path, while removing the final occurrence
        // must release its subscription.
        var shared = new MutableNode();
        root.Items.Add(shared);
        root.Items.Add(shared);
        root.Items.Remove(shared);
        shared.Value = 6;
        Require(notifications == 12, "A shared collection item was released after only one occurrence was removed.");
        root.Items.Remove(shared);
        shared.Value = 7;
        Require(notifications == 13, "A removed shared collection item remained subscribed.");

        var replaced = new MutableNode();
        root.Items[0] = replaced;
        retained.Value = 8;
        Require(notifications == 14, "A replaced collection item remained subscribed.");
        replaced.Value = 9;
        Require(notifications == 15, "A replacement collection item was not subscribed.");
    }

    private static void ScalarUpdatesDoNotChurnUnrelatedSubscriptions()
    {
        var left = new CountingNode();
        var right = new CountingNode();
        var root = new ChurnRoot { Left = left, Right = right };
        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("value", static owner => ((CountingNode)owner).Value)
        };
        var members = new[]
        {
            new BridgeDataSubscriptionMember("left", static owner => ((ChurnRoot)owner).Left, childMembers),
            new BridgeDataSubscriptionMember("right", static owner => ((ChurnRoot)owner).Right, childMembers)
        };
        var notifications = 0;
        using var subscriptions = new BridgeDataSubscriptions(root, members, () => notifications++);

        left.Value = 1;
        Require(notifications == 1, "A scalar leaf update did not publish.");
        Require(left.Adds == 1 && left.Removes == 0 && right.Adds == 1 && right.Removes == 0,
            "A scalar leaf update rebuilt subscriptions for retained graph branches.");
    }

    private static void OneShotCollectionsAreNotReenumeratedForUnrelatedChanges()
    {
        var root = new EnumerableRoot { Sequence = new OneShotEnumerable([new MutableNode()]) };
        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("value", static owner => ((MutableNode)owner).Value)
        };
        var members = new[]
        {
            new BridgeDataSubscriptionMember("sequence", static owner => ((EnumerableRoot)owner).Sequence,
                childMembers, static value => (IEnumerable)value),
            new BridgeDataSubscriptionMember("tick", static owner => ((EnumerableRoot)owner).Tick)
        };
        var notifications = 0;
        using var subscriptions = new BridgeDataSubscriptions(root, members, () => notifications++);

        Require(root.Sequence.EnumerationCount == 1, "The initial collection graph was not enumerated once.");
        root.Tick = 1;
        Require(notifications == 1 && root.Sequence.EnumerationCount == 1,
            "An unrelated scalar update re-enumerated a retained one-shot collection.");
    }

    private static void PlainCollectionsReconcileWhenTheirOwnerNotifies()
    {
        var removed = new MutableNode();
        var root = new PlainListRoot();
        root.Items.Add(removed);
        var childMembers = new[]
        {
            new BridgeDataSubscriptionMember("value", static owner => ((MutableNode)owner).Value)
        };
        var members = new[]
        {
            new BridgeDataSubscriptionMember("items", static owner => ((PlainListRoot)owner).Items,
                childMembers, static value => (IEnumerable)value)
        };
        var notifications = 0;
        using var subscriptions = new BridgeDataSubscriptions(root, members, () => notifications++);

        var added = new MutableNode();
        root.Items.Add(added);
        root.NotifyItemsChanged();
        added.Value = 1;
        Require(notifications == 2, "A notified plain List addition did not attach its nested DTO.");
        root.Items.Remove(removed);
        root.NotifyItemsChanged();
        removed.Value = 2;
            Require(notifications == 3, "A notified plain List removal retained its nested DTO subscription.");
    }

    private static void NestedCollectionsReconcileAtEveryLevel()
    {
        var old = new MutableNode();
        var inner = new ObservableCollection<MutableNode> { old };
        var root = new NestedCollectionRoot();
        root.Groups.Add(inner);
        var nodeMembers = new[]
        {
            new BridgeDataSubscriptionMember("value", static owner => ((MutableNode)owner).Value)
        };
        var innerMembers = new[]
        {
            new BridgeDataSubscriptionMember("$items", static owner => owner, nodeMembers,
                static value => (IEnumerable)value)
        };
        var rootMembers = new[]
        {
            new BridgeDataSubscriptionMember("groups", static owner => ((NestedCollectionRoot)owner).Groups,
                innerMembers, static value => (IEnumerable)value)
        };
        var notifications = 0;
        using var subscriptions = new BridgeDataSubscriptions(root, rootMembers, () => notifications++);

        old.Value = 1;
        var added = new MutableNode();
        inner.Add(added);
        added.Value = 2;
        inner.Remove(old);
        old.Value = 3;
        Require(notifications == 4, "Nested collection updates did not reconcile the inner DTO graph.");
        root.Groups.Remove(inner);
        added.Value = 4;
        Require(notifications == 5, "Removing an inner collection retained its descendants.");

        var scalarInner = new ObservableCollection<int> { 1 };
        var scalarRoot = new NestedScalarCollectionRoot();
        scalarRoot.Groups.Add(scalarInner);
        var scalarInnerMembers = new[]
        {
            new BridgeDataSubscriptionMember("$items", static owner => owner, [], static value => (IEnumerable)value)
        };
        var scalarRootMembers = new[]
        {
            new BridgeDataSubscriptionMember("groups", static owner => ((NestedScalarCollectionRoot)owner).Groups,
                scalarInnerMembers, static value => (IEnumerable)value)
        };
        var scalarNotifications = 0;
        using var scalarSubscriptions = new BridgeDataSubscriptions(scalarRoot, scalarRootMembers,
            () => scalarNotifications++);
        scalarInner.Add(2);
        Require(scalarNotifications == 1,
            "An inner scalar ObservableCollection mutation did not publish its outer data snapshot.");
    }

    private sealed class MutableOwner
    {
        internal MutableValue Value { get; set; } = null!;
    }

    private sealed class MutableValue(int number)
    {
        internal int Number { get; set; } = number;
    }

    private sealed class MutableValueComparer : IEqualityComparer<MutableValue>
    {
        internal static MutableValueComparer Instance { get; } = new();
        public bool Equals(MutableValue? left, MutableValue? right) => left?.Number == right?.Number;
        public int GetHashCode(MutableValue value) => value.Number;
    }

    private sealed class SubscriptionRoot : NotifyBase
    {
        private MutableNode? _child;
        internal MutableNode? Child
        {
            get => _child;
            set => Set(ref _child, value);
        }

        internal ObservableCollection<MutableNode> Items { get; } = [];
    }

    private sealed class ChurnRoot : NotifyBase
    {
        private CountingNode? _left;
        private CountingNode? _right;
        internal CountingNode? Left { get => _left; set => Set(ref _left, value); }
        internal CountingNode? Right { get => _right; set => Set(ref _right, value); }
    }

    private sealed class EnumerableRoot : NotifyBase
    {
        private int _tick;
        internal OneShotEnumerable Sequence { get; set; } = null!;
        internal int Tick { get => _tick; set => Set(ref _tick, value); }
    }

    private sealed class PlainListRoot : NotifyBase
    {
        internal List<MutableNode> Items { get; } = [];
        internal void NotifyItemsChanged() => Raise(nameof(Items));
    }

    private sealed class NestedCollectionRoot : NotifyBase
    {
        internal ObservableCollection<ObservableCollection<MutableNode>> Groups { get; } = [];
    }

    private sealed class NestedScalarCollectionRoot : NotifyBase
    {
        internal ObservableCollection<ObservableCollection<int>> Groups { get; } = [];
    }

    private sealed class MutableNode : NotifyBase
    {
        private int _value;
        private MutableNode? _next;

        internal int Value
        {
            get => _value;
            set => Set(ref _value, value);
        }

        internal MutableNode? Next
        {
            get => _next;
            set => Set(ref _next, value);
        }
    }

    private sealed class CountingNode : INotifyPropertyChanged
    {
        private PropertyChangedEventHandler? _propertyChanged;
        private int _value;
        internal int Adds { get; private set; }
        internal int Removes { get; private set; }
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { Adds++; _propertyChanged += value; }
            remove { Removes++; _propertyChanged -= value; }
        }
        internal int Value
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                _propertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
            }
        }
    }

    private sealed class OneShotEnumerable(IEnumerable<MutableNode> values) : IEnumerable
    {
        private readonly IEnumerable<MutableNode> _values = values;
        internal int EnumerationCount { get; private set; }
        public IEnumerator GetEnumerator()
        {
            if (++EnumerationCount != 1)
                throw new InvalidOperationException("The one-shot sequence was enumerated more than once.");
            return _values.GetEnumerator();
        }
    }

    private abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
