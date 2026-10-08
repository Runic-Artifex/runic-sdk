// Shared domain, identical in every comparison app. Not counted.
namespace Comparison;

public sealed record Note(int Id, string Title);

public sealed class NoteStore
{
    private readonly List<Note> _notes = [new(1, "Groceries"), new(2, "Release checklist"), new(3, "Ideas")];
    public IReadOnlyList<Note> All => _notes;
    public Note Get(int id) => _notes.Single(n => n.Id == id);
    public void Update(Note note) => _notes[_notes.FindIndex(n => n.Id == note.Id)] = note;
    public void Delete(int id) => _notes.RemoveAll(n => n.Id == id);
}

public static class CollectionExtensions
{
    public static void ReplaceWith<T>(this System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
