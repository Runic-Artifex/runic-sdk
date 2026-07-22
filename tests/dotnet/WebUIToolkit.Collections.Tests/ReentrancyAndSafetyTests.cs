using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace WebUIToolkit.Collections.Tests;

internal static class ReentrancyAndSafetyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("OR1 reentrancy / PropertyChanged one and multiple subscribers", PropertyChangedReentrancy),
        new("OR1 reentrancy / CollectionChanged one and multiple subscribers", CollectionChangedReentrancy),
        new("OR1 reentrancy / source iterator", SourceIteratorReentrancy),
        new("OR2 reentrancy / comparer", ComparerReentrancy),
        new("OR2 reentrancy / key selector", KeySelectorReentrancy),
        new("OR2 reentrancy / match resolver", ResolverReentrancy),
        new("OR1 exception safety / throwing source is pre-mutation", ThrowingSourceIsAtomic),
        new("OR2 exception safety / comparer selector resolver are pre-mutation", PlanningFailuresAreAtomic),
        new("OR1 exception safety / PropertyChanged subscriber leaves coherent state", ThrowingPropertySubscriber),
        new("OR1 exception safety / CollectionChanged subscriber leaves coherent state", ThrowingCollectionSubscriber),
        new("OR2 exception safety / reset subscriber leaves final state", ThrowingResetSubscriber),
        new("OR2 exception safety / granular subscriber leaves usable partial state", ThrowingGranularSubscriber),
    ];

    private static void PropertyChangedReentrancy()
    {
        foreach (var subscriberCount in new[] { 1, 2 })
        {
            foreach (var mutation in ReentrantMutations())
            {
                var collection = NewCollection();
                var attempts = 0;
                PropertyChangedEventHandler handler = (_, _) =>
                {
                    attempts++;
                    Assert.Throws<InvalidOperationException>(() => mutation.Value(collection));
                };
                ((INotifyPropertyChanged)collection).PropertyChanged += handler;
                PropertyChangedEventHandler? observer = subscriberCount == 2 ? static (_, _) => { } : null;
                if (observer is not null)
                {
                    ((INotifyPropertyChanged)collection).PropertyChanged += observer;
                }

                collection.AddRange([7, 8]);
                Assert.True(attempts >= 1, $"Property callback did not run for {mutation.Key}.");
                Assert.SequenceEqual([0, 1, 2, 3, 7, 8], collection);
                ((INotifyPropertyChanged)collection).PropertyChanged -= handler;
                if (observer is not null)
                {
                    ((INotifyPropertyChanged)collection).PropertyChanged -= observer;
                }

                collection.Add(9);
                Assert.Equal(9, collection[^1]);
            }
        }
    }

    private static void CollectionChangedReentrancy()
    {
        foreach (var subscriberCount in new[] { 1, 2 })
        {
            foreach (var mutation in ReentrantMutations())
            {
                var collection = NewCollection();
                var attempts = 0;
                System.Collections.Specialized.NotifyCollectionChangedEventHandler handler = (_, _) =>
                {
                    attempts++;
                    Assert.Throws<InvalidOperationException>(() => mutation.Value(collection));
                };
                collection.CollectionChanged += handler;
                System.Collections.Specialized.NotifyCollectionChangedEventHandler? observer =
                    subscriberCount == 2 ? static (_, _) => { }
                : null;
                if (observer is not null)
                {
                    collection.CollectionChanged += observer;
                }

                collection.AddRange([7, 8]);
                Assert.Equal(1, attempts, $"Collection callback did not run exactly once for {mutation.Key}.");
                Assert.SequenceEqual([0, 1, 2, 3, 7, 8], collection);
                collection.CollectionChanged -= handler;
                if (observer is not null)
                {
                    collection.CollectionChanged -= observer;
                }

                collection.Add(9);
                Assert.Equal(9, collection[^1]);
            }
        }
    }

    private static void SourceIteratorReentrancy()
    {
        var collection = NewCollection();
        using var trace = new TraceRecorder<int>(collection);
        Assert.Throws<InvalidOperationException>(() => collection.AddRange(MutatingIterator(collection)));
        Assert.SequenceEqual([0, 1, 2, 3], collection);
        Assert.Equal(0, trace.Entries.Count);
        collection.Add(4);
        Assert.Equal(4, collection[^1]);
    }

    private static void ComparerReentrancy()
    {
        var collection = NewCollection();
        using var trace = new TraceRecorder<int>(collection);
        var comparer = new CallbackComparer<int>(
            static (left, right) => left == right,
            () => collection.Add(9));

        Assert.Throws<InvalidOperationException>(() => collection.UpdateTo([3, 2, 1], comparer));
        Assert.SequenceEqual([0, 1, 2, 3], collection);
        Assert.Equal(0, trace.Entries.Count);
        collection.Add(4);
    }

    private static void KeySelectorReentrancy()
    {
        var collection = NewCollection();
        using var trace = new TraceRecorder<int>(collection);

        Assert.Throws<InvalidOperationException>(() => collection.UpdateTo(
            [3, 2, 1],
            item =>
            {
                collection.Add(9);
                return item;
            }));
        Assert.SequenceEqual([0, 1, 2, 3], collection);
        Assert.Equal(0, trace.Entries.Count);
        collection.Add(4);
    }

    private static void ResolverReentrancy()
    {
        var collection = NewCollection();
        using var trace = new TraceRecorder<int>(collection);

        Assert.Throws<InvalidOperationException>(() => collection.UpdateTo(
            [3, 2, 1],
            resolveMatch: (existing, _) =>
            {
                collection.Add(9);
                return existing;
            }));
        Assert.SequenceEqual([0, 1, 2, 3], collection);
        Assert.Equal(0, trace.Entries.Count);
        collection.Add(4);
    }

    private static void ThrowingSourceIsAtomic()
    {
        foreach (var operation in new Action<ObservableRangeCollection<int>, IEnumerable<int>>[]
                 {
                     static (collection, source) => collection.AddRange(source),
                     static (collection, source) => collection.InsertRange(1, source),
                     static (collection, source) => collection.ReplaceRange(1, 2, source),
                     static (collection, source) => collection.UpdateTo(source),
                 })
        {
            var collection = NewCollection();
            using var trace = new TraceRecorder<int>(collection);
            Assert.Throws<SentinelException>(() => operation(collection, ThrowAfterOne()));
            Assert.SequenceEqual([0, 1, 2, 3], collection);
            Assert.Equal(0, trace.Entries.Count);
            collection.Add(4);
        }
    }

    private static void PlanningFailuresAreAtomic()
    {
        AssertPlanningFailure(collection => collection.UpdateTo(
            [3, 2, 1],
            new ThrowingComparer<int>()));

        AssertPlanningFailure(collection => collection.UpdateTo<int>(
            [3, 2, 1],
            _ => throw new SentinelException()));

        AssertPlanningFailure(collection => collection.UpdateTo(
            [3, 2, 1],
            resolveMatch: static (_, _) => throw new SentinelException()));
    }

    private static void ThrowingPropertySubscriber()
    {
        var collection = NewCollection();
        PropertyChangedEventHandler handler = static (_, _) => throw new SentinelException();
        ((INotifyPropertyChanged)collection).PropertyChanged += handler;

        Assert.Throws<SentinelException>(() => collection.AddRange([4, 5]));
        Assert.SequenceEqual([0, 1, 2, 3, 4, 5], collection);
        ((INotifyPropertyChanged)collection).PropertyChanged -= handler;
        collection.RemoveRange(4, 2);
        Assert.SequenceEqual([0, 1, 2, 3], collection);
    }

    private static void ThrowingCollectionSubscriber()
    {
        var collection = NewCollection();
        System.Collections.Specialized.NotifyCollectionChangedEventHandler handler =
            static (_, _) => throw new SentinelException();
        collection.CollectionChanged += handler;

        Assert.Throws<SentinelException>(() => collection.ReplaceRange(1, 2, [8, 9]));
        Assert.SequenceEqual([0, 8, 9, 3], collection);
        collection.CollectionChanged -= handler;
        collection.MoveRange(1, 2, 0);
        Assert.SequenceEqual([8, 9, 0, 3], collection);
    }

    private static void ThrowingResetSubscriber()
    {
        var collection = NewCollection();
        System.Collections.Specialized.NotifyCollectionChangedEventHandler handler =
            static (_, _) => throw new SentinelException();
        collection.CollectionChanged += handler;

        Assert.Throws<SentinelException>(() => collection.UpdateTo(
            [3, 7, 0],
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Reset }));
        Assert.SequenceEqual([3, 7, 0], collection);
        collection.CollectionChanged -= handler;
        collection.Add(8);
        Assert.SequenceEqual([3, 7, 0, 8], collection);
    }

    private static void ThrowingGranularSubscriber()
    {
        var collection = NewCollection();
        System.Collections.Specialized.NotifyCollectionChangedEventHandler handler =
            static (_, _) => throw new SentinelException();
        collection.CollectionChanged += handler;

        Assert.Throws<SentinelException>(() => collection.UpdateTo(
            [3, 7, 0],
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular }));
        Assert.True(collection.Count > 0, "A failed granular update must leave a coherent collection.");
        _ = collection.ToSnapshot();
        collection.CollectionChanged -= handler;
        collection.UpdateTo(
            [3, 7, 0],
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });
        Assert.SequenceEqual([3, 7, 0], collection);
    }

    private static Dictionary<string, Action<ObservableRangeCollection<int>>> ReentrantMutations() =>
        new Dictionary<string, Action<ObservableRangeCollection<int>>>(StringComparer.Ordinal)
        {
            ["inherited Add"] = static collection => collection.Add(10),
            ["inherited RemoveAt"] = static collection => collection.RemoveAt(0),
            ["inherited SetItem"] = static collection => collection[0] = 10,
            ["inherited Move"] = static collection => collection.Move(0, 1),
            ["inherited Clear"] = static collection => collection.Clear(),
            ["AddRange"] = static collection => collection.AddRange([10, 11]),
            ["RemoveRange"] = static collection => collection.RemoveRange(0, 1),
            ["ReplaceRange"] = static collection => collection.ReplaceRange(0, 1, [10]),
            ["MoveRange"] = static collection => collection.MoveRange(0, 1, 1),
            ["UpdateTo"] = static collection => collection.UpdateTo([]),
        };

    private static ObservableRangeCollection<int> NewCollection() => new([0, 1, 2, 3]);

    private static IEnumerable<int> MutatingIterator(ObservableRangeCollection<int> collection)
    {
        collection.Add(9);
        yield return 4;
    }

    private static IEnumerable<int> ThrowAfterOne()
    {
        yield return 8;
        throw new SentinelException();
    }

    private static void AssertPlanningFailure(Action<ObservableRangeCollection<int>> update)
    {
        var collection = NewCollection();
        using var trace = new TraceRecorder<int>(collection);
        Assert.Throws<SentinelException>(() => update(collection));
        Assert.SequenceEqual([0, 1, 2, 3], collection);
        Assert.Equal(0, trace.Entries.Count);
        collection.Add(4);
    }

    private sealed class CallbackComparer<T>(Func<T?, T?, bool> equals, Action callback) : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y)
        {
            callback();
            return equals(x, y);
        }

        public int GetHashCode(T obj) => EqualityComparer<T>.Default.GetHashCode(obj!);
    }

    private sealed class ThrowingComparer<T> : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => throw new SentinelException();

        public int GetHashCode(T obj) => throw new SentinelException();
    }

    private sealed class SentinelException : Exception;
}
