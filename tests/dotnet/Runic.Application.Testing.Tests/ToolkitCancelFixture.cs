using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Application.Testing.Tests;

public sealed partial class ToolkitCancelViewModel : ObservableObject
{
    public int CancelledCount { get; private set; }
    public string? ExportInput { get; private set; }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task SaveAsync(CancellationToken token) => WaitAsync(token);

    [RelayCommand(IncludeCancelCommand = true)]
    private Task OnExportAsync(string input, CancellationToken token)
    {
        ExportInput = input;
        return WaitAsync(token);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private static Task BackgroundAsync(CancellationToken token) => Task.Delay(Timeout.InfiniteTimeSpan, token);

    private async Task WaitAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            CancelledCount++;
            OnPropertyChanged(nameof(CancelledCount));
            throw;
        }
    }
}

public sealed partial class ToolkitCancelWindow(ToolkitCancelViewModel model)
    : Runic.Application.Views.RunicWindow<ToolkitCancelViewModel>(model);
