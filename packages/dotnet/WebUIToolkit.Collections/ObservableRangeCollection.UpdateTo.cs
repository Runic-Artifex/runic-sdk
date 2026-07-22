using System;
using System.Collections.Generic;
using WebUIToolkit.Collections.Internal;

namespace WebUIToolkit.Collections;

public sealed partial class ObservableRangeCollection<T>
{
    private int _updatePlanningDepth;

    private bool IsUpdatePlanning => _updatePlanningDepth != 0;

    /// <summary>
    /// Reconciles the collection with a target sequence by pairing equal occurrences in first-in,
    /// first-out order and retaining each matched existing item by default.
    /// </summary>
    /// <param name="target">The target membership and order. It is enumerated exactly once.</param>
    /// <param name="comparer">
    /// The equality comparer used to pair current and target occurrences, or <see langword="null"/>
    /// to use <see cref="EqualityComparer{T}.Default"/>.
    /// </param>
    /// <param name="resolveMatch">
    /// An optional callback invoked exactly once for every match, in target order. Its result becomes
    /// the desired item. When omitted, the matched existing item is retained.
    /// </param>
    /// <param name="options">Notification-selection options, or <see langword="null"/> for defaults.</param>
    /// <returns>The counts in the applied deterministic edit plan and the notification strategy used.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option has an invalid value.</exception>
    /// <exception cref="InvalidOperationException">
    /// A target iterator, comparer, resolver, or notification callback attempts structural mutation.
    /// </exception>
    /// <remarks>
    /// Target materialization, matching, resolver execution, and edit planning all finish before the
    /// first structural mutation. Arbitrary equality comparison can require O(n²) comparisons when
    /// duplicates are present; use the keyed overload for expected O(n) matching on large collections.
    /// </remarks>
    public CollectionUpdateResult UpdateTo(
        IEnumerable<T> target,
        IEqualityComparer<T>? comparer = null,
        Func<T, T, T>? resolveMatch = null,
        CollectionUpdateOptions? options = null)
    {
        ReconciliationPlan<T> plan;
        int oldCount;
        CollectionUpdateOptions effectiveOptions;
        EnterUpdatePlanning();
        try
        {
            ArgumentNullException.ThrowIfNull(target);
            effectiveOptions = options ?? new CollectionUpdateOptions();
            effectiveOptions.Validate();
            T[] current = ToSnapshot();
            oldCount = current.Length;
            T[] targetItems = MaterializeUpdateTarget(target);
            int[] matches = MatchByEquality(current, targetItems, comparer ?? EqualityComparer<T>.Default);
            plan = ReconciliationPlanner.Create(current, targetItems, matches, resolveMatch);
        }
        finally
        {
            ExitUpdatePlanning();
        }

        return ApplyUpdatePlan(plan, oldCount, effectiveOptions);
    }

    /// <summary>
    /// Reconciles the collection with a target sequence by unique key while retaining each matched
    /// existing item by default.
    /// </summary>
    /// <typeparam name="TKey">The non-null key type.</typeparam>
    /// <param name="target">The target membership and order. It is enumerated exactly once.</param>
    /// <param name="keySelector">Selects the unique reconciliation key for each current and target item.</param>
    /// <param name="keyComparer">
    /// The key comparer, or <see langword="null"/> to use <see cref="EqualityComparer{TKey}.Default"/>.
    /// </param>
    /// <param name="resolveMatch">
    /// An optional callback invoked exactly once for every match, in target order. Its result becomes
    /// the desired item. When omitted, the matched existing item is retained.
    /// </param>
    /// <param name="options">Notification-selection options, or <see langword="null"/> for defaults.</param>
    /// <returns>The counts in the applied deterministic edit plan and the notification strategy used.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="target"/> or <paramref name="keySelector"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A selected key is null, or a key is duplicated in the current or target sequence. The exception
    /// message identifies the side and position before any structural mutation occurs.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">An option has an invalid value.</exception>
    /// <exception cref="InvalidOperationException">
    /// A target iterator, key selector, key comparer, resolver, or notification callback attempts structural mutation.
    /// </exception>
    /// <remarks>
    /// Matching uses a dictionary and is O(n) expected. Deterministic edit planning is O(n log n)
    /// in the worst case. Resolver execution and all planning finish before the first structural mutation.
    /// </remarks>
    public CollectionUpdateResult UpdateTo<TKey>(
        IEnumerable<T> target,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? keyComparer = null,
        Func<T, T, T>? resolveMatch = null,
        CollectionUpdateOptions? options = null)
        where TKey : notnull
    {
        ReconciliationPlan<T> plan;
        int oldCount;
        CollectionUpdateOptions effectiveOptions;
        EnterUpdatePlanning();
        try
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(keySelector);
            effectiveOptions = options ?? new CollectionUpdateOptions();
            effectiveOptions.Validate();
            T[] current = ToSnapshot();
            oldCount = current.Length;
            T[] targetItems = MaterializeUpdateTarget(target);
            int[] matches = MatchByUniqueKey(current, targetItems, keySelector, keyComparer);
            plan = ReconciliationPlanner.Create(current, targetItems, matches, resolveMatch);
        }
        finally
        {
            ExitUpdatePlanning();
        }

