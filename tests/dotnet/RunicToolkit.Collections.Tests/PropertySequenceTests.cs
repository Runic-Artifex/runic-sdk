using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace RunicToolkit.Collections.Tests;

internal static class PropertySequenceTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("OR1 property sequences / range model and event shadow", RangeSequencesMatchModel),
        new("OR1 property sequences / seeded traces are deterministic", SeededTracesAreDeterministic),
        new("OR2 property pairs / comparer convergence replay and FIFO identity", ComparerPairsConverge),
        new("OR2 property pairs / keyed convergence replay and identity", KeyedPairsConverge),
        new("G2 property stress / wide range sequences match model", WideRangeSequencesMatchModel),
        new("G2 property stress / comparer FIFO covers reference null and value duplicates", ComparerDuplicateTypeMatrix),
        new("G2 property stress / keyed sizes order and churn preserve identity", KeyedSizeOrderChurnMatrix),
        new("G2 property stress / resolver identity and replacement model", ResolverIdentityReplacementMatrix),
        new("G2 property stress / reconciliation traces are deterministic", ReconciliationTracesAreDeterministic),
        new("G2 property stress / AVL edit planner adversarial orders", AvlPlannerAdversarialOrders),
        new("G2 value resolver regression / payload-insensitive equality still replaces", ValueResolverInstallsEqualityEquivalentPayload),
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

    private static void WideRangeSequencesMatchModel()
    {
        foreach (var mode in new[] { RangeNotificationMode.Range, RangeNotificationMode.Reset })
        {
            for (var seed = 1_200; seed < 1_232; seed++)
            {
                RunRangeSequence(
                    seed,
                    mode,
                    steps: 240,
                    maximumInitialCount: 96,
                    maximumBatchCount: 16,
                    minimumValue: -128,
                    maximumValue: 129);
            }
        }
    }

    private static void ComparerDuplicateTypeMatrix()
    {
        foreach (var mode in ReconciliationModes())
        {
            for (var seed = 2_000; seed < 2_024; seed++)
            {
                var random = new Random(seed);
                var current = MakeNullableDuplicateItems(random, random.Next(0, 72), "old");
                var incoming = MakeNullableDuplicateItems(random, random.Next(0, 72), "new");
                NullableIdentityItem?[] expected = NullableFifoExpected(current, incoming);
                var collection = new ObservableRangeCollection<NullableIdentityItem?>(current);
                var shadow = collection.ToList();
                collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);

                collection.UpdateTo(
                    incoming,
                    NullableIdentityValueComparer.Instance,
                    options: StressOptions(mode));

                AssertNullableIdentitySequence(expected, collection, $"nullable seed={seed}, mode={mode}");
                AssertNullableIdentitySequence(expected, shadow, $"nullable shadow seed={seed}, mode={mode}");

                int[] currentValues = Enumerable.Range(0, random.Next(0, 96))
                    .Select(_ => random.Next(-8, 9))
                    .ToArray();
                int[] targetValues = Enumerable.Range(0, random.Next(0, 96))
                    .Select(_ => random.Next(-8, 9))
                    .ToArray();
                var values = new ObservableRangeCollection<int>(currentValues);
                var valueShadow = values.ToList();
                values.CollectionChanged += (_, args) => EventReplay.Apply(valueShadow, values, args);

                values.UpdateTo(targetValues, options: StressOptions(mode));

                Assert.SequenceEqual(targetValues, values, $"value seed={seed}, mode={mode}");
                Assert.SequenceEqual(targetValues, valueShadow, $"value shadow seed={seed}, mode={mode}");
            }
        }
    }

    private static void KeyedSizeOrderChurnMatrix()
    {
        int[] sizes = [0, 1, 2, 7, 31, 64, 127, 257];
        foreach (var mode in ReconciliationModes())
        {
            foreach (int size in sizes)
            {
                for (var sample = 0; sample < 4; sample++)
                {
                    int seed = 3_000 + (size * 17) + sample;
                    var random = new Random(seed);
                    int[] currentKeys = Shuffled(Enumerable.Range(0, size), random);
                    int retainedCount = size == 0 ? 0 : random.Next(size + 1);
                    int addedCount = size == 0 ? sample : random.Next((size / 2) + 2);
                    int[] targetKeys = Shuffled(
                        currentKeys.Take(retainedCount).Concat(Enumerable.Range(size, addedCount)),
                        random);
                    var current = currentKeys
                        .Select(key => new KeyedIdentityItem(key, $"old-{seed}-{key}"))
                        .ToArray();
                    var incoming = targetKeys
                        .Select(key => new KeyedIdentityItem(key, $"new-{seed}-{key}"))
                        .ToArray();
                    var currentByKey = current.ToDictionary(static item => item.Key);
                    KeyedIdentityItem[] expected = incoming
                        .Select(item => currentByKey.TryGetValue(item.Key, out var existing) ? existing : item)
                        .ToArray();
                    var collection = new ObservableRangeCollection<KeyedIdentityItem>(current);
                    var shadow = collection.ToList();
                    collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);

                    collection.UpdateTo(
                        incoming,
                        static item => item.Key,
                        options: StressOptions(mode));

                    string context = $"keyed size={size}, sample={sample}, mode={mode}";
                    AssertIdentitySequence(expected, collection, context);
                    AssertIdentitySequence(expected, shadow, $"shadow {context}");
                    AssertIdentitySequence(expected, collection.ToSnapshot(), $"snapshot {context}");
                }
            }
        }
    }

    private static void ResolverIdentityReplacementMatrix()
    {
        foreach (var mode in ReconciliationModes())
        {
            for (var seed = 4_000; seed < 4_018; seed++)
            {
                var random = new Random(seed);
                int size = random.Next(1, 96);
                var current = Enumerable.Range(0, size)
                    .Select(key => new KeyedIdentityItem(key, $"old-{seed}-{key}"))
                    .ToArray();
                int[] targetKeys = Shuffled(
                    Enumerable.Range(0, size).Where(key => key % 5 != 0)
                        .Concat(Enumerable.Range(size, Math.Max(1, size / 5))),
                    random);
                var incoming = targetKeys
                    .Select(key => new KeyedIdentityItem(key, $"new-{seed}-{key}"))
                    .ToArray();
                var currentByKey = current.ToDictionary(static item => item.Key);
                var expected = new KeyedIdentityItem[incoming.Length];
                var expectedCalls = new List<int>();
                var replacements = new Dictionary<int, KeyedIdentityItem>();
                for (var index = 0; index < incoming.Length; index++)
                {
                    KeyedIdentityItem item = incoming[index];
                    if (!currentByKey.TryGetValue(item.Key, out var existing))
                    {
                        expected[index] = item;
                    }
                    else
                    {
                        expectedCalls.Add(item.Key);
                        expected[index] = (item.Key % 3) switch
                        {
                            0 => existing,
                            1 => item,
                            _ => replacements[item.Key] = new KeyedIdentityItem(item.Key, $"resolved-{seed}-{item.Key}"),
                        };
                    }
                }

                var actualCalls = new List<int>();
                var collection = new ObservableRangeCollection<KeyedIdentityItem>(current);
                var shadow = collection.ToList();
                collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);
                CollectionUpdateResult result = collection.UpdateTo(
                    incoming,
                    static item => item.Key,
                    resolveMatch: (existing, target) =>
                    {
                        actualCalls.Add(target.Key);
                        return (target.Key % 3) switch
                        {
                            0 => existing,
                            1 => target,
                            _ => replacements[target.Key],
                        };
                    },
                    options: StressOptions(mode));

                string context = $"resolver seed={seed}, mode={mode}";
                Assert.SequenceEqual(expectedCalls, actualCalls, $"Resolver call order mismatch: {context}");
                AssertIdentitySequence(expected, collection, context);
                AssertIdentitySequence(expected, shadow, $"shadow {context}");
                int expectedReplacements = expectedCalls.Count(key => key % 3 != 0);
                Assert.Equal(expectedReplacements, result.Replaced, $"Replacement count mismatch: {context}");
            }
        }
    }

    private static void ReconciliationTracesAreDeterministic()
    {
        foreach (var mode in ReconciliationModes())
        {
            for (var seed = 5_000; seed < 5_012; seed++)
            {
                List<string> first = RunKeyedReconciliationTrace(seed, mode, 72);
                List<string> second = RunKeyedReconciliationTrace(seed, mode, 72);
                Assert.SequenceEqual(first, second, $"Reconciliation trace mismatch: seed={seed}, mode={mode}");
            }
        }
    }

    private static void AvlPlannerAdversarialOrders()
    {
        foreach (int size in new[] { 3, 31, 127, 511, 2_047 })
        {
            var current = Enumerable.Range(0, size)
                .Select(key => new KeyedIdentityItem(key, $"old-{size}-{key}"))
                .ToArray();
            int[] retained = Enumerable.Range(0, size).Where(key => key % 4 != 0).Reverse().ToArray();
            int[] added = Enumerable.Range(size, Math.Max(1, size / 4)).ToArray();
            int[] targetKeys = Interleave(retained, added);
            var incoming = targetKeys
                .Select(key => new KeyedIdentityItem(key, $"new-{size}-{key}"))
                .ToArray();
            var currentByKey = current.ToDictionary(static item => item.Key);
            KeyedIdentityItem[] expected = incoming
                .Select(item => currentByKey.TryGetValue(item.Key, out var existing) ? existing : item)
                .ToArray();
            var collection = new ObservableRangeCollection<KeyedIdentityItem>(current);
            var shadow = collection.ToList();
            collection.CollectionChanged += (_, args) => EventReplay.Apply(shadow, collection, args);

            CollectionUpdateResult result = collection.UpdateTo(
                incoming,
                static item => item.Key,
                options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });

            string context = $"AVL adversarial size={size}";
            AssertIdentitySequence(expected, collection, context);
            AssertIdentitySequence(expected, shadow, $"shadow {context}");
            Assert.Equal(added.Length, result.Added, $"Added count mismatch: {context}");
            Assert.Equal(size - retained.Length, result.Removed, $"Removed count mismatch: {context}");
            Assert.True(result.Moved > 0, $"Expected moves: {context}");
            Assert.Equal(result.Added + result.Removed + result.Moved + result.Replaced, result.NotificationCount);
        }
    }

    private static void ValueResolverInstallsEqualityEquivalentPayload()
    {
        var cases = new[]
        {
            (Mode: UpdateNotificationMode.Granular, ExpectedReset: false, ExpectedNotifications: 3),
            (Mode: UpdateNotificationMode.Reset, ExpectedReset: true, ExpectedNotifications: 1),
            (Mode: UpdateNotificationMode.Auto, ExpectedReset: false, ExpectedNotifications: 3),
        };

        foreach (var testCase in cases)
        {
            PayloadInsensitiveValue[] current =
            [
                new(1, "old-one"),
                new(2, "old-two"),
                new(3, "old-three"),
            ];
            PayloadInsensitiveValue[] incoming =
            [
                new(1, "new-one"),
                new(2, "new-two"),
                new(3, "new-three"),
            ];
            var collection = new ObservableRangeCollection<PayloadInsensitiveValue>(current);
            var shadow = collection.ToList();
            var actions = new List<NotifyCollectionChangedAction>();
            collection.CollectionChanged += (_, args) =>
            {
                actions.Add(args.Action);
                EventReplay.Apply(shadow, collection, args);
            };

            CollectionUpdateResult result = collection.UpdateTo(
                incoming,
                PayloadInsensitiveKeyComparer.Instance,
                resolveMatch: static (_, target) => target,
                options: new CollectionUpdateOptions
                {
                    Notifications = testCase.Mode,
                    MaxGranularEvents = 3,
                    ResetRatioMinimumCount = int.MaxValue,
                });

            string context = $"value resolver mode={testCase.Mode}";
            Assert.SequenceEqual(
                incoming.Select(static item => item.Payload),
                collection.Select(static item => item.Payload),
                $"Resolver payload was not installed: {context}");
            Assert.SequenceEqual(
                incoming.Select(static item => item.Payload),
                shadow.Select(static item => item.Payload),
                $"Event replay payload mismatch: {context}");
            Assert.Equal(3, result.Replaced, $"Replacement count mismatch: {context}");
            Assert.True(result.Changed, $"Changed must reflect resolver replacements: {context}");
            Assert.Equal(testCase.ExpectedReset, result.UsedReset, $"Reset selection mismatch: {context}");
            Assert.Equal(
                testCase.ExpectedNotifications,
                result.NotificationCount,
                $"Result notification count mismatch: {context}");
            Assert.Equal(
                testCase.ExpectedNotifications,
                actions.Count,
                $"Observed notification count mismatch: {context}");
            Assert.True(
                actions.All(action => action == (testCase.ExpectedReset
                    ? NotifyCollectionChangedAction.Reset
                    : NotifyCollectionChangedAction.Replace)),
                $"Unexpected notification action: {context}");
        }
    }

    private static List<string> RunRangeSequence(
        int seed,
        RangeNotificationMode mode,
        int steps,
        int maximumInitialCount = 8,
        int maximumBatchCount = 5,
        int minimumValue = -3,
        int maximumValue = 6)
    {
        var random = new Random(seed);
        var initial = Enumerable.Range(0, random.Next(0, maximumInitialCount))
            .Select(_ => random.Next(minimumValue, maximumValue))
            .ToList();
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
                        var added = RandomItems(random, maximumBatchCount, minimumValue, maximumValue);
                        collection.AddRange(added);
                        model.AddRange(added);
                        break;
                    }
                case 1:
                    {
                        var inserted = RandomItems(random, maximumBatchCount, minimumValue, maximumValue);
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
                        var replacement = RandomItems(random, maximumBatchCount, minimumValue, maximumValue);
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
                        var value = random.Next(minimumValue, maximumValue);
                        collection.Add(value);
                        model.Add(value);
                    }
                    else
                    {
                        var index = random.Next(model.Count);
                        var value = random.Next(minimumValue, maximumValue);
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

    private static List<int> RandomItems(
        Random random,
        int maximumBatchCount,
        int minimumValue,
        int maximumValue) =>
        Enumerable.Range(0, random.Next(0, maximumBatchCount))
            .Select(_ => random.Next(minimumValue, maximumValue))
            .ToList();

    private static (int Index, int Count) RandomRange(Random random, int count)
    {
        var index = random.Next(count + 1);
        return (index, random.Next(count - index + 1));
    }

    private static IdentityItem[] MakeDuplicateItems(Random random, int count, string prefix) =>
        Enumerable.Range(0, count)
            .Select(index => new IdentityItem(random.Next(0, 6), $"{prefix}-{index}"))
            .ToArray();

    private static NullableIdentityItem?[] MakeNullableDuplicateItems(Random random, int count, string prefix) =>
        Enumerable.Range(0, count)
            .Select(index => index % 11 == 0 || random.Next(7) == 0
                ? null
                : new NullableIdentityItem(random.Next(0, 10), $"{prefix}-{index}"))
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

    private static NullableIdentityItem?[] NullableFifoExpected(
        IReadOnlyList<NullableIdentityItem?> current,
        IReadOnlyList<NullableIdentityItem?> incoming)
    {
        var used = new bool[current.Count];
        var result = new NullableIdentityItem?[incoming.Count];
        for (var targetIndex = 0; targetIndex < incoming.Count; targetIndex++)
        {
            result[targetIndex] = incoming[targetIndex];
            for (var currentIndex = 0; currentIndex < current.Count; currentIndex++)
            {
                if (!used[currentIndex]
                    && NullableIdentityValueComparer.Instance.Equals(current[currentIndex], incoming[targetIndex]))
                {
                    used[currentIndex] = true;
                    result[targetIndex] = current[currentIndex];
                    break;
                }
            }
        }

        return result;
    }

    private static UpdateNotificationMode[] ReconciliationModes() =>
        [UpdateNotificationMode.Granular, UpdateNotificationMode.Reset, UpdateNotificationMode.Auto];

    private static CollectionUpdateOptions StressOptions(UpdateNotificationMode mode) =>
        new()
        {
            Notifications = mode,
            MaxGranularEvents = 12,
            ResetRatioMinimumCount = 16,
            ResetChangeRatio = 0.3,
        };

    private static int[] Shuffled(IEnumerable<int> source, Random random)
    {
        int[] result = source.ToArray();
        for (var index = result.Length - 1; index > 0; index--)
        {
            int other = random.Next(index + 1);
            (result[index], result[other]) = (result[other], result[index]);
        }

        return result;
    }

    private static int[] Interleave(int[] first, int[] second)
    {
        var result = new int[first.Length + second.Length];
        var firstIndex = 0;
        var secondIndex = 0;
        for (var index = 0; index < result.Length; index++)
        {
            bool takeSecond = secondIndex < second.Length && (firstIndex >= first.Length || index % 3 == 1);
            result[index] = takeSecond ? second[secondIndex++] : first[firstIndex++];
        }

        return result;
    }

    private static List<string> RunKeyedReconciliationTrace(
        int seed,
        UpdateNotificationMode mode,
        int size)
    {
        var random = new Random(seed);
        int[] currentKeys = Shuffled(Enumerable.Range(0, size), random);
        int[] targetKeys = Shuffled(
            currentKeys.Where(key => key % 4 != 0).Take((size * 2) / 3)
                .Concat(Enumerable.Range(size, size / 3)),
            random);
        var current = currentKeys
            .Select(key => new KeyedIdentityItem(key, $"old-{seed}-{key}"))
            .ToArray();
        var incoming = targetKeys
            .Select(key => new KeyedIdentityItem(key, $"new-{seed}-{key}"))
            .ToArray();
        var currentByKey = current.ToDictionary(static item => item.Key);
        KeyedIdentityItem[] expected = incoming
            .Select(item => currentByKey.TryGetValue(item.Key, out var existing) ? existing : item)
            .ToArray();
        var collection = new ObservableRangeCollection<KeyedIdentityItem>(current);
        var shadow = collection.ToList();
        var trace = new List<string>();
        collection.CollectionChanged += (_, args) =>
        {
            EventReplay.Apply(shadow, collection, args);
            trace.Add(
                $"{args.Action}:{args.OldStartingIndex}:{args.OldItems?.Count ?? 0}:" +
                $"{args.NewStartingIndex}:{args.NewItems?.Count ?? 0}:" +
                $"[{string.Join(',', collection.Select(static item => item.Identity))}]");
        };

        CollectionUpdateResult result = collection.UpdateTo(
            incoming,
            static item => item.Key,
            options: StressOptions(mode));
        trace.Add($"result:{result}");
        AssertIdentitySequence(expected, collection, $"Trace identity mismatch: seed={seed}, mode={mode}");
        Assert.SequenceEqual(collection, shadow, $"Trace replay mismatch: seed={seed}, mode={mode}");
        return trace;
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

    private static void AssertNullableIdentitySequence(
        IReadOnlyList<NullableIdentityItem?> expected,
        IReadOnlyList<NullableIdentityItem?> actual,
        string context)
    {
        Assert.Equal(expected.Count, actual.Count, context);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index], actual[index], $"Identity mismatch at {index}: {context}");
        }
    }

    private sealed record IdentityItem(int Value, string Identity);

    private sealed record NullableIdentityItem(int Value, string Identity);

    private readonly struct PayloadInsensitiveValue(int key, string payload) : IEquatable<PayloadInsensitiveValue>
    {
        public int Key { get; } = key;

        public string Payload { get; } = payload;

        public bool Equals(PayloadInsensitiveValue other) => Key == other.Key;

        public override bool Equals(object? obj) => obj is PayloadInsensitiveValue other && Equals(other);

        public override int GetHashCode() => Key;

        public override string ToString() => $"{Key}:{Payload}";
    }

    private sealed class IdentityValueComparer : IEqualityComparer<IdentityItem>
    {
        public static IdentityValueComparer Instance { get; } = new();

        public bool Equals(IdentityItem? x, IdentityItem? y) => x?.Value == y?.Value;

        public int GetHashCode(IdentityItem obj) => obj.Value;
    }

    private sealed class NullableIdentityValueComparer : IEqualityComparer<NullableIdentityItem?>
    {
        public static NullableIdentityValueComparer Instance { get; } = new();

        public bool Equals(NullableIdentityItem? x, NullableIdentityItem? y) =>
            x is null ? y is null : y is not null && x.Value == y.Value;

        public int GetHashCode(NullableIdentityItem? obj) => obj?.Value ?? 0;
    }

    private sealed class PayloadInsensitiveKeyComparer : IEqualityComparer<PayloadInsensitiveValue>
    {
        public static PayloadInsensitiveKeyComparer Instance { get; } = new();

        public bool Equals(PayloadInsensitiveValue x, PayloadInsensitiveValue y) => x.Key == y.Key;

        public int GetHashCode(PayloadInsensitiveValue obj) => obj.Key;
    }

    private sealed record KeyedIdentityItem(int Key, string Identity);
}
