using Comparison.RoutingStateApp.S2;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Comparison.RoutingStateApp.S1;

public sealed class NoteDetailViewModel : ReactiveObject, IRoutableViewModel, ILeaveGuard
{
    private Note _note;
    private string _title;

    public NoteDetailViewModel(IScreen host, NoteStore store, int id)
    {
        HostScreen = host;
        _note = store.Get(id);
        _title = _note.Title;
        Save = ReactiveCommand.Create(() => store.Update(_note = _note with { Title = Title }));
    }

    public string UrlPathSegment => "note";
    public IScreen HostScreen { get; }
    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }
    public bool IsDirty => _note.Title != Title;
    public ReactiveCommand<RxVoid, RxVoid> Save { get; }

    public async Task<bool> CanLeaveAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        if (!await Dialogs.Confirm.Handle("Discard unsaved changes?"))
        {
            return false;
        }

        Title = _note.Title; // discard; the Back that follows is not conditional on it
        return true;
    }
}
