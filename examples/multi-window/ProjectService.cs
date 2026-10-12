namespace MultiWindow;

// One instance for the application: every Window shows and renames the same project. Commands of two Windows can
// run at the same moment on two threads, so the state is guarded.
public sealed class ProjectService
{
    private readonly Lock _gate = new();
    private string _title = "Untitled";

    public event EventHandler? Renamed;

    public string Title { get { lock (_gate) return _title; } }

    public void Rename(string title)
    {
        lock (_gate) _title = title;
        Renamed?.Invoke(this, EventArgs.Empty);
    }
}
