using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using RunicToolkit.Collections;

namespace RunicToolkit.Collections.Benchmarks;

internal static class Program
{
    private const int WarmUpPasses = 3;

    private static readonly int[] FullSizes = [10, 100, 1_000, 10_000];
    private static readonly int[] QuickSizes = [10, 100, 1_000];
    private static readonly int[] ChurnPercentages = [1, 10, 50];

    public static int Main(string[] args)
    {
        if (!TryParseMode(args, out ExecutionMode mode))
        {
            Console.Error.WriteLine("Usage: RunicToolkit.Collections.Benchmarks [--quick|--full|--gate]");
            return 2;
        }

        WarmUp();
        int[] sizes = mode == ExecutionMode.Quick ? QuickSizes : FullSizes;
        var results = new List<BenchmarkResult>(sizes.Length * 20);

        Console.WriteLine(BenchmarkResult.CsvHeader);
        foreach (int size in sizes)
        {
            int repetitions = mode switch
            {
                ExecutionMode.Quick => 3,
                ExecutionMode.Full => GetFullRepetitions(size),
                _ => 1,
            };

            RunRangeMatrix(size, repetitions, results);
            foreach (int churn in ChurnPercentages)
            {
                RunReconciliationMatrix(size, churn, repetitions, results);
            }
        }

        if (mode != ExecutionMode.Gate)
        {
            return 0;
        }

        return EvaluateGate(results);
    }