        return ApplyUpdatePlan(plan, oldCount, effectiveOptions);
    }

    private static T[] MaterializeUpdateTarget(IEnumerable<T> target)
    {
        var items = new List<T>();
        foreach (T item in target)
        {
            items.Add(item);
        }

        return items.ToArray();
    }

    private static int[] MatchByEquality(T[] current, T[] target, IEqualityComparer<T> comparer)
    {
        var currentIsMatched = new bool[current.Length];
        var currentMatchByTarget = new int[target.Length];
        Array.Fill(currentMatchByTarget, -1);

        for (var targetIndex = 0; targetIndex < target.Length; targetIndex++)
        {
            for (var currentIndex = 0; currentIndex < current.Length; currentIndex++)
            {
                if (!currentIsMatched[currentIndex] && comparer.Equals(current[currentIndex], target[targetIndex]))
                {
                    currentIsMatched[currentIndex] = true;
                    currentMatchByTarget[targetIndex] = currentIndex;
                    break;
                }
            }
        }

        return currentMatchByTarget;
    }

    private static int[] MatchByUniqueKey<TKey>(
        T[] current,
        T[] target,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? keyComparer)
        where TKey : notnull
    {
        var comparer = keyComparer ?? EqualityComparer<TKey>.Default;
        var currentPositions = new Dictionary<TKey, int>(current.Length, comparer);

        for (var currentIndex = 0; currentIndex < current.Length; currentIndex++)
        {
            TKey key = keySelector(current[currentIndex]);
            ValidateKey(key, "current", currentIndex);
            if (!currentPositions.TryAdd(key, currentIndex))
            {
                int firstIndex = currentPositions[key];
                throw DuplicateKey("current", currentIndex, firstIndex);
            }
        }

        var targetPositions = new Dictionary<TKey, int>(target.Length, comparer);
        var currentMatchByTarget = new int[target.Length];
        Array.Fill(currentMatchByTarget, -1);

        for (var targetIndex = 0; targetIndex < target.Length; targetIndex++)
        {
            TKey key = keySelector(target[targetIndex]);
            ValidateKey(key, "target", targetIndex);
            if (!targetPositions.TryAdd(key, targetIndex))
            {
                int firstIndex = targetPositions[key];
                throw DuplicateKey("target", targetIndex, firstIndex);
            }

            if (currentPositions.TryGetValue(key, out int currentIndex))
            {
                currentMatchByTarget[targetIndex] = currentIndex;
            }
        }

        return currentMatchByTarget;
    }

    private static void ValidateKey<TKey>(TKey key, string side, int position)
    {
        if (key is null)
        {
            throw new ArgumentException($"The {side} key at position {position} is null.");
        }
    }

    private static ArgumentException DuplicateKey(string side, int position, int firstPosition)
    {
        return new ArgumentException(
            $"The {side} key at position {position} duplicates the key at position {firstPosition}.");
    }

    private CollectionUpdateResult ApplyUpdatePlan(
        ReconciliationPlan<T> plan,
        int oldCount,
        CollectionUpdateOptions options)
    {
        if (plan.EventCount == 0)
        {
            return new CollectionUpdateResult(0, 0, 0, 0, 0, false);
        }

        bool useReset = ShouldUseReset(plan.EventCount, oldCount, plan.Desired.Length, options);
        if (useReset)
        {
            InstallReset(plan.Desired);
            return new CollectionUpdateResult(
                plan.Added,
                plan.Removed,
                plan.Moved,
                plan.Replaced,
                1,
                true);
        }

        foreach (ReconciliationEdit<T> edit in plan.Edits)
        {
            switch (edit.Kind)
            {
                case ReconciliationEditKind.Add:
                    Insert(edit.Index, edit.Item);
                    break;
                case ReconciliationEditKind.Remove:
                    RemoveAt(edit.Index);
                    break;
                case ReconciliationEditKind.Move:
                    Move(edit.OldIndex, edit.Index);
                    break;
                case ReconciliationEditKind.Replace:
                    this[edit.Index] = edit.Item;
                    break;
                default:
                    throw new InvalidOperationException("The reconciliation plan contains an unknown edit kind.");
            }
        }

        return new CollectionUpdateResult(
            plan.Added,
            plan.Removed,
            plan.Moved,
            plan.Replaced,
            plan.EventCount,
            false);
    }

    private static bool ShouldUseReset(
        int eventCount,
        int oldCount,
        int newCount,
        CollectionUpdateOptions options)
    {
        if (options.Notifications == UpdateNotificationMode.Reset)
        {
            return true;
        }

        if (options.Notifications == UpdateNotificationMode.Granular)
        {
            return false;
        }

        if (eventCount > options.MaxGranularEvents)
        {
            return true;
        }

        int largerCount = Math.Max(oldCount, newCount);
        return largerCount >= options.ResetRatioMinimumCount
            && eventCount / (double)largerCount > options.ResetChangeRatio;
    }

    private void EnterUpdatePlanning()
    {
        if (_mutationDepth != 0 || IsUpdatePlanning)
        {
            throw new InvalidOperationException(ReentrantMutationMessage);
        }

        _updatePlanningDepth = 1;
    }

    private void ExitUpdatePlanning()
    {
        _updatePlanningDepth--;
    }
}
