using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace RunicToolkit.Collections.Tests;

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
        new("Wave B range model / exhaustive valid boundary matrix", ExhaustiveBoundaryModel),
        new("Wave B range model / clear and snapshot matrix", ClearAndSnapshotMatrix),
        new("Wave B range model / copied payload isolation matrix", PayloadIsolationMatrix),
        new("Wave B range model / invalid operations are silent and atomic", InvalidOperationsAreSilentAndAtomic),
        new("Wave B range model / nullable duplicate and self-source matrix", NullableDuplicateAndSelfSourceMatrix),
        new("Wave B range model / property and collection event cardinality", EventCardinalityMatrix),
        new("Wave B range model / subscriber-list mutation semantics", SubscriberListMutationSemantics),
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

    private static void ExhaustiveBoundaryModel()
    {
        foreach (RangeNotificationMode mode in Enum.GetValues<RangeNotificationMode>())
        {
            var options = new ObservableRangeCollectionOptions { RangeNotifications = mode };
            for (var size = 0; size <= 5; size++)
            {
                int[] seed = Enumerable.Range(0, size).ToArray();

                for (var rangeSize = 0; rangeSize <= 3; rangeSize++)
                {
                    int[] added = Enumerable.Range(100, rangeSize).ToArray();
                    AssertModeledMutation(
                        seed,
                        options,
                        collection => collection.AddRange(added),
                        [.. seed, .. added],
                        countChanged: rangeSize != 0,
                        expectedAction: AdditionAction(mode, rangeSize));

                    for (var index = 0; index <= size; index++)
                    {
                        int insertionIndex = index;
                        AssertModeledMutation(
                            seed,
                            options,
                            collection => collection.InsertRange(insertionIndex, added),
                            [.. seed.Take(insertionIndex), .. added, .. seed.Skip(insertionIndex)],
                            countChanged: rangeSize != 0,
                            expectedAction: AdditionAction(mode, rangeSize));
                    }
                }

                for (var index = 0; index <= size; index++)
                {
                    for (var count = 0; count <= size - index; count++)
                    {
                        int rangeIndex = index;
                        int rangeCount = count;
                        int[] removedModel = [.. seed.Take(index), .. seed.Skip(index + count)];
                        AssertModeledMutation(
                            seed,
                            options,
                            collection => collection.RemoveRange(rangeIndex, rangeCount),
                            removedModel,
                            countChanged: count != 0,
                            expectedAction: RemovalAction(mode, count));

                        for (var replacementCount = 0; replacementCount <= 3; replacementCount++)
                        {
                            int[] replacement = Enumerable.Range(200, replacementCount).ToArray();
                            int[] replacedModel =
                            [
                                .. seed.Take(index),
                                .. replacement,
                                .. seed.Skip(index + count),
                            ];
                            AssertModeledMutation(
                                seed,
                                options,
                                collection => collection.ReplaceRange(rangeIndex, rangeCount, replacement),
                                replacedModel,
                                countChanged: count != replacementCount,
                                expectedAction: ReplacementAction(mode, count, replacementCount));
                        }
                    }
                }

                for (var oldIndex = 0; oldIndex <= size; oldIndex++)
                {
                    for (var count = 0; count <= size - oldIndex; count++)
                    {
                        for (var newIndex = 0; newIndex <= size - count; newIndex++)
                        {
                            int sourceIndex = oldIndex;
                            int rangeCount = count;
                            int destinationIndex = newIndex;
                            var movedModel = seed.ToList();
                            int[] moved = movedModel.GetRange(oldIndex, count).ToArray();
                            movedModel.RemoveRange(oldIndex, count);
                            movedModel.InsertRange(newIndex, moved);

                            AssertModeledMutation(
                                seed,
                                options,
                                collection => collection.MoveRange(sourceIndex, rangeCount, destinationIndex),
                                movedModel,
                                countChanged: false,
                                expectedAction: MovementAction(mode, count, oldIndex, newIndex));
                        }
                    }
                }
            }
        }
    }

    private static void ClearAndSnapshotMatrix()
    {
        foreach (RangeNotificationMode mode in Enum.GetValues<RangeNotificationMode>())
        {
            var options = new ObservableRangeCollectionOptions { RangeNotifications = mode };
            AssertModeledMutation(
                [],
                options,
                static collection => collection.Clear(),
                [],
                countChanged: false,
                expectedAction: null);
            AssertModeledMutation(
                [0],
                options,
                static collection => collection.Clear(),
                [],
                countChanged: true,
                expectedAction: NotifyCollectionChangedAction.Reset);
            AssertModeledMutation(
                [0, 1, 2],
                options,
                static collection => collection.Clear(),
                [],
                countChanged: true,
                expectedAction: NotifyCollectionChangedAction.Reset);
        }

        var first = new SnapshotItem(1);
        var second = new SnapshotItem(2);
        var collection = new ObservableRangeCollection<SnapshotItem>([first, second]);
        SnapshotItem[] snapshot = collection.ToSnapshot();
        SnapshotItem[] anotherSnapshot = collection.ToSnapshot();
        Assert.False(ReferenceEquals(snapshot, anotherSnapshot));
        Assert.Same(first, snapshot[0]);
        Assert.Same(second, snapshot[1]);
        collection.RemoveAt(0);
        Assert.SequenceEqual([first, second], snapshot);
        snapshot[1] = first;
        Assert.Same(second, collection[0]);

        var empty = new ObservableRangeCollection<SnapshotItem>();
        Assert.Equal(0, empty.ToSnapshot().Length);
        Assert.False(ReferenceEquals(empty.ToSnapshot(), empty.ToSnapshot()));
    }

    private static void PayloadIsolationMatrix()
    {
        foreach (RangeNotificationMode mode in Enum.GetValues<RangeNotificationMode>())
        {
            var options = new ObservableRangeCollectionOptions { RangeNotifications = mode };
            AssertCopiedPayload(
                [0, 1],
                options,
                static (collection, source) => collection.AddRange(source),
                [7, 8],
                expectedOld: null,
                expectedNew: [7, 8]);
            AssertCopiedPayload(
                [0, 1],
                options,
                static (collection, source) => collection.InsertRange(1, source),
                [7, 8],
                expectedOld: null,
                expectedNew: [7, 8]);
            AssertCopiedPayload(
                [0, 1, 2, 3],
                options,
                static (collection, source) => collection.ReplaceRange(1, 2, source),
                [7, 8],
                expectedOld: [1, 2],
                expectedNew: [7, 8]);

            var removed = new ObservableRangeCollection<int>([0, 1, 2, 3], options);
            using var removedTrace = new TraceRecorder<int>(removed);
            removed.RemoveRange(1, 2);
            AssertPayload(removedTrace.Events.Single(), mode, expectedOld: [1, 2], expectedNew: null);
            removed.AddRange([9, 10]);
            AssertPayload(removedTrace.Events[0], mode, expectedOld: [1, 2], expectedNew: null);

            var moved = new ObservableRangeCollection<int>([0, 1, 2, 3], options);
            using var movedTrace = new TraceRecorder<int>(moved);
            moved.MoveRange(1, 2, 0);
            AssertPayload(movedTrace.Events.Single(), mode, expectedOld: [1, 2], expectedNew: [1, 2]);
            moved[0] = 99;
            AssertPayload(movedTrace.Events[0], mode, expectedOld: [1, 2], expectedNew: [1, 2]);
        }
    }

    private static void InvalidOperationsAreSilentAndAtomic()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new ObservableRangeCollection<int>((IEnumerable<int>)null!);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new ObservableRangeCollection<int>((ObservableRangeCollectionOptions)null!);
        });
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new ObservableRangeCollection<int>([1], (ObservableRangeCollectionOptions)null!);
        });
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new ObservableRangeCollection<int>(
                [1],
                new ObservableRangeCollectionOptions { RangeNotifications = (RangeNotificationMode)int.MaxValue });
        });

        foreach (RangeNotificationMode mode in Enum.GetValues<RangeNotificationMode>())
        {
            var collection = new ObservableRangeCollection<int>(
                [0, 1, 2],
                new ObservableRangeCollectionOptions { RangeNotifications = mode });
            using var trace = new TraceRecorder<int>(collection);

            foreach (Action nullAction in new Action[]
                     {
                         () => collection.AddRange(null!),
                         () => collection.InsertRange(0, null!),
                         () => collection.ReplaceRange(0, 0, null!),
                     })
            {
                Assert.Throws<ArgumentNullException>(nullAction);
                Assert.SequenceEqual([0, 1, 2], collection);
                Assert.Equal(0, trace.Entries.Count);
            }

            var invalid = new Action[]
            {
                () => collection.InsertRange(int.MinValue, [9]),
                () => collection.InsertRange(int.MaxValue, [9]),
                () => collection.RemoveRange(int.MinValue, 0),
                () => collection.RemoveRange(int.MaxValue, 0),
                () => collection.RemoveRange(0, int.MinValue),
                () => collection.RemoveRange(0, int.MaxValue),
                () => collection.ReplaceRange(int.MinValue, 0, [9]),
                () => collection.ReplaceRange(int.MaxValue, 0, [9]),
                () => collection.ReplaceRange(0, int.MinValue, [9]),
                () => collection.ReplaceRange(0, int.MaxValue, [9]),
                () => collection.MoveRange(int.MinValue, 0, 0),
                () => collection.MoveRange(int.MaxValue, 0, 0),
                () => collection.MoveRange(0, int.MinValue, 0),
                () => collection.MoveRange(0, int.MaxValue, 0),
                () => collection.MoveRange(0, 0, int.MinValue),
                () => collection.MoveRange(0, 0, int.MaxValue),
                () => collection.MoveRange(2, 2, 0),
                () => collection.RemoveRange(2, 2),
                () => collection.ReplaceRange(2, 2, [9]),
            };

            foreach (Action action in invalid)
            {
                Assert.Throws<ArgumentOutOfRangeException>(action);
                Assert.SequenceEqual([0, 1, 2], collection);
                Assert.Equal(0, trace.Entries.Count);
            }

            collection.RemoveRange(collection.Count, 0);
            collection.ReplaceRange(collection.Count, 0, []);
            collection.MoveRange(collection.Count, 0, 0);
            collection.MoveRange(0, 0, collection.Count);
            Assert.SequenceEqual([0, 1, 2], collection);
            Assert.Equal(0, trace.Entries.Count);
        }
    }

    private static void NullableDuplicateAndSelfSourceMatrix()
    {
        var nullable = new ObservableRangeCollection<string?>([null, "a", null]);
        nullable.InsertRange(1, [null, "a", null]);
        Assert.SequenceEqual<string?>([null, null, "a", null, "a", null], nullable);
        nullable.ReplaceRange(1, 3, nullable);
        Assert.SequenceEqual<string?>([null, null, null, "a", null, "a", null, "a", null], nullable);
        nullable.RemoveRange(0, 2);
        Assert.SequenceEqual<string?>([null, "a", null, "a", null, "a", null], nullable);

        var duplicates = new ObservableRangeCollection<int>([1, 1, 2, 1]);
        duplicates.AddRange(duplicates);
        Assert.SequenceEqual([1, 1, 2, 1, 1, 1, 2, 1], duplicates);
        duplicates.ReplaceRange(2, 3, duplicates);
        Assert.SequenceEqual([1, 1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 2, 1], duplicates);
    }

    private static void EventCardinalityMatrix()
    {
        foreach (RangeNotificationMode mode in Enum.GetValues<RangeNotificationMode>())
        {
            var options = new ObservableRangeCollectionOptions { RangeNotifications = mode };
            var collection = new ObservableRangeCollection<int>(options);
            var propertyEvents = 0;
            var collectionEvents = 0;
            ((INotifyPropertyChanged)collection).PropertyChanged += (_, _) => propertyEvents++;
            collection.CollectionChanged += (_, _) => collectionEvents++;

            const int iterations = 64;
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                collection.AddRange([iteration, iteration]);
            }

            Assert.Equal(iterations * 2, propertyEvents);
            Assert.Equal(iterations, collectionEvents);

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                collection.ReplaceRange(iteration * 2, 2, [iteration + 1, iteration + 1]);
            }

            Assert.Equal(iterations * 3, propertyEvents);
            Assert.Equal(iterations * 2, collectionEvents);

            for (var iteration = iterations - 1; iteration >= 0; iteration--)
            {
                collection.RemoveRange(iteration * 2, 2);
            }

            Assert.Equal(iterations * 5, propertyEvents);
            Assert.Equal(iterations * 3, collectionEvents);
            Assert.Equal(0, collection.Count);
        }
    }

    private static void SubscriberListMutationSemantics()
    {
        var collection = new ObservableRangeCollection<int>();
        var calls = new List<string>();
        NotifyCollectionChangedEventHandler late = (_, _) => calls.Add("late");
        NotifyCollectionChangedEventHandler second = (_, _) => calls.Add("second");
        NotifyCollectionChangedEventHandler? first = null;
        first = (_, _) =>
        {
            calls.Add("first");
            collection.CollectionChanged -= first;
            collection.CollectionChanged += late;
        };
        collection.CollectionChanged += first;
        collection.CollectionChanged += second;

        collection.AddRange([1, 2]);
        Assert.SequenceEqual(["first", "second"], calls);
        calls.Clear();
        collection.AddRange([3, 4]);
        Assert.SequenceEqual(["second", "late"], calls);

        calls.Clear();
        collection.CollectionChanged += second;
        collection.Add(5);
        Assert.SequenceEqual(["second", "late", "second"], calls);
        calls.Clear();
        collection.CollectionChanged -= second;
        collection.Add(6);
        Assert.SequenceEqual(["second", "late"], calls);
    }

    private static NotifyCollectionChangedAction? AdditionAction(RangeNotificationMode mode, int count) =>
        count == 0
            ? null
            : count == 1 || mode == RangeNotificationMode.Range
                ? NotifyCollectionChangedAction.Add
                : NotifyCollectionChangedAction.Reset;

    private static NotifyCollectionChangedAction? RemovalAction(RangeNotificationMode mode, int count) =>
        count == 0
            ? null
            : count == 1 || mode == RangeNotificationMode.Range
                ? NotifyCollectionChangedAction.Remove
                : NotifyCollectionChangedAction.Reset;

    private static NotifyCollectionChangedAction? ReplacementAction(
        RangeNotificationMode mode,
        int oldCount,
        int newCount)
    {
        if (oldCount == 0)
        {
            return AdditionAction(mode, newCount);
        }

        if (newCount == 0)
        {
            return RemovalAction(mode, oldCount);
        }

        if (oldCount != newCount)
        {
            return NotifyCollectionChangedAction.Reset;
        }

        return oldCount == 1 || mode == RangeNotificationMode.Range
            ? NotifyCollectionChangedAction.Replace
            : NotifyCollectionChangedAction.Reset;
    }

    private static NotifyCollectionChangedAction? MovementAction(
        RangeNotificationMode mode,
        int count,
        int oldIndex,
        int newIndex) =>
        count == 0 || oldIndex == newIndex
            ? null
            : count == 1 || mode == RangeNotificationMode.Range
                ? NotifyCollectionChangedAction.Move
                : NotifyCollectionChangedAction.Reset;

    private static void AssertModeledMutation(
        IEnumerable<int> initial,
        ObservableRangeCollectionOptions options,
        Action<ObservableRangeCollection<int>> mutation,
        IEnumerable<int> expected,
        bool countChanged,
        NotifyCollectionChangedAction? expectedAction)
    {
        var collection = new ObservableRangeCollection<int>(initial, options);
        int[] expectedState = expected.ToArray();
        var propertyNames = new List<string?>();
        var propertyStates = new List<int[]>();
        var collectionStates = new List<int[]>();
        var events = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, args) =>
        {
            propertyNames.Add(args.PropertyName);
            propertyStates.Add(collection.ToSnapshot());
        };
        collection.CollectionChanged += (_, args) =>
        {
            events.Add(args);
            collectionStates.Add(collection.ToSnapshot());
        };

        mutation(collection);

        Assert.SequenceEqual(expectedState, collection);
        if (expectedAction is null)
        {
            Assert.Equal(0, propertyNames.Count);
            Assert.Equal(0, events.Count);
            return;
        }

        Assert.Equal(countChanged ? 2 : 1, propertyNames.Count);
        if (countChanged)
        {
            Assert.Equal("Count", propertyNames[0]);
        }

        Assert.Equal("Item[]", propertyNames[^1]);
        Assert.Equal(1, events.Count);
        Assert.Equal(expectedAction.Value, events[0].Action);
        foreach (int[] state in propertyStates)
        {
            Assert.SequenceEqual(expectedState, state);
        }

        Assert.SequenceEqual(expectedState, collectionStates.Single());
    }

    private static void AssertCopiedPayload(
        IEnumerable<int> initial,
        ObservableRangeCollectionOptions options,
        Action<ObservableRangeCollection<int>, IEnumerable<int>> mutation,
        int[] supplied,
        int[]? expectedOld,
        int[]? expectedNew)
    {
        var source = supplied.ToList();
        var collection = new ObservableRangeCollection<int>(initial, options);
        using var trace = new TraceRecorder<int>(collection);
        mutation(collection, source);
        NotifyCollectionChangedEventArgs args = trace.Events.Single();
        source[0] = -1;
        source.Add(-2);
        if (collection.Count != 0)
        {
            collection[0] = -3;
        }

        AssertPayload(args, options.RangeNotifications, expectedOld, expectedNew);
    }

    private static void AssertPayload(
        NotifyCollectionChangedEventArgs args,
        RangeNotificationMode mode,
        int[]? expectedOld,
        int[]? expectedNew)
    {
        if (mode == RangeNotificationMode.Reset)
        {
            Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action);
            Assert.True(args.OldItems is null && args.NewItems is null);
            return;
        }

        if (expectedOld is null)
        {
            Assert.True(args.OldItems is null);
        }
        else
        {
            Assert.SequenceEqual(expectedOld, args.OldItems!.Cast<int>());
            Assert.True(args.OldItems!.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => args.OldItems!.Add(-10));
        }

        if (expectedNew is null)
        {
            Assert.True(args.NewItems is null);
        }
        else
        {
            Assert.SequenceEqual(expectedNew, args.NewItems!.Cast<int>());
            Assert.True(args.NewItems!.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => args.NewItems!.Add(-10));
        }
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

    private sealed class SnapshotItem(int value)
    {
        public int Value { get; } = value;
    }
}
