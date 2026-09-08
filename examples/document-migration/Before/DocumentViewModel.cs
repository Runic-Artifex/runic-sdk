using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentMigration.Domain;
namespace DocumentMigration.Before;

// Extracted MAUI page orchestration: Editor.Text binding and AsyncRelayCommand buttons.
// It is intentionally portable; this project does not assert MAUI workload/native coverage.
public sealed partial class DocumentViewModel(DocumentService service) : ObservableObject
{
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private bool cleanupFailed;
    private string _saved = "";
    private long _revision;
    public bool IsDirty => Text != _saved;
    public bool CanClose(bool discard) => !OpenCommand.IsRunning && !SaveCommand.IsRunning && (!IsDirty || discard);
    partial void OnTextChanged(string value) { _revision++; OnPropertyChanged(nameof(IsDirty)); }
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task OpenAsync(CancellationToken token)
    {
        if (IsDirty || SaveCommand.IsRunning) { Status = "Resolve unsaved changes first"; return; }
        var revision = _revision;
        try
        {
            var result = await service.OpenAsync(token); Status = result.Status; CleanupFailed = result.CleanupFailed;
            if (result.Status == "opened" && revision == _revision) { Text = result.Text!; _saved = Text; OnPropertyChanged(nameof(IsDirty)); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Status = "cancelled"; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { Status = "invalid-or-inaccessible-document"; }
    }
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SaveAsync(CancellationToken token)
    {
        if (OpenCommand.IsRunning) return;
        var captured = Text;
        try
        {
            var result = await service.SaveAsync(captured, token); Status = result.Status; CleanupFailed = result.CleanupFailed;
            if (result.Status == "saved") { _saved = captured; OnPropertyChanged(nameof(IsDirty)); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Status = "cancelled"; }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { Status = "invalid-or-inaccessible-document"; }
    }
}
