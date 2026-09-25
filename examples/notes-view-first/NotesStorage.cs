namespace NotesWindowViews;

public interface INotesStorage
{
    Task SaveAsync(string title, string body, CancellationToken token);
}

public sealed class MemoryNotesStorage : INotesStorage
{
    public string? LastTitle { get; private set; }
    public string? LastBody { get; private set; }

    public async Task SaveAsync(string title, string body, CancellationToken token)
    {
        await Task.Delay(250, token);
        LastTitle = title;
        LastBody = body;
    }
}
