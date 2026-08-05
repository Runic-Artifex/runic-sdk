using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace RunicToolkit.Collections.Tests;

internal static class ReentrancyAndSafetyTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("OR1 reentrancy / PropertyChanged one and multiple subscribers", PropertyChangedReentrancy),
        new("OR1 reentrancy / CollectionChanged one and multiple subscribers", CollectionChangedReentrancy),
        new("OR1-OR2 reentrancy / source iterators guard every mutation entry point", SourceIteratorReentrancy),
        new("OR2 reentrancy / equality comparer guards every mutation entry point", ComparerReentrancy),
        new("OR2 reentrancy / key selector guards every mutation entry point", KeySelectorReentrancy),
        new("OR2 reentrancy / key comparer Equals and GetHashCode guard every mutation entry point", KeyComparerReentrancy),
        new("OR2 reentrancy / match resolver guards every mutation entry point", ResolverReentrancy),
        new("OR1 exception safety / throwing source is pre-mutation", ThrowingSourceIsAtomic),
        new("OR2 exception safety / all planning callbacks fail before mutation", PlanningFailuresAreAtomic),
        new("OR1 exception safety / Count subscriber truncates the event stream exactly", ThrowingCountSubscriber),
        new("OR1 exception safety / Item[] subscriber truncates the event stream exactly", ThrowingIndexerSubscriber),
        new("OR1 exception safety / collection subscriber truncates the event stream exactly", ThrowingCollectionSubscriber),
        new("OR2 exception safety / reset subscriber leaves final state", ThrowingResetSubscriber),
        new("OR2 exception safety / granular subscriber leaves exact usable partial state", ThrowingGranularSubscriber),
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
        foreach (var outerOperation in SourceOperations())
        {
            foreach (var mutation in ReentrantMutations())
            {
                var collection = NewCollection();
                using var trace = new TraceRecorder<int>(collection);
                Assert.Throws<InvalidOperationException>(
                    () => outerOperation.Value(collection, MutatingIterator(() => mutation.Value(collection))),
                    messageContains: "structural mutation");
                AssertUnchangedAndRecovered(collection, trace, $"{outerOperation.Key} / {mutation.Key}");
            }
        }
    }

    private static void ComparerReentrancy()
    {
        foreach (var mutation in ReentrantMutations())
        {
            var collection = NewCollection();
            using var trace = new TraceRecorder<int>(collection);
            var comparer = new CallbackComparer<int>(
                static (left, right) => left == right,
                () => mutation.Value(collection));

            Assert.Throws<InvalidOperationException>(() => collection.UpdateTo([3, 2, 1], comparer));
            AssertUnchangedAndRecovered(collection, trace, mutation.Key);
        }
    }

    private static void KeySelectorReentrancy()
    {
        foreach (var mutation in ReentrantMutations())
        {
            var collection = NewCollection();
            using var trace = new TraceRecorder<int>(collection);

            Assert.Throws<InvalidOperationException>(() => collection.UpdateTo(
                [3, 2, 1],
                item =>
                {
                    mutation.Value(collection);
                    return item;
                }));
            AssertUnchangedAndRecovered(collection, trace, mutation.Key);
        }
    }

    private static void KeyComparerReentrancy()
    {
        foreach (var callbackKind in new[] { KeyComparerCallback.GetHashCode, KeyComparerCallback.Equals })
        {
            foreach (var mutation in ReentrantMutations())
            {
                var collection = NewCollection();
                using var trace = new TraceRecorder<int>(collection);
                var comparer = new CallbackKeyComparer(
                    callbackKind,
                    () => mutation.Value(collection));

                Assert.Throws<InvalidOperationException>(() => collection.UpdateTo(
                    [3, 2, 1],
                    static item => item,
                    keyComparer: comparer));
                Assert.True(comparer.CallbackCount > 0, $"{callbackKind} was not called for {mutation.Key}.");
                AssertUnchangedAndRecovered(collection, trace, $"{callbackKind} / {mutation.Key}");
            }
        }
    }

    private static void ResolverReentrancy()
    {
        foreach (var mutation in ReentrantMutations())
        {
            var collection = NewCollection();
            using var trace = new TraceRecorder<int>(collection);

            Assert.Throws<InvalidOperationException>(() => collection.UpdateTo(
                [3, 2, 1],
                resolveMatch: (existing, _) =>
                {
                    mutation.Value(collection);
                    return existing;
                }));
            AssertUnchangedAndRecovered(collection, trace, mutation.Key);
        }
    }

    private static void ThrowingSourceIsAtomic()
    {
        foreach (var operation in new Action<ObservableRangeCollection<int>, IEnumerable<int>>[]
                 {
                     static (collection, source) => collection.AddRange(source),
                     static (collection, source) => collection.InsertRange(1, source),
                     static (collection, source) => collection.ReplaceRange(1, 2, source),
                     static (collection, source) => collection.UpdateTo(source),
                     static (collection, source) => collection.UpdateTo(source, static item => item),
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
            new ThrowingEqualsComparer<int>()));

        AssertPlanningFailure(collection => collection.UpdateTo<int>(
            [3, 2, 1],
            _ => throw new SentinelException()));

        AssertPlanningFailure(collection => collection.UpdateTo(
            [3, 2, 1],
            static item => item,
            new ThrowingHashComparer<int>()));

        AssertPlanningFailure(collection => collection.UpdateTo(
            [3, 2, 1],
            static item => item,
            new ThrowingEqualsComparer<int>()));

        AssertPlanningFailure(collection => collection.UpdateTo(
            [3, 2, 1],
            resolveMatch: static (_, _) => throw new SentinelException()));

        AssertPlanningFailure(collection => collection.UpdateTo(
            [3, 2, 1],
            static item => item,
            resolveMatch: static (_, _) => throw new SentinelException()));
    }

    private static void ThrowingCountSubscriber()
    {
        var collection = NewCollection();
        var entries = new List<string>();
        PropertyChangedEventHandler throwing = (_, args) =>
        {
            entries.Add($"throwing:{args.PropertyName}");
            throw new SentinelException();
        };
        PropertyChangedEventHandler suppressed = (_, args) => entries.Add($"suppressed:{args.PropertyName}");
        NotifyCollectionChangedEventHandler collectionObserver = (_, args) => entries.Add($"collection:{args.Action}");
        ((INotifyPropertyChanged)collection).PropertyChanged += throwing;
        ((INotifyPropertyChanged)collection).PropertyChanged += suppressed;
        collection.CollectionChanged += collectionObserver;

        Assert.Throws<SentinelException>(() => collection.AddRange([4, 5]));
        Assert.SequenceEqual([0, 1, 2, 3, 4, 5], collection);
        Assert.SequenceEqual(["throwing:Count"], entries);
        ((INotifyPropertyChanged)collection).PropertyChanged -= throwing;
        ((INotifyPropertyChanged)collection).PropertyChanged -= suppressed;
        collection.CollectionChanged -= collectionObserver;
        collection.RemoveRange(4, 2);
        Assert.SequenceEqual([0, 1, 2, 3], collection);
    }

    private static void ThrowingIndexerSubscriber()
    {
        var collection = NewCollection();
        var entries = new List<string>();
        PropertyChangedEventHandler before = (_, args) => entries.Add($"before:{args.PropertyName}");
        PropertyChangedEventHandler throwing = (_, args) =>
        {
            entries.Add($"throwing:{args.PropertyName}");
            if (args.PropertyName == "Item[]")
            {
                throw new SentinelException();
            }
        };
        PropertyChangedEventHandler suppressed = (_, args) => entries.Add($"after:{args.PropertyName}");
        NotifyCollectionChangedEventHandler collectionObserver = (_, args) => entries.Add($"collection:{args.Action}");
        ((INotifyPropertyChanged)collection).PropertyChanged += before;
        ((INotifyPropertyChanged)collection).PropertyChanged += throwing;
        ((INotifyPropertyChanged)collection).PropertyChanged += suppressed;
        collection.CollectionChanged += collectionObserver;

        Assert.Throws<SentinelException>(() => collection.AddRange([4, 5]));
        Assert.SequenceEqual([0, 1, 2, 3, 4, 5], collection);
        Assert.SequenceEqual(
            ["before:Count", "throwing:Count", "after:Count", "before:Item[]", "throwing:Item[]"],
            entries);
        ((INotifyPropertyChanged)collection).PropertyChanged -= before;
        ((INotifyPropertyChanged)collection).PropertyChanged -= throwing;
        ((INotifyPropertyChanged)collection).PropertyChanged -= suppressed;
        collection.CollectionChanged -= collectionObserver;
        collection.RemoveRange(4, 2);
        Assert.SequenceEqual([0, 1, 2, 3], collection);
    }

    private static void ThrowingCollectionSubscriber()
    {
        var collection = NewCollection();
        var entries = new List<string>();
        PropertyChangedEventHandler propertyObserver = (_, args) => entries.Add($"property:{args.PropertyName}");
        NotifyCollectionChangedEventHandler before = (_, args) => entries.Add($"before:{args.Action}");
        NotifyCollectionChangedEventHandler throwing = (_, args) =>
        {
            entries.Add($"throwing:{args.Action}");
            throw new SentinelException();
        };
        NotifyCollectionChangedEventHandler suppressed = (_, args) => entries.Add($"after:{args.Action}");
        ((INotifyPropertyChanged)collection).PropertyChanged += propertyObserver;
        collection.CollectionChanged += before;
        collection.CollectionChanged += throwing;
        collection.CollectionChanged += suppressed;

        Assert.Throws<SentinelException>(() => collection.ReplaceRange(1, 2, [8, 9]));
        Assert.SequenceEqual([0, 8, 9, 3], collection);
        Assert.SequenceEqual(
            ["property:Item[]", "before:Replace", "throwing:Replace"],
            entries);
        ((INotifyPropertyChanged)collection).PropertyChanged -= propertyObserver;
        collection.CollectionChanged -= before;
        collection.CollectionChanged -= throwing;
        collection.CollectionChanged -= suppressed;
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
        var entries = new List<string>();
        PropertyChangedEventHandler propertyObserver = (_, args) => entries.Add($"property:{args.PropertyName}");
        NotifyCollectionChangedEventHandler handler = (_, args) =>
        {
            entries.Add($"collection:{args.Action}");
            throw new SentinelException();
        };
        ((INotifyPropertyChanged)collection).PropertyChanged += propertyObserver;
        collection.CollectionChanged += handler;

        Assert.Throws<SentinelException>(() => collection.UpdateTo(
            [3, 7, 0],
            options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular }));
        Assert.SequenceEqual([3, 0, 1, 2], collection);
        Assert.SequenceEqual(["property:Item[]", "collection:Move"], entries);
        _ = collection.ToSnapshot();
        ((INotifyPropertyChanged)collection).PropertyChanged -= propertyObserver;
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
            ["inherited Insert"] = static collection => collection.Insert(1, 10),
            ["inherited Remove"] = static collection => collection.Remove(1),
            ["inherited RemoveAt"] = static collection => collection.RemoveAt(0),
            ["inherited SetItem"] = static collection => collection[0] = 10,
            ["inherited Move"] = static collection => collection.Move(0, 1),
            ["inherited Clear"] = static collection => collection.Clear(),
            ["AddRange"] = static collection => collection.AddRange([10, 11]),
            ["InsertRange"] = static collection => collection.InsertRange(1, [10, 11]),
            ["RemoveRange"] = static collection => collection.RemoveRange(0, 1),
            ["ReplaceRange"] = static collection => collection.ReplaceRange(0, 1, [10]),
            ["MoveRange"] = static collection => collection.MoveRange(0, 1, 1),
            ["UpdateTo comparer"] = static collection => collection.UpdateTo([]),
            ["UpdateTo keyed"] = static collection => collection.UpdateTo([], static item => item),
        };

    private static Dictionary<string, Action<ObservableRangeCollection<int>, IEnumerable<int>>> SourceOperations() =>
        new(StringComparer.Ordinal)
        {
            ["AddRange"] = static (collection, source) => collection.AddRange(source),
            ["InsertRange"] = static (collection, source) => collection.InsertRange(1, source),
            ["ReplaceRange"] = static (collection, source) => collection.ReplaceRange(1, 2, source),
            ["UpdateTo comparer"] = static (collection, source) => collection.UpdateTo(source),
            ["UpdateTo keyed"] = static (collection, source) => collection.UpdateTo(source, static item => item),
        };

    private static ObservableRangeCollection<int> NewCollection() => new([0, 1, 2, 3]);

    private static IEnumerable<int> MutatingIterator(Action mutation)
    {
        mutation();
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

    private static void AssertUnchangedAndRecovered(
        ObservableRangeCollection<int> collection,
        TraceRecorder<int> trace,
        string context)
    {
        Assert.SequenceEqual([0, 1, 2, 3], collection, $"State changed during {context}.");
        Assert.Equal(0, trace.Entries.Count, $"Notifications escaped during {context}.");
        collection.Add(4);
        Assert.SequenceEqual([0, 1, 2, 3, 4], collection, $"Guard did not recover after {context}.");
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

    private sealed class ThrowingEqualsComparer<T> : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => throw new SentinelException();

        public int GetHashCode(T obj) => 0;
    }

    private sealed class ThrowingHashComparer<T> : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => EqualityComparer<T>.Default.Equals(x!, y!);

        public int GetHashCode(T obj) => throw new SentinelException();
    }

    private enum KeyComparerCallback
    {
        Equals,
        GetHashCode,
    }

    private sealed class CallbackKeyComparer(KeyComparerCallback callbackKind, Action callback) : IEqualityComparer<int>
    {
        public int CallbackCount { get; private set; }

        public bool Equals(int x, int y)
        {
            if (callbackKind == KeyComparerCallback.Equals)
            {
                CallbackCount++;
                callback();
            }

            return x == y;
        }

        public int GetHashCode(int obj)
        {
            if (callbackKind == KeyComparerCallback.GetHashCode)
            {
                CallbackCount++;
                callback();
            }

            return 0;
        }
    }

    private sealed class SentinelException : Exception;
}
