using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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
            RunPayloadIsolationAndOrdering();
            RunResultSurface();
            RunReentrancyAndExceptionSafety();
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

    private static void RunPayloadIsolationAndOrdering()
    {
        var collection = new ObservableRangeCollection<int>();
        var trace = new List<string>();
        IList? payload = null;
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, eventArgs) => trace.Add(eventArgs.PropertyName!);
        collection.CollectionChanged += (_, eventArgs) =>
        {
            trace.Add(eventArgs.Action.ToString());
            payload = eventArgs.NewItems;
        };

        int[] source = [10, 11];
        collection.AddRange(source);
        source[0] = 99;

        AssertSequence(trace, ["Count", "Item[]", "Add"], "notification ordering");
        IList capturedPayload = payload ??
            throw new InvalidOperationException("AddRange did not expose its copied notification payload.");
        Assert(capturedPayload.IsReadOnly, "AddRange notification payload was mutable.");
        AssertSequence(capturedPayload.Cast<int>(), [10, 11], "notification payload isolation");
        AssertThrows<NotSupportedException>(() => capturedPayload[0] = 42, "notification payload mutation");
    }

    private static void RunResultSurface()
    {
        var unchanged = new ObservableRangeCollection<int>([1, 2, 3]);
        int notifications = 0;
        unchanged.CollectionChanged += (_, _) => notifications++;

        CollectionUpdateResult noOp = unchanged.UpdateTo([1, 2, 3]);
        Assert(!noOp.Changed, "No-op UpdateTo reported a change.");
        Assert(noOp == default, "No-op UpdateTo returned nonzero counters.");
        Assert(notifications == 0, "No-op UpdateTo emitted a notification.");

        var result = new CollectionUpdateResult(
            Added: 1,
            Removed: 2,
            Moved: 3,
            Replaced: 4,
            NotificationCount: 5,
            UsedReset: true);
        Assert(
            result.Changed && result.Added == 1 && result.Removed == 2 && result.Moved == 3 &&
            result.Replaced == 4 && result.NotificationCount == 5 && result.UsedReset,
            "CollectionUpdateResult public surface was inconsistent.");
    }

    private static void RunReentrancyAndExceptionSafety()
    {
        var reentrant = new ObservableRangeCollection<int>();
        bool rejected = false;
        reentrant.CollectionChanged += (_, _) =>
        {
            try
            {
                reentrant.Add(99);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
        };

        reentrant.AddRange([1, 2]);
        Assert(rejected, "A notification callback was allowed to mutate reentrantly.");
        AssertSequence(reentrant, [1, 2], "reentrancy rejection content");

        var exceptionSafe = new ObservableRangeCollection<int>([7, 8]);
        AssertThrows<InvalidOperationException>(
            () => exceptionSafe.ReplaceRange(0, 1, ThrowingSequence()),
            "throwing source materialization");
        AssertSequence(exceptionSafe, [7, 8], "pre-mutation exception safety");

        var subscriberFailure = new ObservableRangeCollection<int>();
        subscriberFailure.CollectionChanged += (_, _) => throw new TestException();
        AssertThrows<TestException>(() => subscriberFailure.AddRange([3, 4]), "subscriber exception propagation");
        AssertSequence(subscriberFailure, [3, 4], "subscriber exception coherence");
    }

    private static IEnumerable<int> ThrowingSequence()
    {
        yield return 9;
        throw new InvalidOperationException("Expected smoke-test source failure.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string operation)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{operation} did not throw {typeof(TException).Name}.");
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

    private sealed class TestException : Exception;
}
