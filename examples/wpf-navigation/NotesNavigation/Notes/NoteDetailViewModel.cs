using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Navigation.Examples.Notes;

public sealed class NoteDetailViewModel : ObservableObject, INavigationInitialize<int>, INavigationDepartureGuard
{
    private readonly NoteStore _store;
    private readonly LeaveConfirmation _leave;
    private Note _note = new(0, "");
    private string _title = "";

    public NoteDetailViewModel(NoteStore store, AppRegions regions)
    {
        _store = store;
        // Asks in the Dialog region before edits are lost. The discard runs only if the departure commits,
        // so a cancelled or superseded Back keeps the edits.
        _leave = LeaveConfirmation.InDialog(regions.Dialog,
            () => NavigationTarget.Create<ConfirmViewModel, string>("Discard unsaved changes?"),
            () => IsDirty, () => Title = _note.Title);
        SaveCommand = new RelayCommand(() => _store.Update(_note = _note with { Title = Title }));
    }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public bool IsDirty => _note.Title != Title;
    public IRelayCommand SaveCommand { get; }

    // Runs once, before the page is shown. A retained page keeps its note and edits.
    public ValueTask InitializeAsync(NavigationEntryContext entry, int id, CancellationToken cancellationToken)
    {
        _note = _store.Get(id);
        Title = _note.Title;
        return ValueTask.CompletedTask;
    }

    // Every exit asks: Back, a push from code, or a parent region leaving.
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        _leave.CanDepartAsync(departure, cancellationToken);
}
