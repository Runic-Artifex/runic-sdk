using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using WebUIToolkit.Collections;

namespace WebUIToolkit.Collections.Benchmarks;

internal static class Program
{
    private static readonly int[] FullSizes = [10, 100, 1_000, 10_000];
    private static readonly int[] QuickSizes = [10, 100, 1_000];
    private static readonly int[] ChurnPercentages = [1, 10, 50];

    public static int Main(string[] args)
    {
        if (args.Length > 1 || (args.Length == 1 && args[0] is not "--quick" and not "--full"))
        {
            Console.Error.WriteLine("Usage: WebUIToolkit.Collections.Benchmarks [--quick|--full]");
            return 2;
        }

        bool quick = args.Length == 0 || args[0] == "--quick";
        int[] sizes = quick ? QuickSizes : FullSizes;
        WarmUp();
        Console.WriteLine("scenario,policy,size,churn_percent,repetitions,elapsed_us,allocated_bytes,events,retained_identities");
        foreach (int size in sizes)
        {
            int repetitions = quick ? 3 : GetFullRepetitions(size);
            RunRangeMatrix(size, repetitions);
            foreach (int churn in ChurnPercentages)
            {
                RunReconciliationMatrix(size, churn, repetitions);
            }
        }

        return 0;
    }

    private static int GetFullRepetitions(int size) => size switch
    {
        <= 10 => 12,
        <= 100 => 8,
        <= 1_000 => 3,
        _ => 1,
    };

    private static void WarmUp()
    {
        var range = new ObservableRangeCollection<Item>(CreateItems(16, 0));
        range.AddRange(CreateItems(2, 100));
        range.RemoveRange(0, 2);
        range.ReplaceRange(0, 2, CreateItems(2, 200));
        range.MoveRange(0, 2, range.Count - 2);

        Item[] keyedTarget = CreateKeyedTarget(range.ToSnapshot(), 10);
        range.UpdateTo(
            keyedTarget,
            static item => item.Key,
            resolveMatch: static (existing, _) => existing,
            options: CreateUpdateOptions(UpdateNotificationMode.Granular));

        Item[] duplicateTarget = CreateDuplicateTarget(range.ToSnapshot(), 10);
        range.UpdateTo(
            duplicateTarget,
            new GroupComparer(),
            resolveMatch: static (existing, _) => existing,
            options: CreateUpdateOptions(UpdateNotificationMode.Reset));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void RunRangeMatrix(int size, int repetitions)
    {
        foreach (RangeNotificationMode mode in new[] { RangeNotificationMode.Range, RangeNotificationMode.Reset })
        {
            MeasureRange("range-append", mode, size, repetitions, static (collection, count) =>
                collection.AddRange(CreateItems(count, 1_000_000)));
            MeasureRange("range-remove", mode, size, repetitions, static (collection, count) =>
                collection.RemoveRange((collection.Count - count) / 2, count));
            MeasureRange("range-replace", mode, size, repetitions, static (collection, count) =>
                collection.ReplaceRange((collection.Count - count) / 2, count, CreateItems(count, 2_000_000)));
            MeasureRange("range-move", mode, size, repetitions, static (collection, count) =>
                collection.MoveRange(0, count, collection.Count - count));
        }
    }

    private static void MeasureRange(
        string scenario,
        RangeNotificationMode mode,
        int size,
        int repetitions,
        Action<ObservableRangeCollection<Item>, int> operation)
    {
        long elapsedTicks = 0;
        long allocatedBytes = 0;
        int events = 0;
        int retained = 0;
        int rangeCount = Math.Max(1, size / 10);

        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            Item[] initial = CreateItems(size, 0);
            var collection = new ObservableRangeCollection<Item>(
                initial,
                new ObservableRangeCollectionOptions { RangeNotifications = mode });
            collection.CollectionChanged += (_, _) => events++;

            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long timestamp = Stopwatch.GetTimestamp();
            operation(collection, rangeCount);
            elapsedTicks += Stopwatch.GetTimestamp() - timestamp;
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            retained += CountRetained(initial, collection);
        }

        WriteResult(scenario, mode.ToString(), size, null, repetitions, elapsedTicks, allocatedBytes, events, retained);
    }

    private static void RunReconciliationMatrix(int size, int churnPercent, int repetitions)
    {
        foreach (UpdateNotificationMode mode in new[] { UpdateNotificationMode.Granular, UpdateNotificationMode.Reset })
        {
            MeasureKeyed(size, churnPercent, repetitions, mode);
            MeasureDuplicateComparer(size, churnPercent, repetitions, mode);
        }
    }

    private static void MeasureKeyed(int size, int churnPercent, int repetitions, UpdateNotificationMode mode)
    {
        long elapsedTicks = 0;
        long allocatedBytes = 0;
        int events = 0;
        int retained = 0;

        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            Item[] initial = CreateItems(size, 0);
            Item[] target = CreateKeyedTarget(initial, churnPercent);
            var collection = new ObservableRangeCollection<Item>(initial);
            collection.CollectionChanged += (_, _) => events++;

            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long timestamp = Stopwatch.GetTimestamp();
            collection.UpdateTo(
                target,
                static item => item.Key,
                resolveMatch: static (existing, _) => existing,
                options: CreateUpdateOptions(mode));
            elapsedTicks += Stopwatch.GetTimestamp() - timestamp;
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            EnsureKeys(collection, target);
            retained += CountRetained(initial, collection);
        }

