using System.Collections.ObjectModel;

namespace HybridNotes;

public sealed record Note(int Id, string Title, string Body);

// An application service, shared by the native list and either editor presentation.
public interface INoteStore
{
    IReadOnlyList<Note> All { get; }
    Note Get(int id);
    Task SaveAsync(Note note, CancellationToken cancellationToken);
}

public sealed class NoteStore(TimeProvider time) : INoteStore
{
    private readonly List<Note> _notes = [new(1, "Groceries", "Milk, eggs"), new(2, "Release checklist", "Build and review")];
    public IReadOnlyList<Note> All => _notes;
    public Note Get(int id) => _notes.Single(note => note.Id == id);

    public async Task SaveAsync(Note note, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(350), time, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _notes[_notes.FindIndex(saved => saved.Id == note.Id)] = note;
    }
}

internal static class CollectionExtensions
{
    internal static void ReplaceWith<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }
}
