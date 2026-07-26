using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebUIToolkit.Collections;

namespace WebUIToolkit.Samples.SimpleTodo;

/// <summary>
/// Owns the page state. It has no knowledge of HTML, JavaScript, or CsWebUi.
/// </summary>
internal sealed partial class TodoViewModel : ObservableObject
{
    /// <summary>Initializes the sample with a few tasks worth interacting with.</summary>
    public TodoViewModel()
    {
        Items =
        [
            new TodoItem("Read the SimpleTodo guided tour"),
            new TodoItem("Add a task of my own"),
            new TodoItem("Inspect the compiled cwhtml view"),
        ];
    }

    /// <summary>Gets the observable task collection.</summary>
    public ObservableRangeCollection<TodoItem> Items { get; }

    /// <summary>Gets or sets the text entered in the composer.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string newTitle = string.Empty;

    /// <summary>
    /// Gets or sets the opaque item selected by a browser action.
    /// The browser never chooses a CLR command name or invokes arbitrary code.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private string selectedId = string.Empty;

    /// <summary>Gets the number of completed tasks.</summary>
    public int CompletedCount => Items.Count(static item => item.IsCompleted);

    /// <summary>Adds the validated draft and resets the composer.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        Items.Add(new TodoItem(NewTitle.Trim()));
        NewTitle = string.Empty;
    }

    private bool CanAdd() =>
        !string.IsNullOrWhiteSpace(NewTitle) &&
        NewTitle.Trim().Length is >= 2 and <= 80;

    /// <summary>Toggles the selected task.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedItem))]
    private void Toggle()
    {
        TodoItem item = FindSelected()!;
        item.IsCompleted = !item.IsCompleted;
        SelectedId = string.Empty;
    }

    /// <summary>Removes the selected task.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedItem))]
    private void Remove()
    {
        Items.Remove(FindSelected()!);
        SelectedId = string.Empty;
    }

    private bool HasSelectedItem() => FindSelected() is not null;

    private TodoItem? FindSelected() =>
        Guid.TryParse(SelectedId, out Guid id)
            ? Items.FirstOrDefault(item => item.Id == id)
            : null;

    /// <summary>Toggles one task from a typed frontend command argument.</summary>
    [RelayCommand]
    private void ToggleById(string id)
    {
        TodoItem? item = Find(id);
        if (item is not null)
        {
            item.IsCompleted = !item.IsCompleted;
        }
    }

    /// <summary>Removes one task from a typed frontend command argument.</summary>
    [RelayCommand]
    private void RemoveById(string id)
    {
        TodoItem? item = Find(id);
        if (item is not null)
        {
            Items.Remove(item);
        }
    }

    private TodoItem? Find(string id) =>
        Guid.TryParse(id, out Guid parsed)
            ? Items.FirstOrDefault(item => item.Id == parsed)
            : null;
}
