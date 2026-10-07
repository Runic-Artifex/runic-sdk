namespace NotesWindowViews;

public interface INotesStorage
{
    Task SaveAsync(string title, string body, CancellationToken token);
}

// Simulates a slow store. The delay uses the injected clock, so a test with a
// manual clock decides when a save completes.
public sealed class MemoryNotesStorage(TimeProvider time) : INotesStorage
{
    public string? LastTitle { get; private set; }
    public string? LastBody { get; private set; }

    public async Task SaveAsync(string title, string body, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(250), time, token);
        LastTitle = title;
        LastBody = body;
    }
}
