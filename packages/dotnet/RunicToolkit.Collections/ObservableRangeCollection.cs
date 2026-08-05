using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RunicToolkit.Collections;

/// <summary>
/// Represents an observable collection with deterministic range mutation operations.
/// </summary>
/// <typeparam name="T">The type of elements in the collection.</typeparam>
/// <remarks>
/// The collection is not thread-safe and does not capture or use a synchronization context.
/// Callers are responsible for coordinating all access with the collection's owner.
/// </remarks>
public sealed partial class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private const string ReentrantMutationMessage =
        "Structural mutation is not allowed while another operation, callback, or input enumeration is active.";

    private static readonly PropertyChangedEventArgs CountPropertyChangedEventArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerPropertyChangedEventArgs = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetCollectionChangedEventArgs =
        new(NotifyCollectionChangedAction.Reset);

    private readonly RangeNotificationMode _rangeNotificationMode;
    private int _mutationDepth;

    /// <summary>
    /// Initializes an empty collection that reports multi-item range notifications.
    /// </summary>
    public ObservableRangeCollection()
        : this(Array.Empty<T>(), new ObservableRangeCollectionOptions())
    {
    }

    /// <summary>
    /// Initializes a collection containing the supplied items.
    /// </summary>
    /// <param name="items">The items to copy into the collection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    public ObservableRangeCollection(IEnumerable<T> items)
        : this(items, new ObservableRangeCollectionOptions())
    {
    }

    /// <summary>
    /// Initializes an empty collection with the supplied options.
    /// </summary>
    /// <param name="options">The range notification options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ObservableRangeCollectionOptions.RangeNotifications"/> is not a defined value.
    /// </exception>
    public ObservableRangeCollection(ObservableRangeCollectionOptions options)
        : this(Array.Empty<T>(), options)
    {
    }

    /// <summary>
    /// Initializes a collection containing the supplied items and using the supplied options.
    /// </summary>
    /// <param name="items">The items to copy into the collection.</param>
    /// <param name="options">The range notification options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="items"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="ObservableRangeCollectionOptions.RangeNotifications"/> is not a defined value.
    /// </exception>
    public ObservableRangeCollection(
        IEnumerable<T> items,
        ObservableRangeCollectionOptions options)
        : this(CreateInitialization(items, options))
    {
    }

    private ObservableRangeCollection(Initialization initialization)
        : base(initialization.Items)
    {
        _rangeNotificationMode = initialization.RangeNotificationMode;
    }

    /// <summary>
    /// Adds a copy of the supplied sequence to the end of the collection.
    /// </summary>
    /// <param name="items">The items to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A structural mutation is already active.</exception>
    public void AddRange(IEnumerable<T> items)
    {
        InsertRange(Count, items);
    }

    /// <summary>
    /// Inserts a copy of the supplied sequence at the specified index.
    /// </summary>
    /// <param name="index">The zero-based insertion index.</param>
    /// <param name="items">The items to insert.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Count]</c>.</exception>
    /// <exception cref="InvalidOperationException">A structural mutation is already active.</exception>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        using (EnterMutation())
        {
            ValidateInsertionIndex(index);
            List<T> buffer = Materialize(items);

            if (buffer.Count == 0)
            {
                return;
            }

            if (buffer.Count == 1)
            {
                base.InsertItem(index, buffer[0]);
                return;
            }

            for (int offset = 0; offset < buffer.Count; offset++)
            {
                Items.Insert(index + offset, buffer[offset]);
            }

            RaiseCountAndIndexerChanged();
            RaiseAddOrReset(buffer, index);
        }
    }

    /// <summary>
    /// Removes a contiguous range beginning at the specified index.
    /// </summary>
    /// <param name="index">The zero-based index of the first item to remove.</param>
    /// <param name="count">The number of items to remove.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> or <paramref name="count"/> does not describe a valid range.
    /// </exception>
    /// <exception cref="InvalidOperationException">A structural mutation is already active.</exception>
    public void RemoveRange(int index, int count)
    {
        using (EnterMutation())
        {
            ValidateExistingRange(index, count);

            if (count == 0)
            {
                return;
            }

            if (count == 1)
            {
                base.RemoveItem(index);
                return;
            }

            T[] removedItems = CopyRange(index, count);
            for (int offset = count - 1; offset >= 0; offset--)
            {
                Items.RemoveAt(index + offset);
            }

            RaiseCountAndIndexerChanged();
            RaiseRemoveOrReset(removedItems, index);
        }
    }

    /// <summary>
    /// Replaces a contiguous range with a copy of the supplied sequence.
    /// </summary>
    /// <param name="index">The zero-based index of the first item to replace.</param>
    /// <param name="count">The number of existing items to replace.</param>
    /// <param name="replacement">The replacement items.</param>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> or <paramref name="count"/> does not describe a valid range.
    /// </exception>
    /// <exception cref="InvalidOperationException">A structural mutation is already active.</exception>
    public void ReplaceRange(int index, int count, IEnumerable<T> replacement)
    {
        using (EnterMutation())
        {
            ValidateExistingRange(index, count);
            List<T> buffer = Materialize(replacement);

            if (count == 0)
            {
                InsertBufferedRange(index, buffer);
                return;
            }

            if (buffer.Count == 0)
            {
                RemoveBufferedRange(index, count);
                return;
            }

            if (count == 1 && buffer.Count == 1)
            {
                base.SetItem(index, buffer[0]);
                return;
            }

            if (count == buffer.Count)
            {
                T[]? removedItems = _rangeNotificationMode == RangeNotificationMode.Range
                    ? CopyRange(index, count)
                    : null;

                for (int offset = 0; offset < buffer.Count; offset++)
                {
                    Items[index + offset] = buffer[offset];
                }

                OnPropertyChanged(IndexerPropertyChangedEventArgs);
                RaiseReplaceOrReset(buffer, removedItems, index);
                return;
            }

            ReplaceUnequalRange(index, count, buffer);
        }
    }

    /// <summary>
    /// Moves a contiguous block so its start has the specified index in the final collection.
    /// </summary>
    /// <param name="oldIndex">The original zero-based start index of the block.</param>
    /// <param name="count">The number of items to move.</param>
    /// <param name="newIndex">The block's zero-based start index after removal of the block.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The source range is invalid, or <paramref name="newIndex"/> is outside <c>[0, Count - count]</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">A structural mutation is already active.</exception>
    public void MoveRange(int oldIndex, int count, int newIndex)
    {
        using (EnterMutation())
        {
            ValidateExistingRange(oldIndex, count);
            int maximumNewIndex = Count - count;
            if (newIndex < 0 || newIndex > maximumNewIndex)
            {
                throw new ArgumentOutOfRangeException(nameof(newIndex));
            }

            if (count == 0 || oldIndex == newIndex)
            {
                return;
            }

            if (count == 1)
            {
                base.MoveItem(oldIndex, newIndex);
                return;
            }

            T[] movedItems = CopyRange(oldIndex, count);
            for (int offset = count - 1; offset >= 0; offset--)
            {
                Items.RemoveAt(oldIndex + offset);
            }

            for (int offset = 0; offset < movedItems.Length; offset++)
            {
                Items.Insert(newIndex + offset, movedItems[offset]);
            }

            OnPropertyChanged(IndexerPropertyChangedEventArgs);
            RaiseMoveOrReset(movedItems, newIndex, oldIndex);
        }
    }

    /// <summary>
    /// Returns a shallow snapshot of the collection's current membership and order.
    /// </summary>
    /// <returns>A new array containing the current items.</returns>
    public T[] ToSnapshot()
    {
        T[] snapshot = new T[Count];
        CopyTo(snapshot, 0);
        return snapshot;
    }

    /// <inheritdoc/>
    protected override void InsertItem(int index, T item)
    {
        using (EnterMutation())
        {
            base.InsertItem(index, item);
        }
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        using (EnterMutation())
        {
            base.RemoveItem(index);
        }
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, T item)
    {
        using (EnterMutation())
        {
            base.SetItem(index, item);
        }
    }

    /// <inheritdoc/>
    protected override void MoveItem(int oldIndex, int newIndex)
    {
        using (EnterMutation())
        {
            base.MoveItem(oldIndex, newIndex);
        }
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        using (EnterMutation())
        {
            if (Count == 0)
            {
                return;
            }

            base.ClearItems();
        }
    }

    private static Initialization CreateInitialization(
        IEnumerable<T> items,
        ObservableRangeCollectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RangeNotifications is not RangeNotificationMode.Range and not RangeNotificationMode.Reset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RangeNotifications,
                "The range notification mode is not defined.");
        }

        return new Initialization(Materialize(items), options.RangeNotifications);
    }

    private static List<T> Materialize(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var buffer = new List<T>();
        foreach (T item in items)
        {
            buffer.Add(item);
        }

        return buffer;
    }

    private static ReadOnlyCollection<T> ToReadOnlyPayload(List<T> items)
    {
        return items.AsReadOnly();
    }

    private static ReadOnlyCollection<T> ToReadOnlyPayload(T[] items)
    {
        return Array.AsReadOnly(items);
    }

    private void InsertBufferedRange(int index, List<T> buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        if (buffer.Count == 1)
        {
            base.InsertItem(index, buffer[0]);
            return;
        }

        for (int offset = 0; offset < buffer.Count; offset++)
        {
            Items.Insert(index + offset, buffer[offset]);
        }

        RaiseCountAndIndexerChanged();
        RaiseAddOrReset(buffer, index);
    }

    private void RemoveBufferedRange(int index, int count)
    {
        if (count == 1)
        {
            base.RemoveItem(index);
            return;
        }

        T[] removedItems = CopyRange(index, count);
        for (int offset = count - 1; offset >= 0; offset--)
        {
            Items.RemoveAt(index + offset);
        }

        RaiseCountAndIndexerChanged();
        RaiseRemoveOrReset(removedItems, index);
    }

    private void ReplaceUnequalRange(int index, int count, List<T> replacement)
    {
        for (int offset = count - 1; offset >= 0; offset--)
        {
            Items.RemoveAt(index + offset);
        }

        for (int offset = 0; offset < replacement.Count; offset++)
        {
            Items.Insert(index + offset, replacement[offset]);
        }

        if (count != replacement.Count)
        {
            OnPropertyChanged(CountPropertyChangedEventArgs);
        }

        OnPropertyChanged(IndexerPropertyChangedEventArgs);
        OnCollectionChanged(ResetCollectionChangedEventArgs);
    }

    private void InstallReset(T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);

        using (EnterMutation())
        {
            bool countChanged = Count != items.Length;
            Items.Clear();
            for (int index = 0; index < items.Length; index++)
            {
                Items.Add(items[index]);
            }

            if (countChanged)
            {
                OnPropertyChanged(CountPropertyChangedEventArgs);
            }

            OnPropertyChanged(IndexerPropertyChangedEventArgs);
            OnCollectionChanged(ResetCollectionChangedEventArgs);
        }
    }

    private T[] CopyRange(int index, int count)
    {
        var items = new T[count];
        for (int offset = 0; offset < count; offset++)
        {
            items[offset] = Items[index + offset];
        }

        return items;
    }

    private void ValidateInsertionIndex(int index)
    {
        if (index < 0 || index > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    private void ValidateExistingRange(int index, int count)
    {
        if (index < 0 || index > Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (count < 0 || count > Count - index)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
    }

    private MutationScope EnterMutation()
    {
        if (_mutationDepth != 0 || IsUpdatePlanning)
        {
            throw new InvalidOperationException(ReentrantMutationMessage);
        }

        _mutationDepth++;
        return new MutationScope(this);
    }

    private void ExitMutation()
    {
        _mutationDepth--;
    }

    private void RaiseCountAndIndexerChanged()
    {
        OnPropertyChanged(CountPropertyChangedEventArgs);
        OnPropertyChanged(IndexerPropertyChangedEventArgs);
    }

    private void RaiseAddOrReset(List<T> addedItems, int index)
    {
        if (_rangeNotificationMode == RangeNotificationMode.Reset)
        {
            OnCollectionChanged(ResetCollectionChangedEventArgs);
            return;
        }

        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add,
                ToReadOnlyPayload(addedItems),
                index));
    }

    private void RaiseRemoveOrReset(T[] removedItems, int index)
    {
        if (_rangeNotificationMode == RangeNotificationMode.Reset)
        {
            OnCollectionChanged(ResetCollectionChangedEventArgs);
            return;
        }

        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove,
                ToReadOnlyPayload(removedItems),
                index));
    }

    private void RaiseReplaceOrReset(List<T> addedItems, T[]? removedItems, int index)
    {
        if (_rangeNotificationMode == RangeNotificationMode.Reset)
        {
            OnCollectionChanged(ResetCollectionChangedEventArgs);
            return;
        }

        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace,
                ToReadOnlyPayload(addedItems),
                ToReadOnlyPayload(removedItems!),
                index));
    }

    private void RaiseMoveOrReset(T[] movedItems, int newIndex, int oldIndex)
    {
        if (_rangeNotificationMode == RangeNotificationMode.Reset)
        {
            OnCollectionChanged(ResetCollectionChangedEventArgs);
            return;
        }

        OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Move,
                ToReadOnlyPayload(movedItems),
                newIndex,
                oldIndex));
    }

    private readonly record struct Initialization(
        List<T> Items,
        RangeNotificationMode RangeNotificationMode);

    private readonly struct MutationScope : IDisposable
    {
        private readonly ObservableRangeCollection<T> _owner;

        public MutationScope(ObservableRangeCollection<T> owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            _owner.ExitMutation();
        }
    }
}
