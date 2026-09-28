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

    private abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