        WriteResult("reconcile-keyed", mode.ToString(), size, churnPercent, repetitions, elapsedTicks, allocatedBytes, events, retained);
    }

    private static void MeasureDuplicateComparer(int size, int churnPercent, int repetitions, UpdateNotificationMode mode)
    {
        long elapsedTicks = 0;
        long allocatedBytes = 0;
        int events = 0;
        int retained = 0;
        var comparer = new GroupComparer();

        for (int iteration = 0; iteration < repetitions; iteration++)
        {
            Item[] initial = CreateItems(size, 0);
            Item[] target = CreateDuplicateTarget(initial, churnPercent);
            var collection = new ObservableRangeCollection<Item>(initial);
            collection.CollectionChanged += (_, _) => events++;

            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long timestamp = Stopwatch.GetTimestamp();
            collection.UpdateTo(
                target,
                comparer,
                resolveMatch: static (existing, _) => existing,
                options: CreateUpdateOptions(mode));
            elapsedTicks += Stopwatch.GetTimestamp() - timestamp;
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocationStart;

            EnsureGroups(collection, target);
            retained += CountRetained(initial, collection);
        }

        WriteResult("reconcile-duplicate", mode.ToString(), size, churnPercent, repetitions, elapsedTicks, allocatedBytes, events, retained);
    }

    private static CollectionUpdateOptions CreateUpdateOptions(UpdateNotificationMode mode) => new()
    {
        Notifications = mode,
        MaxGranularEvents = int.MaxValue,
        ResetRatioMinimumCount = int.MaxValue,
        ResetChangeRatio = 1.0,
    };

    private static Item[] CreateItems(int count, int keyOffset)
    {
        var items = new Item[count];
        for (int index = 0; index < items.Length; index++)
        {
            items[index] = new Item(keyOffset + index, index % 8);
        }

        return items;
    }

    private static Item[] CreateKeyedTarget(Item[] initial, int churnPercent)
    {
        int churnCount = Math.Max(1, checked(initial.Length * churnPercent / 100));
        var target = new Item[initial.Length];
        int retainedCount = initial.Length - churnCount;
        for (int index = 0; index < retainedCount; index++)
        {
            Item source = initial[index];
            target[index] = new Item(source.Key, source.Group);
        }

        for (int index = retainedCount; index < target.Length; index++)
        {
            target[index] = new Item(10_000_000 + index, 100_000 + index);
        }

        return target;
    }

    private static Item[] CreateDuplicateTarget(Item[] initial, int churnPercent)
    {
        int churnCount = Math.Max(1, checked(initial.Length * churnPercent / 100));
        var target = new Item[initial.Length];
        int retainedCount = initial.Length - churnCount;
        for (int index = 0; index < retainedCount; index++)
        {
            Item source = initial[index];
            target[index] = new Item(20_000_000 + index, source.Group);
        }

        for (int index = retainedCount; index < target.Length; index++)
        {
            target[index] = new Item(30_000_000 + index, 200_000 + index);
        }

        return target;
    }

    private static int CountRetained(IEnumerable<Item> initial, IEnumerable<Item> current)
    {
        var identities = new HashSet<Item>(initial, ReferenceEqualityComparer.Instance);
        return current.Count(identities.Contains);
    }

    private static void EnsureKeys(IReadOnlyList<Item> actual, IReadOnlyList<Item> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException("Keyed reconciliation produced an invalid count.");
        }

        for (int index = 0; index < actual.Count; index++)
        {
            if (actual[index].Key != expected[index].Key)
            {
                throw new InvalidOperationException("Keyed reconciliation did not converge.");
            }
        }
    }

    private static void EnsureGroups(IReadOnlyList<Item> actual, IReadOnlyList<Item> expected)
    {
        if (actual.Count != expected.Count)
        {
            throw new InvalidOperationException("Comparer reconciliation produced an invalid count.");
        }

        for (int index = 0; index < actual.Count; index++)
        {
            if (actual[index].Group != expected[index].Group)
            {
                throw new InvalidOperationException("Comparer reconciliation did not converge.");
            }
        }
    }

    private static void WriteResult(
        string scenario,
        string policy,
        int size,
        int? churnPercent,
        int repetitions,
        long elapsedTicks,
        long allocatedBytes,
        int events,
        int retained)
    {
        double elapsedMicroseconds = elapsedTicks * 1_000_000d / Stopwatch.Frequency;
        Console.WriteLine(string.Join(",",
            scenario,
            policy,
            size.ToString(CultureInfo.InvariantCulture),
            churnPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            repetitions.ToString(CultureInfo.InvariantCulture),
            elapsedMicroseconds.ToString("F1", CultureInfo.InvariantCulture),
            allocatedBytes.ToString(CultureInfo.InvariantCulture),
            events.ToString(CultureInfo.InvariantCulture),
            retained.ToString(CultureInfo.InvariantCulture)));
    }

    private sealed class Item(int key, int group)
    {
        public int Key { get; } = key;

        public int Group { get; } = group;
    }

    private sealed class GroupComparer : IEqualityComparer<Item>
    {
        public bool Equals(Item? x, Item? y) => x?.Group == y?.Group;

        public int GetHashCode(Item obj) => obj.Group;
    }
}
