using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace WebUIToolkit.Collections.Tests;

internal sealed class TraceRecorder<T> : IDisposable
{
    private readonly ObservableRangeCollection<T> _collection;
    private readonly INotifyPropertyChanged _properties;

    public TraceRecorder(ObservableRangeCollection<T> collection)
    {
        _collection = collection;
        _properties = collection;
        _properties.PropertyChanged += OnPropertyChanged;
        collection.CollectionChanged += OnCollectionChanged;
    }

    public List<string> Entries { get; } = [];

    public List<NotifyCollectionChangedEventArgs> Events { get; } = [];

    public void Dispose()
    {
        _properties.PropertyChanged -= OnPropertyChanged;
        _collection.CollectionChanged -= OnCollectionChanged;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        Entries.Add($"P:{args.PropertyName}:{State()}");
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        Events.Add(args);
        var oldItems = Format(args.OldItems);
        var newItems = Format(args.NewItems);
        Entries.Add(
            $"C:{args.Action}:old={oldItems}@{args.OldStartingIndex}:new={newItems}@{args.NewStartingIndex}:{State()}");
    }

    private string State() => $"[{string.Join(",", _collection)}]";

    private static string Format(IList? items) =>
        items is null ? "-" : $"[{string.Join(",", items.Cast<object?>())}]";
}

internal static class EventReplay
{
    public static void Apply<T>(List<T> shadow, ObservableRangeCollection<T> source, NotifyCollectionChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Add:
                Insert(shadow, args.NewStartingIndex, args.NewItems!);
                break;
            case NotifyCollectionChangedAction.Remove:
                shadow.RemoveRange(args.OldStartingIndex, args.OldItems!.Count);
                break;
            case NotifyCollectionChangedAction.Replace:
                shadow.RemoveRange(args.OldStartingIndex, args.OldItems!.Count);
                Insert(shadow, args.NewStartingIndex, args.NewItems!);
                break;
            case NotifyCollectionChangedAction.Move:
                var moved = shadow.GetRange(args.OldStartingIndex, args.OldItems!.Count);
                shadow.RemoveRange(args.OldStartingIndex, moved.Count);
                shadow.InsertRange(args.NewStartingIndex, moved);
                break;
            case NotifyCollectionChangedAction.Reset:
                shadow.Clear();
                shadow.AddRange(source.ToSnapshot());
                break;
            default:
                throw new AssertionException($"Unexpected event action {args.Action}.");
        }
    }

    private static void Insert<T>(List<T> target, int index, IList source)
    {
        for (var offset = 0; offset < source.Count; offset++)
        {
            target.Insert(index + offset, (T)source[offset]!);
        }
    }
}
