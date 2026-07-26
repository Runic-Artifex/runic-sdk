using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>A single task displayed by the sample.</summary>
public sealed partial class TodoItem(string title) : ObservableObject
{
    /// <summary>Gets the stable identifier submitted by the item buttons.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Gets the task description.</summary>
    public string Title { get; } = title;

    /// <summary>Gets or sets whether the task is complete.</summary>
    [ObservableProperty]
    private bool isCompleted;
}
