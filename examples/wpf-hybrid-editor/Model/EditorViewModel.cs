using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;
using Runic.Navigation;

namespace HybridNotes;

// The model has no WPF types. Native bindings and generated web clients use these
// same Toolkit properties and commands, including their cancellation behavior.
public sealed partial class EditorViewModel : ObservableObject, INavigationInitialize<int>, INavigationDepartureGuard, IDisposable
{
    private readonly INoteStore _store;
    private readonly LeaveConfirmation _leave;
    private Note _saved = new(0, "", "");
    private bool _disposed;

    public EditorViewModel(INoteStore store, AppRegions regions)
    {
        _store = store;
        _leave = LeaveConfirmation.InDialog(regions.Dialog,
            () => NavigationTarget.Create<ConfirmViewModel, string>("Discard unsaved changes?"),
            () => IsDirty, Discard);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(ValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string body = "";

    public bool IsDirty => Title != _saved.Title || Body != _saved.Body;
    public string ValidationMessage => string.IsNullOrWhiteSpace(Title) ? "Enter a title." : "";

    private string _error = "";
    private string _status = "";
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private bool CanSaveNote() => !_disposed && IsDirty && ValidationMessage.Length == 0;

    [RelayCommand(CanExecute = nameof(CanSaveNote), IncludeCancelCommand = true)]
    private async Task SaveAsync(CancellationToken token)
    {
        // Validate in the model even if a caller bypasses ICommand.CanExecute.
        if (!CanSaveNote()) return;
        Error = "";
        Status = "Saving…";
        var submitted = _saved with { Title = Title, Body = Body };
        try
        {
            await _store.SaveAsync(submitted, token);
            // The store has written the note; a late Cancel no longer undoes it.
            if (_disposed) return;
            _saved = submitted;
            RefreshDraftState(); // Later edits remain dirty after this save.
            Status = "Saved.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_disposed) Status = "Save cancelled.";
        }
        catch (IOException exception)
        {
            if (!_disposed) { Error = exception.Message; Status = "Save failed."; }
        }
    }

    public ValueTask InitializeAsync(NavigationEntryContext entry, int id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _saved = _store.Get(id);
        Title = _saved.Title;
        Body = _saved.Body;
        RefreshDraftState();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token) =>
        // Keep the note alive until its save completes or the user cancels it.
        SaveCommand.IsRunning ? ValueTask.FromResult(false) : _leave.CanDepartAsync(departure, token);

    private void Discard()
    {
        Title = _saved.Title;
        Body = _saved.Body;
        Error = "";
        Status = "Changes discarded.";
        RefreshDraftState();
    }

    private void RefreshDraftState()
    {
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        SaveCommand.Cancel();
    }
}

// This is a logical web View, not a Runic Window or a second model owner.
public sealed partial class EditorWebView : RunicView<EditorViewModel>;
