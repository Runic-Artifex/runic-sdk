using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace WebUIToolkit.Collections.Tests;

internal static class RangeMutationTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("OR0 contract traces / Range mode", RangeModeTraces),
        new("OR0 contract traces / Reset mode", ResetModeTraces),
        new("OR0 contract traces / single-item range calls retain BCL events", SingleItemRangeCalls),
        new("OR0 contract traces / inherited single-item behavior", InheritedSingleItemTraces),
        new("OR0 contract traces / copied read-only payloads", PayloadsAreCopiedAndReadOnly),
        new("OR0 contract traces / post-event state and replay", HandlerStateAndReplay),
        new("OR1 range kernel / no-op and snapshot semantics", NoOpsAndSnapshots),
        new("OR1 range kernel / self-source materialization", SelfSourceAndSingleEnumeration),
        new("OR1 range kernel / nullable items", NullableItems),
        new("OR1 range kernel / invalid and overflow matrix", InvalidArguments),
        new("OR1 range kernel / move uses final index", MoveFinalIndexSemantics),
    ];

    private static void RangeModeTraces()
    {
        AssertTrace(
            [1, 2],
            static collection => collection.AddRange([3, 4]),
            "P:Count:[1,2,3,4]",
            "P:Item[]:[1,2,3,4]",
            "C:Add:old=-@-1:new=[3,4]@2:[1,2,3,4]");

        AssertTrace(
            [1, 4],
            static collection => collection.InsertRange(1, [2, 3]),
            "P:Count:[1,2,3,4]",
            "P:Item[]:[1,2,3,4]",
            "C:Add:old=-@-1:new=[2,3]@1:[1,2,3,4]");

        AssertTrace(
            [0, 1, 2, 3, 4],
            static collection => collection.RemoveRange(1, 2),
            "P:Count:[0,3,4]",
            "P:Item[]:[0,3,4]",
            "C:Remove:old=[1,2]@1:new=-@-1:[0,3,4]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.ReplaceRange(1, 2, [8, 9]),
            "P:Item[]:[0,8,9,3]",
            "C:Replace:old=[1,2]@1:new=[8,9]@1:[0,8,9,3]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.ReplaceRange(1, 2, [7, 8, 9]),
            "P:Count:[0,7,8,9,3]",
            "P:Item[]:[0,7,8,9,3]",
            "C:Reset:old=-@-1:new=-@-1:[0,7,8,9,3]");

        AssertTrace(
            [0, 3],
            static collection => collection.ReplaceRange(1, 0, [1, 2]),
            "P:Count:[0,1,2,3]",
            "P:Item[]:[0,1,2,3]",
            "C:Add:old=-@-1:new=[1,2]@1:[0,1,2,3]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.ReplaceRange(1, 2, []),
            "P:Count:[0,3]",
            "P:Item[]:[0,3]",
            "C:Remove:old=[1,2]@1:new=-@-1:[0,3]");

        AssertTrace(
            [0, 1, 2, 3, 4],
            static collection => collection.MoveRange(1, 2, 3),
            "P:Item[]:[0,3,4,1,2]",
            "C:Move:old=[1,2]@1:new=[1,2]@3:[0,3,4,1,2]");
    }

    private static void ResetModeTraces()
    {
        var reset = new ObservableRangeCollectionOptions { RangeNotifications = RangeNotificationMode.Reset };

        AssertTrace(
            [1, 2],
            static collection => collection.AddRange([3, 4]),
            reset,
            "P:Count:[1,2,3,4]",
            "P:Item[]:[1,2,3,4]",
            "C:Reset:old=-@-1:new=-@-1:[1,2,3,4]");

        AssertTrace(
            [1, 4],
            static collection => collection.InsertRange(1, [2, 3]),
            reset,
            "P:Count:[1,2,3,4]",
            "P:Item[]:[1,2,3,4]",
            "C:Reset:old=-@-1:new=-@-1:[1,2,3,4]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.RemoveRange(1, 2),
            reset,
            "P:Count:[0,3]",
            "P:Item[]:[0,3]",
            "C:Reset:old=-@-1:new=-@-1:[0,3]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.ReplaceRange(1, 2, [8, 9]),
            reset,
            "P:Item[]:[0,8,9,3]",
            "C:Reset:old=-@-1:new=-@-1:[0,8,9,3]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.ReplaceRange(1, 2, [7, 8, 9]),
            reset,
            "P:Count:[0,7,8,9,3]",
            "P:Item[]:[0,7,8,9,3]",
            "C:Reset:old=-@-1:new=-@-1:[0,7,8,9,3]");

        AssertTrace(
            [0, 3],
            static collection => collection.ReplaceRange(1, 0, [1, 2]),
            reset,
            "P:Count:[0,1,2,3]",
            "P:Item[]:[0,1,2,3]",
            "C:Reset:old=-@-1:new=-@-1:[0,1,2,3]");

        AssertTrace(
            [0, 1, 2, 3],
            static collection => collection.ReplaceRange(1, 2, []),
            reset,
            "P:Count:[0,3]",
            "P:Item[]:[0,3]",
            "C:Reset:old=-@-1:new=-@-1:[0,3]");

        AssertTrace(
            [0, 1, 2, 3, 4],
            static collection => collection.MoveRange(1, 2, 3),
            reset,
            "P:Item[]:[0,3,4,1,2]",
            "C:Reset:old=-@-1:new=-@-1:[0,3,4,1,2]");
    }

    private static void SingleItemRangeCalls()
    {
        var reset = new ObservableRangeCollectionOptions { RangeNotifications = RangeNotificationMode.Reset };
        AssertTrace(
            [1],
            static collection => collection.AddRange([2]),
            reset,
            "P:Count:[1,2]",
            "P:Item[]:[1,2]",
            "C:Add:old=-@-1:new=[2]@1:[1,2]");
        AssertTrace(
            [1, 3],
            static collection => collection.InsertRange(1, [2]),
            reset,
            "P:Count:[1,2,3]",
            "P:Item[]:[1,2,3]",
            "C:Add:old=-@-1:new=[2]@1:[1,2,3]");
        AssertTrace(
            [1, 2],
            static collection => collection.RemoveRange(0, 1),
            reset,
            "P:Count:[2]",
            "P:Item[]:[2]",
            "C:Remove:old=[1]@0:new=-@-1:[2]");
        AssertTrace(
            [1, 2],
            static collection => collection.ReplaceRange(0, 1, [9]),
            reset,
            "P:Item[]:[9,2]",
            "C:Replace:old=[1]@0:new=[9]@0:[9,2]");
        AssertTrace(
            [1, 2, 3],
            static collection => collection.MoveRange(0, 1, 2),
            reset,
            "P:Item[]:[2,3,1]",
            "C:Move:old=[1]@0:new=[1]@2:[2,3,1]");
    }

    private static void InheritedSingleItemTraces()
    {
        var reset = new ObservableRangeCollectionOptions { RangeNotifications = RangeNotificationMode.Reset };
        AssertTrace(
            [1],
            static collection => collection.Add(2),
            reset,
            "P:Count:[1,2]",
            "P:Item[]:[1,2]",
            "C:Add:old=-@-1:new=[2]@1:[1,2]");
        AssertTrace(
            [1, 3],
            static collection => collection.Insert(1, 2),
            reset,
            "P:Count:[1,2,3]",
            "P:Item[]:[1,2,3]",
            "C:Add:old=-@-1:new=[2]@1:[1,2,3]");
        AssertTrace(
            [1, 2],
            static collection => collection.RemoveAt(0),
            reset,
            "P:Count:[2]",
            "P:Item[]:[2]",
            "C:Remove:old=[1]@0:new=-@-1:[2]");
        AssertTrace(
            [1, 2],
            static collection => collection[0] = 9,
            reset,
            "P:Item[]:[9,2]",
            "C:Replace:old=[1]@0:new=[9]@0:[9,2]");
        AssertTrace(
            [1, 2, 3],
            static collection => collection.Move(0, 2),
            reset,
            "P:Item[]:[2,3,1]",
            "C:Move:old=[1]@0:new=[1]@2:[2,3,1]");
        AssertTrace(
            [1, 2],
            static collection => collection.Clear(),
            reset,
            "P:Count:[]",
            "P:Item[]:[]",
            "C:Reset:old=-@-1:new=-@-1:[]");
    }

    private static void PayloadsAreCopiedAndReadOnly()
    {
        var source = new List<int> { 3, 4 };
        var collection = new ObservableRangeCollection<int>([1, 2]);
        using var trace = new TraceRecorder<int>(collection);

        collection.AddRange(source);
        var args = trace.Events.Single();
        source[0] = 99;
        source.Add(100);
        collection[2] = 7;

        Assert.SequenceEqual([3, 4], args.NewItems!.Cast<int>());
        Assert.True(args.NewItems!.IsReadOnly, "Range event payload must be read-only.");
        Assert.Throws<NotSupportedException>(() => args.NewItems!.Add(5));

        var replacement = new List<int> { 8, 9 };
        collection.ReplaceRange(0, 2, replacement);
        var replace = trace.Events[^1];
        replacement.Clear();
        collection[0] = 10;
        Assert.SequenceEqual([8, 9], replace.NewItems!.Cast<int>());
        Assert.SequenceEqual([1, 2], replace.OldItems!.Cast<int>());
        Assert.True(replace.NewItems!.IsReadOnly && replace.OldItems!.IsReadOnly);

        var removedCollection = new ObservableRangeCollection<int>([0, 1, 2, 3]);
        using var removedTrace = new TraceRecorder<int>(removedCollection);
        removedCollection.RemoveRange(1, 2);
        var removed = removedTrace.Events.Single();
        removedCollection.AddRange([8, 9]);
        Assert.SequenceEqual([1, 2], removed.OldItems!.Cast<int>());
        Assert.True(removed.OldItems!.IsReadOnly);

        var movedCollection = new ObservableRangeCollection<int>([0, 1, 2, 3]);
        using var movedTrace = new TraceRecorder<int>(movedCollection);
        movedCollection.MoveRange(1, 2, 0);
        var moved = movedTrace.Events.Single();
        movedCollection[0] = 9;
        Assert.SequenceEqual([1, 2], moved.NewItems!.Cast<int>());
        Assert.True(moved.NewItems!.IsReadOnly);
    }

    private static void HandlerStateAndReplay()
    {
        var collection = new ObservableRangeCollection<int>([0, 1, 2, 3]);
        var shadow = collection.ToList();
        collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);

        collection.InsertRange(2, [8, 9]);
        Assert.SequenceEqual(collection, shadow);
        collection.RemoveRange(1, 2);
        Assert.SequenceEqual(collection, shadow);
        collection.ReplaceRange(1, 2, [5, 6]);
        Assert.SequenceEqual(collection, shadow);
        collection.MoveRange(0, 2, 2);
        Assert.SequenceEqual(collection, shadow);
        collection.ReplaceRange(0, 1, [7, 8]);
        Assert.SequenceEqual(collection, shadow);
        collection.Clear();
        Assert.SequenceEqual(collection, shadow);
    }

    private static void NoOpsAndSnapshots()
    {
        var collection = new ObservableRangeCollection<int>([1, 2, 3]);
        using var trace = new TraceRecorder<int>(collection);
        var snapshot = collection.ToSnapshot();

        collection.AddRange([]);
        collection.InsertRange(1, []);
        collection.RemoveRange(1, 0);
        collection.ReplaceRange(1, 0, []);
        collection.MoveRange(1, 0, 1);
        collection.MoveRange(1, 1, 1);

        Assert.Equal(0, trace.Entries.Count);
        collection.Add(4);
        Assert.SequenceEqual([1, 2, 3], snapshot);
        Assert.False(ReferenceEquals(snapshot, collection.ToSnapshot()));

        var empty = new ObservableRangeCollection<int>();
        using var emptyTrace = new TraceRecorder<int>(empty);
        empty.Clear();
        Assert.Equal(0, emptyTrace.Entries.Count);
    }

    private static void SelfSourceAndSingleEnumeration()
    {
        var collection = new ObservableRangeCollection<int>([1, 2, 3]);
        collection.AddRange(collection);
        Assert.SequenceEqual([1, 2, 3, 1, 2, 3], collection);

        var inserted = new ObservableRangeCollection<int>([1, 2, 3]);
        inserted.InsertRange(1, inserted);
        Assert.SequenceEqual([1, 1, 2, 3, 2, 3], inserted);

        var counted = new CountingEnumerable<int>([4, 5]);
        inserted.ReplaceRange(0, 1, counted);
        Assert.Equal(1, counted.EnumerationCount);
        Assert.SequenceEqual([4, 5, 1, 2, 3, 2, 3], inserted);

        var constructorSource = new CountingEnumerable<int>([1, 2]);
        _ = new ObservableRangeCollection<int>(constructorSource);
        Assert.Equal(1, constructorSource.EnumerationCount);

        var addSource = new CountingEnumerable<int>([3, 4]);
        var addTarget = new ObservableRangeCollection<int>([1, 2]);
        addTarget.AddRange(addSource);
        Assert.Equal(1, addSource.EnumerationCount);

        var insertSource = new CountingEnumerable<int>([3, 4]);
        var insertTarget = new ObservableRangeCollection<int>([1, 2]);
        insertTarget.InsertRange(1, insertSource);
        Assert.Equal(1, insertSource.EnumerationCount);

        var updateSource = new CountingEnumerable<int>([3, 2, 1]);
        var updateTarget = new ObservableRangeCollection<int>([1, 2, 3]);
        updateTarget.UpdateTo(updateSource);
        Assert.Equal(1, updateSource.EnumerationCount);

        var replaced = new ObservableRangeCollection<int>([1, 2, 3]);
        replaced.ReplaceRange(1, 1, replaced);
        Assert.SequenceEqual([1, 1, 2, 3, 3], replaced);

        var updated = new ObservableRangeCollection<int>([1, 2, 3]);
        var result = updated.UpdateTo(updated);
        Assert.False(result.Changed);
        Assert.SequenceEqual([1, 2, 3], updated);
    }

    private static void NullableItems()
    {
        var collection = new ObservableRangeCollection<string?>(["a"]);
        collection.AddRange([null, "b"]);
        Assert.SequenceEqual<string?>(["a", null, "b"], collection);
        collection.ReplaceRange(0, 2, [null, null]);
        Assert.SequenceEqual<string?>([null, null, "b"], collection);
        collection.RemoveRange(0, 2);
        Assert.SequenceEqual<string?>(["b"], collection);
    }

    private static void InvalidArguments()
    {
        var collection = new ObservableRangeCollection<int>([1, 2, 3]);
        var original = collection.ToSnapshot();

        Assert.Throws<ArgumentNullException>(() => collection.AddRange(null!));
        Assert.Throws<ArgumentNullException>(() => collection.InsertRange(0, null!));
        Assert.Throws<ArgumentNullException>(() => collection.ReplaceRange(0, 0, null!));

        foreach (var index in new[] { -1, 4, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => collection.InsertRange(index, [9]));
        }

        foreach (var pair in new[]
                 {
                     (-1, 0), (0, -1), (3, 1), (4, 0),
                     (1, int.MaxValue), (int.MaxValue, 1),
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => collection.RemoveRange(pair.Item1, pair.Item2));
            Assert.Throws<ArgumentOutOfRangeException>(() => collection.ReplaceRange(pair.Item1, pair.Item2, [9]));
        }

        foreach (var move in new[]
                 {
                     (-1, 1, 0), (0, -1, 0), (2, 2, 0),
                     (1, int.MaxValue, 0), (int.MaxValue, 1, 0),
                     (0, 1, -1), (0, 1, 3),
                 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => collection.MoveRange(move.Item1, move.Item2, move.Item3));
        }

        Assert.SequenceEqual(original, collection);
    }

    private static void MoveFinalIndexSemantics()
    {
        var right = new ObservableRangeCollection<int>([0, 1, 2, 3, 4, 5]);
        right.MoveRange(1, 2, 4);
        Assert.SequenceEqual([0, 3, 4, 5, 1, 2], right);

        var left = new ObservableRangeCollection<int>([0, 1, 2, 3, 4, 5]);
        left.MoveRange(3, 2, 1);
        Assert.SequenceEqual([0, 3, 4, 1, 2, 5], left);

        var overlap = new ObservableRangeCollection<int>([0, 1, 2, 3, 4]);
        overlap.MoveRange(1, 2, 2);
        Assert.SequenceEqual([0, 3, 1, 2, 4], overlap);
    }

    private static void AssertTrace(
        IEnumerable<int> items,
        Action<ObservableRangeCollection<int>> action,
        params string[] expected) =>
        AssertTrace(items, action, new ObservableRangeCollectionOptions(), expected);

    private static void AssertTrace(
        IEnumerable<int> items,
        Action<ObservableRangeCollection<int>> action,
        ObservableRangeCollectionOptions options,
        params string[] expected)
    {
        var collection = new ObservableRangeCollection<int>(items, options);
        using var trace = new TraceRecorder<int>(collection);
        action(collection);
        Assert.SequenceEqual(expected, trace.Entries);
    }

    private sealed class CountingEnumerable<T>(IEnumerable<T> values) : IEnumerable<T>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return values.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
