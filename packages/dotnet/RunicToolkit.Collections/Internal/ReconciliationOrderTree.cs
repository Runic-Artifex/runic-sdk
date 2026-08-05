using System;

namespace RunicToolkit.Collections.Internal;

/// <summary>
/// An implicit AVL tree used to plan indexed edits without repeatedly scanning or shifting a list.
/// Rank, insertion, movement, and removal are O(log n) in the worst case.
/// </summary>
internal sealed class ReconciliationOrderTree<T>
{
    private Node? _root;

    internal int Count => SizeOf(_root);

    internal Node Append(T item) => Insert(Count, item);

    internal Node Insert(int index, T item)
    {
        if ((uint)index > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var node = new Node(item);
        _root = InsertAt(_root, index, node);
        SetParent(_root, null);
        return node;
    }

    internal static int Rank(Node node)
    {
        var rank = SizeOf(node.Left);
        while (node.Parent is not null)
        {
            if (ReferenceEquals(node, node.Parent.Right))
            {
                rank += SizeOf(node.Parent.Left) + 1;
            }

            node = node.Parent;
        }

        return rank;
    }

    internal void Move(Node node, int newIndex)
    {
        var oldIndex = Rank(node);
        _root = RemoveAt(_root!, oldIndex, out Node removed);
        SetParent(_root, null);
        _root = InsertAt(_root, newIndex, removed);
        SetParent(_root, null);
    }

    internal Node RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        _root = RemoveAt(_root!, index, out Node removed);
        SetParent(_root, null);
        return removed;
    }

    private static Node InsertAt(Node? root, int index, Node node)
    {
        if (root is null)
        {
            return node;
        }

        int leftSize = SizeOf(root.Left);
        if (index <= leftSize)
        {
            root.Left = InsertAt(root.Left, index, node);
        }
        else
        {
            root.Right = InsertAt(root.Right, index - leftSize - 1, node);
        }

        return Balance(root);
    }

    private static Node? RemoveAt(Node root, int index, out Node removed)
    {
        int leftSize = SizeOf(root.Left);
        if (index < leftSize)
        {
            root.Left = RemoveAt(root.Left!, index, out removed);
            return Balance(root);
        }

        if (index > leftSize)
        {
            root.Right = RemoveAt(root.Right!, index - leftSize - 1, out removed);
            return Balance(root);
        }

        removed = root;
        Node? replacement = Join(root.Left, root.Right);
        removed.Left = null;
        removed.Right = null;
        removed.Parent = null;
        removed.Height = 1;
        removed.Size = 1;
        SetParent(replacement, null);
        return replacement;
    }

    private static Node? Join(Node? left, Node? right)
    {
        if (left is null)
        {
            SetParent(right, null);
            return right;
        }

        if (right is null)
        {
            SetParent(left, null);
            return left;
        }

        if (HeightOf(left) > HeightOf(right) + 1)
        {
            left.Right = Join(left.Right, right);
            return Balance(left);
        }

        if (HeightOf(right) > HeightOf(left) + 1)
        {
            right.Left = Join(left, right.Left);
            return Balance(right);
        }

        Node? newRight = ExtractMinimum(right, out Node minimum);
        minimum.Left = left;
        minimum.Right = newRight;
        return Balance(minimum);
    }

    private static Node? ExtractMinimum(Node root, out Node minimum)
    {
        if (root.Left is null)
        {
            minimum = root;
            Node? remainder = root.Right;
            root.Left = null;
            root.Right = null;
            root.Parent = null;
            root.Height = 1;
            root.Size = 1;
            SetParent(remainder, null);
            return remainder;
        }

        root.Left = ExtractMinimum(root.Left, out minimum);
        return Balance(root);
    }

    private static Node Balance(Node node)
    {
        Refresh(node);
        int balance = HeightOf(node.Left) - HeightOf(node.Right);

        if (balance > 1)
        {
            if (HeightOf(node.Left!.Left) < HeightOf(node.Left.Right))
            {
                node.Left = RotateLeft(node.Left);
            }

            return RotateRight(node);
        }

        if (balance < -1)
        {
            if (HeightOf(node.Right!.Right) < HeightOf(node.Right.Left))
            {
                node.Right = RotateRight(node.Right);
            }

            return RotateLeft(node);
        }

        node.Parent = null;
        return node;
    }

    private static Node RotateLeft(Node node)
    {
        Node newRoot = node.Right!;
        node.Right = newRoot.Left;
        newRoot.Left = node;
        Refresh(node);
        Refresh(newRoot);
        newRoot.Parent = null;
        return newRoot;
    }

    private static Node RotateRight(Node node)
    {
        Node newRoot = node.Left!;
        node.Left = newRoot.Right;
        newRoot.Right = node;
        Refresh(node);
        Refresh(newRoot);
        newRoot.Parent = null;
        return newRoot;
    }

    private static int SizeOf(Node? node) => node?.Size ?? 0;

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static void SetParent(Node? node, Node? parent)
    {
        if (node is not null)
        {
            node.Parent = parent;
        }
    }

    private static void Refresh(Node node)
    {
        node.Size = checked(SizeOf(node.Left) + SizeOf(node.Right) + 1);
        node.Height = Math.Max(HeightOf(node.Left), HeightOf(node.Right)) + 1;
        SetParent(node.Left, node);
        SetParent(node.Right, node);
    }

    internal sealed class Node
    {
        internal Node(T item)
        {
            Item = item;
        }

        internal T Item { get; set; }

        internal int Size { get; set; } = 1;

        internal int Height { get; set; } = 1;

        internal Node? Left { get; set; }

        internal Node? Right { get; set; }

        internal Node? Parent { get; set; }
    }
}