    private static bool TryParseMode(string[] args, out ExecutionMode mode)
    {
        mode = ExecutionMode.Quick;
        if (args.Length == 0 || (args.Length == 1 && args[0] == "--quick"))
        {
            return true;
        }

        if (args.Length == 1 && args[0] == "--full")
        {
            mode = ExecutionMode.Full;
            return true;
        }

        if (args.Length == 1 && args[0] == "--gate")
        {
            mode = ExecutionMode.Gate;
            return true;
        }

        return false;
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
        for (int pass = 0; pass < WarmUpPasses; pass++)
        {
            foreach (RangeNotificationMode notificationMode in new[]
                     {
                         RangeNotificationMode.Range,
                         RangeNotificationMode.Reset,
                     })
            {
                var range = new ObservableRangeCollection<Item>(
                    CreateItems(32, pass * 1_000),
                    new ObservableRangeCollectionOptions { RangeNotifications = notificationMode });
                range.CollectionChanged += CountNotification;
                range.AddRange(CreateItems(4, 100_000));
                range.RemoveRange(2, 4);
                range.ReplaceRange(2, 4, CreateItems(4, 200_000));
                range.MoveRange(0, 4, range.Count - 4);
            }

            foreach (UpdateNotificationMode notificationMode in new[]
                     {
                         UpdateNotificationMode.Auto,
                         UpdateNotificationMode.Granular,
                         UpdateNotificationMode.Reset,
                     })
            {
                var keyed = new ObservableRangeCollection<Item>(CreateItems(32, 0));
                keyed.CollectionChanged += CountNotification;
                keyed.UpdateTo(
                    CreateKeyedTarget(keyed.ToSnapshot(), 10),
                    static item => item.Key,
                    resolveMatch: static (existing, _) => existing,
                    options: CreateUpdateOptions(notificationMode));

                var duplicate = new ObservableRangeCollection<Item>(CreateItems(32, 0));
                duplicate.CollectionChanged += CountNotification;
                duplicate.UpdateTo(
                    CreateDuplicateTarget(duplicate.ToSnapshot(), 10),
                    new GroupComparer(),
                    resolveMatch: static (existing, _) => existing,
                    options: CreateUpdateOptions(notificationMode));
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void CountNotification(object? sender, NotifyCollectionChangedEventArgs args)
    {
    }

    private static void RunRangeMatrix(int size, int repetitions, ICollection<BenchmarkResult> results)
    {
        foreach (RangeNotificationMode mode in new[] { RangeNotificationMode.Range, RangeNotificationMode.Reset })
        {
            AddResult(results, MeasureRange("range-append", mode, size, repetitions, static (collection, count) =>
                collection.AddRange(CreateItems(count, 1_000_000))));
            AddResult(results, MeasureRange("range-remove", mode, size, repetitions, static (collection, count) =>
                collection.RemoveRange((collection.Count - count) / 2, count)));
            AddResult(results, MeasureRange("range-replace", mode, size, repetitions, static (collection, count) =>
                collection.ReplaceRange((collection.Count - count) / 2, count, CreateItems(count, 2_000_000))));
            AddResult(results, MeasureRange("range-move", mode, size, repetitions, static (collection, count) =>
                collection.MoveRange(0, count, collection.Count - count)));
        }
    }

    private static BenchmarkResult MeasureRange(
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

        return new BenchmarkResult(scenario, mode.ToString(), size, null, repetitions, elapsedTicks, allocatedBytes, events, retained);
    }

    private static void RunReconciliationMatrix(
        int size,
        int churnPercent,
        int repetitions,
        ICollection<BenchmarkResult> results)
    {
        foreach (UpdateNotificationMode mode in new[] { UpdateNotificationMode.Granular, UpdateNotificationMode.Reset })
        {
            AddResult(results, MeasureKeyed(size, churnPercent, repetitions, mode));
            AddResult(results, MeasureDuplicateComparer(size, churnPercent, repetitions, mode));
        }
    }

    private static BenchmarkResult MeasureKeyed(int size, int churnPercent, int repetitions, UpdateNotificationMode mode)
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

        return new BenchmarkResult(
            "reconcile-keyed",
            mode.ToString(),
            size,
            churnPercent,
            repetitions,
            elapsedTicks,
            allocatedBytes,
            events,
            retained);
    }

    private static BenchmarkResult MeasureDuplicateComparer(
        int size,
        int churnPercent,
        int repetitions,
        UpdateNotificationMode mode)
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

        return new BenchmarkResult(
            "reconcile-duplicate",
            mode.ToString(),
            size,
            churnPercent,
            repetitions,
            elapsedTicks,
            allocatedBytes,
            events,
            retained);
    }

    private static void AddResult(ICollection<BenchmarkResult> results, BenchmarkResult result)
    {
        results.Add(result);
        Console.WriteLine(result.ToCsv());
    }

    private static int EvaluateGate(IReadOnlyCollection<BenchmarkResult> results)
    {
        var failures = new List<string>();
        foreach (BenchmarkResult result in results)
        {
            int expectedEvents = GetExpectedEvents(result);
            if (result.Events != expectedEvents)
            {
                failures.Add($"{result.Identity}: events {result.Events}, expected {expectedEvents}");
            }

            int expectedRetained = GetExpectedRetainedIdentities(result);
            if (result.RetainedIdentities != expectedRetained)
            {
                failures.Add($"{result.Identity}: retained identities {result.RetainedIdentities}, expected {expectedRetained}");
            }

            long allocationCeiling = checked(GetAllocationCeilingPerOperation(result) * result.Repetitions);
            if (result.AllocatedBytes > allocationCeiling)
            {
                failures.Add($"{result.Identity}: allocated {result.AllocatedBytes} bytes, ceiling {allocationCeiling}");
            }
        }

        if (results.Count != FullSizes.Length * (8 + (ChurnPercentages.Length * 4)))
        {
            failures.Add($"matrix: produced {results.Count} rows, expected 80");
        }

        if (failures.Count == 0)
        {
            Console.Error.WriteLine($"GATE PASS: {results.Count} rows satisfied performance-gate-v1.");
            return 0;
        }

        Console.Error.WriteLine($"GATE FAIL: {failures.Count} performance-gate-v1 violation(s).");
        foreach (string failure in failures)
        {
            Console.Error.WriteLine($"  {failure}");
        }

        return 1;
    }

    private static int GetExpectedEvents(BenchmarkResult result)
    {
        if (result.Scenario.StartsWith("range-", StringComparison.Ordinal))
        {
            return result.Repetitions;
        }

        if (result.Policy == nameof(UpdateNotificationMode.Reset))
        {
            return result.Repetitions;
        }

        int churnCount = GetChurnCount(result.Size, result.ChurnPercent ?? throw new InvalidOperationException());
        return checked(2 * churnCount * result.Repetitions);
    }

    private static int GetExpectedRetainedIdentities(BenchmarkResult result)
    {
        int retainedPerOperation;
        if (result.Scenario is "range-append" or "range-move")
        {
            retainedPerOperation = result.Size;
        }
        else if (result.Scenario is "range-remove" or "range-replace")
        {
            retainedPerOperation = result.Size - Math.Max(1, result.Size / 10);
        }
        else
        {
            retainedPerOperation = result.Size - GetChurnCount(
                result.Size,
                result.ChurnPercent ?? throw new InvalidOperationException());
        }

        return checked(retainedPerOperation * result.Repetitions);
    }

    private static long GetAllocationCeilingPerOperation(BenchmarkResult result) => result.Scenario switch
    {
        "range-append" => checked(65_536L + (256L * result.Size)),
        "range-remove" or "range-move" => checked(65_536L + (64L * result.Size)),
        "range-replace" => checked(65_536L + (128L * result.Size)),
        "reconcile-keyed" or "reconcile-duplicate" => checked(262_144L + (1_024L * result.Size)),
        _ => throw new InvalidOperationException($"No allocation ceiling is defined for '{result.Scenario}'."),
    };

    private static int GetChurnCount(int size, int churnPercent) => Math.Max(1, checked(size * churnPercent / 100));

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
        int churnCount = GetChurnCount(initial.Length, churnPercent);
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
        int churnCount = GetChurnCount(initial.Length, churnPercent);
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

    private enum ExecutionMode
    {
        Quick,
        Full,
        Gate,
    }

    private sealed record BenchmarkResult(
        string Scenario,
        string Policy,
        int Size,
        int? ChurnPercent,
        int Repetitions,
        long ElapsedTicks,
        long AllocatedBytes,
        int Events,
        int RetainedIdentities)
    {
        public const string CsvHeader =
            "scenario,policy,size,churn_percent,repetitions,elapsed_us,allocated_bytes,events,retained_identities";

        public string Identity => string.Create(
            CultureInfo.InvariantCulture,
            $"{Scenario}/{Policy}/size={Size}/churn={ChurnPercent?.ToString(CultureInfo.InvariantCulture) ?? "-"}");

        public string ToCsv()
        {
            double elapsedMicroseconds = ElapsedTicks * 1_000_000d / Stopwatch.Frequency;
            return string.Join(",",
                Scenario,
                Policy,
                Size.ToString(CultureInfo.InvariantCulture),
                ChurnPercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Repetitions.ToString(CultureInfo.InvariantCulture),
                elapsedMicroseconds.ToString("F1", CultureInfo.InvariantCulture),
                AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                Events.ToString(CultureInfo.InvariantCulture),
                RetainedIdentities.ToString(CultureInfo.InvariantCulture));
        }
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
