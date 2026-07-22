using System;
using System.Collections.Generic;
using System.Linq;

namespace WebUIToolkit.Collections.Tests;

internal static class UpdateToTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("OR2 reconciliation / deterministic granular trace and result", GranularTraceAndResult),
        new("OR2 reconciliation / reset trace and planned result", ResetTraceAndResult),
        new("OR2 reconciliation / auto count threshold boundaries", AutoCountThresholds),
        new("OR2 reconciliation / auto ratio threshold boundaries", AutoRatioThresholds),
        new("OR2 reconciliation / comparer FIFO duplicates preserve identity", ComparerFifoDuplicates),
        new("OR2 reconciliation / keyed identity preservation", KeyedIdentity),
        new("OR2 reconciliation / keyed duplicate and null validation", KeyValidation),
        new("OR2 reconciliation / resolver exactly once and replacement", ResolverSemantics),
        new("OR2 reconciliation / no-op silence and result", AlreadySatisfiedIsSilent),
        new("OR2 reconciliation / granular and reset replay", UpdateReplay),
        new("OR2 reconciliation / invalid options are pre-mutation", InvalidOptions),
    ];

    private static void GranularTraceAndResult()
    {
        var collection = new ObservableRangeCollection<string>(["A", "B", "C"]);
        using var trace = new TraceRecorder<string>(collection);
        var options = new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular };

        var result = collection.UpdateTo(["C", "A", "D"], options: options);

        Assert.SequenceEqual(["C", "A", "D"], collection);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(3, result.NotificationCount);
        Assert.False(result.UsedReset);
        Assert.True(result.Changed);
        Assert.SequenceEqual(
            [
                "P:Item[]:[C,A,B]",
                "C:Move:old=[C]@2:new=[C]@0:[C,A,B]",
                "P:Count:[C,A,D,B]",
                "P:Item[]:[C,A,D,B]",
                "C:Add:old=-@-1:new=[D]@2:[C,A,D,B]",
                "P:Count:[C,A,D]",
                "P:Item[]:[C,A,D]",
                "C:Remove:old=[B]@3:new=-@-1:[C,A,D]",
            ],
            trace.Entries);
    }

    private static void ResetTraceAndResult()
    {
        var collection = new ObservableRangeCollection<string>(["A", "B", "C"]);
        using var trace = new TraceRecorder<string>(collection);
        var options = new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Reset };

        var result = collection.UpdateTo(["C", "A", "D"], options: options);

        Assert.SequenceEqual(["C", "A", "D"], collection);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(1, result.NotificationCount);
        Assert.True(result.UsedReset);
        Assert.True(result.Changed);
        Assert.SequenceEqual(
            [
                "P:Item[]:[C,A,D]",
                "C:Reset:old=-@-1:new=-@-1:[C,A,D]",
            ],
            trace.Entries);
    }

    private static void AutoCountThresholds()
    {
        var atBoundary = new ObservableRangeCollection<int>([1]);
        var granular = atBoundary.UpdateTo(
            [1, 2],
            options: new CollectionUpdateOptions
            {
                Notifications = UpdateNotificationMode.Auto,
                MaxGranularEvents = 1,
                ResetRatioMinimumCount = int.MaxValue,
            });
        Assert.False(granular.UsedReset, "eventCount == MaxGranularEvents must remain granular.");
        Assert.Equal(1, granular.NotificationCount);

        var aboveBoundary = new ObservableRangeCollection<int>([1]);
        var reset = aboveBoundary.UpdateTo(
            [1, 2],
            options: new CollectionUpdateOptions
            {
                Notifications = UpdateNotificationMode.Auto,
                MaxGranularEvents = 0,
                ResetRatioMinimumCount = int.MaxValue,
            });
        Assert.True(reset.UsedReset, "eventCount > MaxGranularEvents must reset.");
        Assert.Equal(1, reset.NotificationCount);
    }

    private static void AutoRatioThresholds()
    {
        var atBoundary = new ObservableRangeCollection<int>(Enumerable.Range(0, 10));
        var granular = atBoundary.UpdateTo(
            Enumerable.Range(0, 5),
            options: new CollectionUpdateOptions
            {
                Notifications = UpdateNotificationMode.Auto,
                MaxGranularEvents = 100,
                ResetRatioMinimumCount = 10,
                ResetChangeRatio = 0.5,
            });
        Assert.False(granular.UsedReset, "A ratio equal to the threshold must remain granular.");
        Assert.Equal(5, granular.NotificationCount);

        var aboveBoundary = new ObservableRangeCollection<int>(Enumerable.Range(0, 10));
        var reset = aboveBoundary.UpdateTo(
            Enumerable.Range(0, 4),
            options: new CollectionUpdateOptions
            {
                Notifications = UpdateNotificationMode.Auto,
                MaxGranularEvents = 100,
                ResetRatioMinimumCount = 10,
                ResetChangeRatio = 0.5,
            });
        Assert.True(reset.UsedReset, "A ratio above the threshold must reset.");
        Assert.Equal(1, reset.NotificationCount);
        Assert.Equal(6, reset.Removed);
    }

    private static void ComparerFifoDuplicates()
    {
        var firstA = new Item("A", "first-A");
        var secondA = new Item("A", "second-A");
        var existingB = new Item("B", "existing-B");
        var collection = new ObservableRangeCollection<Item>([firstA, existingB, secondA]);
        var result = collection.UpdateTo(
            [new Item("A", "incoming-A1"), new Item("A", "incoming-A2"), new Item("B", "incoming-B")],
            ItemValueComparer.Instance,
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });

        Assert.Same(firstA, collection[0]);
        Assert.Same(secondA, collection[1]);
        Assert.Same(existingB, collection[2]);
        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Replaced);
    }

    private static void KeyedIdentity()
    {
        var a = new KeyedItem("a", "old-a");
        var b = new KeyedItem("b", "old-b");
        var c = new KeyedItem("c", "old-c");
        var incomingD = new KeyedItem("d", "new-d");
        var collection = new ObservableRangeCollection<KeyedItem>([a, b, c]);

        var result = collection.UpdateTo(
            [new KeyedItem("c", "new-c"), new KeyedItem("a", "new-a"), incomingD],
            static item => item.Key,
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });

        Assert.Same(c, collection[0]);
        Assert.Same(a, collection[1]);
        Assert.Same(incomingD, collection[2]);
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
    }

    private static void KeyValidation()
    {
        var duplicateCurrent = new ObservableRangeCollection<KeyedItem>(
            [new KeyedItem("a", "one"), new KeyedItem("a", "two")]);
        var beforeCurrent = duplicateCurrent.ToSnapshot();
        Assert.Throws<ArgumentException>(
            () => duplicateCurrent.UpdateTo([new KeyedItem("a", "target")], static item => item.Key),
            "current");
        AssertReferences(beforeCurrent, duplicateCurrent);

        var duplicateTarget = new ObservableRangeCollection<KeyedItem>([new KeyedItem("a", "one")]);
        var beforeTarget = duplicateTarget.ToSnapshot();
        Assert.Throws<ArgumentException>(
            () => duplicateTarget.UpdateTo(
                [new KeyedItem("b", "one"), new KeyedItem("b", "two")],
                static item => item.Key),
            "target");
        AssertReferences(beforeTarget, duplicateTarget);

        var nullCurrent = new ObservableRangeCollection<KeyedItem>([new KeyedItem(null!, "bad")]);
        Assert.Throws<ArgumentException>(
            () => nullCurrent.UpdateTo([], static item => item.Key),
            "current");

        var nullTarget = new ObservableRangeCollection<KeyedItem>();
        Assert.Throws<ArgumentException>(
            () => nullTarget.UpdateTo([new KeyedItem(null!, "bad")], static item => item.Key),
            "target");
    }

    private static void ResolverSemantics()
    {
        var a = new KeyedItem("a", "old-a");
        var b = new KeyedItem("b", "old-b");
        var incomingA = new KeyedItem("a", "new-a");
        var incomingB = new KeyedItem("b", "new-b");
        var replacementA = new KeyedItem("a", "resolved-a");
        var calls = new List<string>();
        var collection = new ObservableRangeCollection<KeyedItem>([a, b]);

        var result = collection.UpdateTo(
            [incomingA, incomingB],
            static item => item.Key,
            resolveMatch: (existing, incoming) =>
            {
                calls.Add(existing.Key);
                return existing.Key == "a" ? replacementA : incoming;
            },
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });

        Assert.SequenceEqual(["a", "b"], calls);
        Assert.Same(replacementA, collection[0]);
        Assert.Same(incomingB, collection[1]);
        Assert.Equal(2, result.Replaced);
    }

    private static void AlreadySatisfiedIsSilent()
    {
        var first = new Item("A", "first");
        var second = new Item("B", "second");
        var collection = new ObservableRangeCollection<Item>([first, second]);
        using var trace = new TraceRecorder<Item>(collection);

        var result = collection.UpdateTo(
            [new Item("A", "incoming-a"), new Item("B", "incoming-b")],
            ItemValueComparer.Instance);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Moved);
        Assert.Equal(0, result.Replaced);
        Assert.Equal(0, result.NotificationCount);
        Assert.False(result.UsedReset);
        Assert.Equal(0, trace.Entries.Count);
        Assert.Same(first, collection[0]);
        Assert.Same(second, collection[1]);
    }

    private static void UpdateReplay()
    {
        foreach (var mode in new[] { UpdateNotificationMode.Granular, UpdateNotificationMode.Reset, UpdateNotificationMode.Auto })
        {
            var collection = new ObservableRangeCollection<int>([0, 1, 2, 3, 4]);
            var shadow = collection.ToList();
            collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);
            collection.UpdateTo(
                [4, 2, 7, 0],
                options: new CollectionUpdateOptions
                {
                    Notifications = mode,
                    MaxGranularEvents = 1,
                    ResetRatioMinimumCount = 1,
                    ResetChangeRatio = 0.1,
                });
            Assert.SequenceEqual(collection, shadow, $"Replay failed in {mode} mode.");
        }
    }

    private static void InvalidOptions()
    {
        var invalidOptions = new[]
        {
            new CollectionUpdateOptions { MaxGranularEvents = -1 },
            new CollectionUpdateOptions { ResetRatioMinimumCount = -1 },
            new CollectionUpdateOptions { ResetChangeRatio = -0.01 },
            new CollectionUpdateOptions { ResetChangeRatio = 1.01 },
            new CollectionUpdateOptions { ResetChangeRatio = double.NaN },
            new CollectionUpdateOptions { Notifications = (UpdateNotificationMode)999 },
        };

        foreach (var options in invalidOptions)
        {
            var collection = new ObservableRangeCollection<int>([1, 2, 3]);
            using var trace = new TraceRecorder<int>(collection);
            Assert.Throws<ArgumentOutOfRangeException>(() => collection.UpdateTo([3, 2, 1], options: options));
            Assert.SequenceEqual([1, 2, 3], collection);
            Assert.Equal(0, trace.Entries.Count);
        }
    }

    private static void AssertReferences<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
        where T : class
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index], actual[index]);
        }
    }

    private sealed record Item(string Value, string Identity);

    private sealed class ItemValueComparer : IEqualityComparer<Item>
    {
        public static ItemValueComparer Instance { get; } = new();

        public bool Equals(Item? x, Item? y) => StringComparer.Ordinal.Equals(x?.Value, y?.Value);

        public int GetHashCode(Item obj) => StringComparer.Ordinal.GetHashCode(obj.Value);
    }

    private sealed record KeyedItem(string Key, string Value);
}
