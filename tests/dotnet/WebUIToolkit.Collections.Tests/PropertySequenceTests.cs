using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace WebUIToolkit.Collections.Tests;

internal static class PropertySequenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("OR1 property sequences / range model and event shadow", RangeSequencesMatchModel),
        new("OR1 property sequences / seeded traces are deterministic", SeededTracesAreDeterministic),
        new("OR2 property pairs / comparer convergence replay and FIFO identity", ComparerPairsConverge),
        new("OR2 property pairs / keyed convergence replay and identity", KeyedPairsConverge),
    ];

    private static void RangeSequencesMatchModel()
    {
        foreach (var mode in new[] { RangeNotificationMode.Range, RangeNotificationMode.Reset })
        {
            for (var seed = 100; seed < 112; seed++)
            {
                RunRangeSequence(seed, mode, 80);
            }
        }
    }

    private static void SeededTracesAreDeterministic()
    {
        var first = RunRangeSequence(8_675_309, RangeNotificationMode.Range, 120);
        var second = RunRangeSequence(8_675_309, RangeNotificationMode.Range, 120);
        Assert.SequenceEqual(first, second);
    }

    private static void ComparerPairsConverge()
    {
        foreach (var mode in new[] { UpdateNotificationMode.Granular, UpdateNotificationMode.Reset, UpdateNotificationMode.Auto })
        {
            for (var seed = 400; seed < 430; seed++)
            {
                var random = new Random(seed);
                var current = MakeDuplicateItems(random, random.Next(0, 18), "old");
                var incoming = MakeDuplicateItems(random, random.Next(0, 18), "new");
                var expected = FifoExpected(current, incoming);
                var collection = new ObservableRangeCollection<IdentityItem>(current);
                var shadow = collection.ToList();
                collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);

                collection.UpdateTo(
                    incoming,
                    IdentityValueComparer.Instance,
                    options: new CollectionUpdateOptions
                    {
                        Notifications = mode,
                        MaxGranularEvents = 4,
                        ResetRatioMinimumCount = 5,
                        ResetChangeRatio = 0.4,
                    });

                AssertIdentitySequence(expected, collection, $"seed={seed}, mode={mode}");
                AssertIdentitySequence(expected, shadow, $"shadow seed={seed}, mode={mode}");
                AssertIdentitySequence(expected, collection.ToSnapshot(), $"snapshot seed={seed}, mode={mode}");
            }
        }
    }

    private static void KeyedPairsConverge()
    {
        foreach (var mode in new[] { UpdateNotificationMode.Granular, UpdateNotificationMode.Reset, UpdateNotificationMode.Auto })
        {
            for (var seed = 700; seed < 720; seed++)
            {
                var random = new Random(seed);
                var keys = Enumerable.Range(0, 20).OrderBy(_ => random.Next()).ToArray();
                var oldKeys = keys.Take(random.Next(0, 15)).ToArray();
                var targetKeys = keys.OrderBy(_ => random.Next()).Take(random.Next(0, 15)).ToArray();
                var current = oldKeys.Select(key => new KeyedIdentityItem(key, $"old-{key}")).ToArray();
                var incoming = targetKeys.Select(key => new KeyedIdentityItem(key, $"new-{key}")).ToArray();
                var currentByKey = current.ToDictionary(static item => item.Key);
                var expected = incoming
                    .Select(item => currentByKey.TryGetValue(item.Key, out var existing) ? existing : item)
                    .ToArray();
                var collection = new ObservableRangeCollection<KeyedIdentityItem>(current);
                var shadow = collection.ToList();
                collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);

                collection.UpdateTo(
                    incoming,
                    static item => item.Key,
                    options: new CollectionUpdateOptions
                    {
                        Notifications = mode,
                        MaxGranularEvents = 4,
                        ResetRatioMinimumCount = 5,
                        ResetChangeRatio = 0.4,
                    });

                AssertIdentitySequence(expected, collection, $"keyed seed={seed}, mode={mode}");
                AssertIdentitySequence(expected, shadow, $"keyed shadow seed={seed}, mode={mode}");
                AssertIdentitySequence(expected, collection.ToSnapshot(), $"keyed snapshot seed={seed}, mode={mode}");
            }
        }
    }

    private static List<string> RunRangeSequence(int seed, RangeNotificationMode mode, int steps)
    {
        var random = new Random(seed);
        var initial = Enumerable.Range(0, random.Next(0, 8)).Select(_ => random.Next(-3, 6)).ToList();
        var model = initial.ToList();
        var collection = new ObservableRangeCollection<int>(
            initial,
            new ObservableRangeCollectionOptions { RangeNotifications = mode });
        var shadow = initial.ToList();
        var eventTrace = new List<string>();
        collection.CollectionChanged += (_, args) =>
        {
            ValidateEventIndices(shadow.Count, args);
            EventReplay.Apply(shadow, collection, args);
            eventTrace.Add(
                $"{args.Action}:{args.OldStartingIndex}:{args.OldItems?.Count ?? 0}:" +
                $"{args.NewStartingIndex}:{args.NewItems?.Count ?? 0}:[{string.Join(",", collection)}]");
        };

        for (var step = 0; step < steps; step++)
        {
            switch (random.Next(8))
            {
                case 0:
                    {
                        var added = RandomItems(random);
                        collection.AddRange(added);
                        model.AddRange(added);
                        break;
                    }
                case 1:
                    {
                        var inserted = RandomItems(random);
                        var index = random.Next(model.Count + 1);
                        collection.InsertRange(index, inserted);
                        model.InsertRange(index, inserted);
                        break;
                    }
                case 2:
                    {
                        var (index, count) = RandomRange(random, model.Count);
                        collection.RemoveRange(index, count);
                        model.RemoveRange(index, count);
                        break;
                    }
                case 3:
                    {
                        var (index, count) = RandomRange(random, model.Count);
                        var replacement = RandomItems(random);
                        collection.ReplaceRange(index, count, replacement);
                        model.RemoveRange(index, count);
                        model.InsertRange(index, replacement);
                        break;
                    }
                case 4:
                    {
                        var (oldIndex, count) = RandomRange(random, model.Count);
                        var newIndex = random.Next(model.Count - count + 1);
                        collection.MoveRange(oldIndex, count, newIndex);
                        if (count != 0 && oldIndex != newIndex)
                        {
                            var moved = model.GetRange(oldIndex, count);
                            model.RemoveRange(oldIndex, count);
                            model.InsertRange(newIndex, moved);
                        }

                        break;
                    }
                case 5:
                    if (model.Count == 0)
                    {
                        var value = random.Next(-3, 6);
                        collection.Add(value);
                        model.Add(value);
                    }
                    else
                    {
                        var index = random.Next(model.Count);
                        var value = random.Next(-3, 6);
                        collection[index] = value;
                        model[index] = value;
                    }

                    break;
                case 6:
                    if (model.Count == 0)
                    {
                        collection.Clear();
                    }
                    else
                    {
                        var index = random.Next(model.Count);
                        collection.RemoveAt(index);
                        model.RemoveAt(index);
                    }

                    break;
                default:
                    if (model.Count > 1)
                    {
                        var oldIndex = random.Next(model.Count);
                        var newIndex = random.Next(model.Count);
                        collection.Move(oldIndex, newIndex);
                        var item = model[oldIndex];
                        model.RemoveAt(oldIndex);
                        model.Insert(newIndex, item);
                    }

                    break;
            }

            Assert.SequenceEqual(model, collection, $"Collection mismatch at seed={seed}, step={step}, mode={mode}.");
            Assert.SequenceEqual(model, shadow, $"Shadow mismatch at seed={seed}, step={step}, mode={mode}.");
            Assert.SequenceEqual(model, collection.ToSnapshot(), $"Snapshot mismatch at seed={seed}, step={step}, mode={mode}.");
        }

        return eventTrace;
    }

    private static void ValidateEventIndices(int oldCount, NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Assert.True(args.NewStartingIndex >= 0 && args.NewStartingIndex <= oldCount);
                break;
            case NotifyCollectionChangedAction.Remove:
                Assert.True(args.OldStartingIndex >= 0 && args.OldStartingIndex + args.OldItems!.Count <= oldCount);
                break;
            case NotifyCollectionChangedAction.Replace:
                Assert.True(args.OldStartingIndex >= 0 && args.OldStartingIndex + args.OldItems!.Count <= oldCount);
                Assert.Equal(args.OldStartingIndex, args.NewStartingIndex);
                break;
            case NotifyCollectionChangedAction.Move:
                Assert.True(args.OldStartingIndex >= 0 && args.OldStartingIndex + args.OldItems!.Count <= oldCount);
                Assert.True(args.NewStartingIndex >= 0 && args.NewStartingIndex <= oldCount - args.OldItems!.Count);
                break;
            case NotifyCollectionChangedAction.Reset:
                Assert.Equal(-1, args.OldStartingIndex);
                Assert.Equal(-1, args.NewStartingIndex);
                break;
            default:
                throw new AssertionException($"Unexpected event action {args.Action}.");
        }
    }

    private static List<int> RandomItems(Random random) =>
        Enumerable.Range(0, random.Next(0, 5)).Select(_ => random.Next(-3, 6)).ToList();

    private static (int Index, int Count) RandomRange(Random random, int count)
    {
        var index = random.Next(count + 1);
        return (index, random.Next(count - index + 1));
    }

    private static IdentityItem[] MakeDuplicateItems(Random random, int count, string prefix) =>
        Enumerable.Range(0, count)
            .Select(index => new IdentityItem(random.Next(0, 6), $"{prefix}-{index}"))
            .ToArray();

    private static IdentityItem[] FifoExpected(
        IReadOnlyList<IdentityItem> current,
        IReadOnlyList<IdentityItem> incoming)
    {
        var used = new bool[current.Count];
        var result = new IdentityItem[incoming.Count];
        for (var targetIndex = 0; targetIndex < incoming.Count; targetIndex++)
        {
            result[targetIndex] = incoming[targetIndex];
            for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
            {
                if (!used[currentIndex] && current[currentIndex].Value == incoming[targetIndex].Value)
                {
                    used[currentIndex] = true;
                    result[targetIndex] = current[currentIndex];
                    break;
                }
            }
        }

        return result;
    }

    private static void AssertIdentitySequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string context)
        where T : class
    {
        Assert.Equal(expected.Count, actual.Count, context);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index], actual[index], $"Identity mismatch at {index}: {context}");
        }
    }

    private sealed record IdentityItem(int Value, string Identity);

    private sealed class IdentityValueComparer : IEqualityComparer<IdentityItem>
    {
        public static IdentityValueComparer Instance { get; } = new();

        public bool Equals(IdentityItem? x, IdentityItem? y) => x?.Value == y?.Value;

        public int GetHashCode(IdentityItem obj) => obj.Value;
    }

    private sealed record KeyedIdentityItem(int Key, string Identity);
}
