using Comparison.PrismApp.S2;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Mvvm;
using Prism.Navigation.Regions;

namespace Comparison.PrismApp.S1;

public sealed class NoteDetailViewModel : BindableBase, IConfirmNavigationRequest, IRegionMemberLifetime
{
    private readonly NoteStore _store;
    private readonly IDialogService _dialogs;
    private Note? _note;
    private string _title = "";

    public NoteDetailViewModel(NoteStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        SaveCommand = new DelegateCommand(() => _store.Update(_note = _note! with { Title = Title }));
    }

    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public bool IsDirty => _note is not null && _note.Title != Title;
    public DelegateCommand SaveCommand { get; }

    // A new view per note; it is removed from the region when deactivated.
    public bool KeepAlive => false;

    public bool IsNavigationTarget(NavigationContext context) => context.Parameters.GetValue<int>("id") == _note?.Id;

    public void OnNavigatedTo(NavigationContext context)
    {
        _note = _store.Get(context.Parameters.GetValue<int>("id"));
        Title = _note.Title;
    }

    // Runs only once a navigation away has committed, so a cancelled or superseded one keeps the edits.
    public void OnNavigatedFrom(NavigationContext context) => Title = _note?.Title ?? "";

    public void ConfirmNavigationRequest(NavigationContext context, Action<bool> continuationCallback)
    {
        if (!IsDirty)
        {
            continuationCallback(true);
            return;
        }

        _dialogs.ShowDialog(nameof(ConfirmDialog), new DialogParameters { { "message", "Discard unsaved changes?" } },
            result => continuationCallback(result.Result == ButtonResult.OK));
    }
}
