using System;
using System.Collections.Generic;

namespace WebUIToolkit.Collections.Internal;

internal static class ReconciliationPlanner
{
    internal static ReconciliationPlan<T> Create<T>(
        T[] current,
        T[] target,
        int[] currentMatchByTarget,
        Func<T, T, T>? resolveMatch)
    {
        var desired = new T[target.Length];
        var replace = new bool[target.Length];

        for (var targetIndex = 0; targetIndex < target.Length; targetIndex++)
        {
            var currentIndex = currentMatchByTarget[targetIndex];
            if (currentIndex < 0)
            {
                desired[targetIndex] = target[targetIndex];
                continue;
            }

            var existing = current[currentIndex];
            var resolved = resolveMatch is null ? existing : resolveMatch(existing, target[targetIndex]);
            desired[targetIndex] = resolved;
            replace[targetIndex] = ShouldReplace(existing, resolved, resolveMatch is not null);
        }

        var tree = new ReconciliationOrderTree<T>();
        var currentNodes = new ReconciliationOrderTree<T>.Node[current.Length];
        for (var i = 0; i < current.Length; i++)
        {
            currentNodes[i] = tree.Append(current[i]);
        }

        var edits = new List<ReconciliationEdit<T>>();
        var added = 0;
        var moved = 0;
        var replaced = 0;

        for (var targetIndex = 0; targetIndex < target.Length; targetIndex++)
        {
            var currentIndex = currentMatchByTarget[targetIndex];
            if (currentIndex < 0)
            {
                tree.Insert(targetIndex, desired[targetIndex]);
                edits.Add(new ReconciliationEdit<T>(ReconciliationEditKind.Add, targetIndex, -1, desired[targetIndex]));
                added++;
                continue;
            }

            var node = currentNodes[currentIndex];
            var oldIndex = ReconciliationOrderTree<T>.Rank(node);
            if (oldIndex != targetIndex)
            {
                tree.Move(node, targetIndex);
                edits.Add(new ReconciliationEdit<T>(ReconciliationEditKind.Move, targetIndex, oldIndex, node.Item));
                moved++;
            }

            if (replace[targetIndex])
            {
                node.Item = desired[targetIndex];
                edits.Add(new ReconciliationEdit<T>(ReconciliationEditKind.Replace, targetIndex, -1, desired[targetIndex]));
                replaced++;
            }
        }

        var removed = 0;
        while (tree.Count > target.Length)
        {
            var removedNode = tree.RemoveAt(target.Length);
            edits.Add(new ReconciliationEdit<T>(ReconciliationEditKind.Remove, target.Length, -1, removedNode.Item));
            removed++;
        }

        return new ReconciliationPlan<T>(desired, edits.ToArray(), added, removed, moved, replaced);
    }

    private static bool ShouldReplace<T>(T existing, T resolved, bool resolverWasProvided)
    {
        if (typeof(T).IsValueType)
        {
            // Generic equality can deliberately ignore value-type fields. Once a resolver is
            // supplied, applying its returned value is the only way to honor the public contract.
            return resolverWasProvided;
        }

        return !ReferenceEquals(existing, resolved);
    }
}
