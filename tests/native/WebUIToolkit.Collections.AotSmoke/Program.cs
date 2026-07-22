using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using WebUIToolkit.Collections;

namespace WebUIToolkit.Collections.AotSmoke;

internal static class Program
{
    public static int Main()
    {
        try
        {
            RunRangeActions();
            RunSingleItemActions();
            RunResetPolicy();
            RunAutoUpdate();
            RunComparerUpdate();
            RunKeyedUpdate();
            Console.WriteLine("WebUIToolkit.Collections Native-AOT smoke: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WebUIToolkit.Collections Native-AOT smoke: FAIL: {exception.Message}");
            return 1;
        }
    }

    private static void RunSingleItemActions()
    {
        var collection = new ObservableRangeCollection<int>([1]);
        collection.Add(2);
        collection.Insert(0, 0);
        collection[1] = 5;
        collection.Move(0, 1);
        Assert(collection.Remove(2), "Single-item Remove did not find the expected value.");
        collection.RemoveAt(1);
        AssertSequence(collection, [5], "inherited single-item actions");
    }

    private static void RunRangeActions()
    {
        var collection = new ObservableRangeCollection<int>();
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, eventArgs) => actions.Add(eventArgs.Action);

        collection.AddRange([1, 2, 3]);
        AssertSequence(collection, [1, 2, 3], "AddRange");
        collection.InsertRange(1, [8, 9]);
        AssertSequence(collection, [1, 8, 9, 2, 3], "InsertRange");
        collection.RemoveRange(1, 2);
        AssertSequence(collection, [1, 2, 3], "RemoveRange");
        collection.ReplaceRange(1, 1, [4]);
        AssertSequence(collection, [1, 4, 3], "equal ReplaceRange");
        collection.ReplaceRange(1, 1, [5, 6]);
        AssertSequence(collection, [1, 5, 6, 3], "unequal ReplaceRange");
        collection.MoveRange(1, 2, 2);
        AssertSequence(collection, [1, 3, 5, 6], "MoveRange");

        int[] snapshot = collection.ToSnapshot();
        collection.Clear();
        AssertSequence(collection, [], "Clear");
        AssertSequence(snapshot, [1, 3, 5, 6], "ToSnapshot isolation");

        AssertSequence(
            actions,
            [
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Add,
                NotifyCollectionChangedAction.Remove,
                NotifyCollectionChangedAction.Replace,
                NotifyCollectionChangedAction.Reset,
                NotifyCollectionChangedAction.Move,
                NotifyCollectionChangedAction.Reset,
            ],
            "range event actions");
    }

    private static void RunResetPolicy()
    {
        var collection = new ObservableRangeCollection<int>(
            new ObservableRangeCollectionOptions { RangeNotifications = RangeNotificationMode.Reset });
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, eventArgs) => actions.Add(eventArgs.Action);

        collection.AddRange([1, 2]);
        collection.ReplaceRange(0, 2, [3, 4]);
        collection.MoveRange(0, 1, 1);

        AssertSequence(collection, [4, 3], "Reset range policy content");
        AssertSequence(
            actions,
            [NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Reset, NotifyCollectionChangedAction.Move],
            "Reset range policy actions");
    }

    private static void RunAutoUpdate()
    {
        var collection = new ObservableRangeCollection<int>([1, 2]);
        CollectionUpdateResult result = collection.UpdateTo([2, 1, 3]);
        AssertSequence(collection, [2, 1, 3], "automatic UpdateTo");
        Assert(result.Changed && !result.UsedReset, "Automatic UpdateTo unexpectedly selected Reset.");
    }

    private static void RunComparerUpdate()
    {
        var firstA = new Item(1, "old-a");
        var secondA = new Item(1, "old-b");
        var b = new Item(2, "old-c");
        var collection = new ObservableRangeCollection<Item>([firstA, secondA, b]);
        int events = 0;
        collection.CollectionChanged += (_, _) => events++;

        CollectionUpdateResult result = collection.UpdateTo(
            [new Item(1, "incoming-a"), new Item(2, "incoming-b"), new Item(1, "incoming-c")],
            new KeyComparer(),
            resolveMatch: static (existing, _) => existing,
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });

        Assert(ReferenceEquals(firstA, collection[0]), "Comparer UpdateTo did not retain the first FIFO duplicate.");
        Assert(ReferenceEquals(b, collection[1]), "Comparer UpdateTo did not retain the moved identity.");
        Assert(ReferenceEquals(secondA, collection[2]), "Comparer UpdateTo did not retain the second FIFO duplicate.");
        Assert(result.Changed && !result.UsedReset && events == result.NotificationCount, "Comparer UpdateTo result was inconsistent.");
    }

    private static void RunKeyedUpdate()
    {
        var one = new Item(1, "old-one");
        var two = new Item(2, "old-two");
        var collection = new ObservableRangeCollection<Item>([one, two]);
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, eventArgs) => actions.Add(eventArgs.Action);

        CollectionUpdateResult result = collection.UpdateTo(
            [new Item(2, "incoming-two"), new Item(3, "incoming-three"), new Item(1, "incoming-one")],
            static item => item.Key,
            resolveMatch: static (existing, _) => existing,
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Reset });

        Assert(ReferenceEquals(two, collection[0]), "Keyed UpdateTo did not retain key 2.");
        Assert(collection[1].Key == 3, "Keyed UpdateTo did not add key 3.");
        Assert(ReferenceEquals(one, collection[2]), "Keyed UpdateTo did not retain key 1.");
        Assert(result.Changed && result.UsedReset && result.NotificationCount == 1, "Keyed reset result was inconsistent.");
        AssertSequence(actions, [NotifyCollectionChangedAction.Reset], "Keyed reset action");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertSequence<T>(IEnumerable<T> actual, IEnumerable<T> expected, string operation)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{operation} produced an unexpected sequence.");
        }
    }

    private sealed class Item(int key, string value)
    {
        public int Key { get; } = key;

        public string Value { get; } = value;
    }

    private sealed class KeyComparer : IEqualityComparer<Item>
    {
        public bool Equals(Item? x, Item? y) => x?.Key == y?.Key;

        public int GetHashCode(Item obj) => obj.Key;
    }
}
