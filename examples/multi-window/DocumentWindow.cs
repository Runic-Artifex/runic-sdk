using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace MultiWindow;

// The document a DocumentWindow shows: its path is the Window's key, so a second request for it focuses the open one.
public sealed record DocumentRequest(string Path);

// A document in a Window of its own. ProjectViewModel opens it with IRunicWindowService.FindOrOpenAsync.
public sealed class DocumentViewModel : ReactiveObject, IDisposable
{
    private readonly ProjectService _project;
    private readonly Runic.Navigation.IRunicModelContext _context;
    private string _projectTitle;

    public DocumentViewModel(IRunicWindowParameter<DocumentRequest> document, ProjectService project, IRunicWindowContext window,
        Runic.Navigation.IRunicModelContext context)
    {
        _project = project;
        _context = context;
        Path = document.Value.Path;
        _projectTitle = project.Title;
        CloseCommand = ReactiveCommand.CreateFromTask(async token => { await window.RequestCloseAsync(token); });
        _project.Renamed += OnRenamed;
    }

    public string Path { get; }

    public string ProjectTitle { get => _projectTitle; private set => this.RaiseAndSetIfChanged(ref _projectTitle, value); }

    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    private void OnRenamed(object? sender, EventArgs e) => _context.TryPost(() => ProjectTitle = _project.Title);

    public void Dispose() => _project.Renamed -= OnRenamed;
}

public sealed partial class DocumentWindow : RunicWindow<DocumentViewModel>;
