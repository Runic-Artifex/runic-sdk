using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using Comparison.CrissCrossApp.S2;
using CrissCross;
using ReactiveUI;

namespace Comparison.CrissCrossApp.S1;

public sealed class NoteDetailViewModel : RxObject
{
    private readonly NoteStore _store;
    private Note? _note;
    private string _title = "";

    public NoteDetailViewModel(NoteStore store)
    {
        _store = store;
        Save = ReactiveCommand.Create(() => _store.Update(_note = _note! with { Title = Title }));
    }

    public string Title { get => _title; set => this.RaiseAndSetIfChanged(ref _title, value); }
    public bool IsDirty => _note is not null && _note.Title != Title;
    public ReactiveCommand<RxVoid, RxVoid> Save { get; }

    // The parameter arrives with every navigation to this singleton.
    public override void WhenNavigatedTo(IViewModelNavigationEventArgs e, MultipleDisposable disposables)
    {
        _note = _store.Get((int)e.NavigationParameter!);
        Title = _note.Title;
    }

    // The guard is synchronous: cancel, ask, then navigate again after a yes.
    public override void WhenNavigating(IViewModelNavigatingEventArgs e)
    {
        if (IsDirty)
        {
            e.Cancel = true;
            _ = ConfirmLeaveAsync();
        }
    }

    private async Task ConfirmLeaveAsync()
    {
        if (await Dialogs.Confirm.Handle("Discard unsaved changes?"))
        {
            Title = _note!.Title; // discard, then retry the Back (the only way out of this page)
            this.NavigateBack();
        }
    }
}
