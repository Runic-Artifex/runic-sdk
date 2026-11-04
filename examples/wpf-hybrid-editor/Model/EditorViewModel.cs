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
    // The edited values against the last saved note: IsDirty, Accept after a save, Revert on discard.
    private readonly RunicEditSession<(string Title, string Body)> _session;
    private Note _saved = new(0, "", "");
    private bool _disposed;

    public EditorViewModel(INoteStore store, AppRegions regions)
    {
        _store = store;
        _session = new(this, () => (Title, Body), saved => (Title, Body) = saved);
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(_session.IsDirty)) return;
            OnPropertyChanged(nameof(IsDirty));
            SaveCommand.NotifyCanExecuteChanged();
        };
        _leave = LeaveConfirmation.InDialog(regions.Dialog,
            () => NavigationTarget.Create<ConfirmViewModel, string>("Discard unsaved changes?"),
            () => IsDirty, Discard);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string title = "";

    [ObservableProperty]
    private string body = "";

    public bool IsDirty => _session.IsDirty;
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
            // Edits typed while the save ran remain dirty after it.
            _session.Accept((submitted.Title, submitted.Body));
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
        _session.Accept();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken token) =>
        // Keep the note alive until its save completes or the user cancels it.
        SaveCommand.IsRunning ? ValueTask.FromResult(false) : _leave.CanDepartAsync(departure, token);

    private void Discard()
    {
        _session.Revert();
        Error = "";
        Status = "Changes discarded.";
    }

    public void Dispose()
    {
        _disposed = true;
        SaveCommand.Cancel();
        _session.Dispose();
    }
}

// This is a logical web View, not a Runic Window or a second model owner.
public sealed partial class EditorWebView : RunicView<EditorViewModel>;
